using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Chaosflix.Api.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaosflix.Api;

/// <summary>
/// HTTP client for the media.ccc.de public API with built-in caching.
/// </summary>
public class CccApiClient : IDisposable
{
    /// <summary>Default public API endpoint; overridable for mirrors and tests.</summary>
    public const string DefaultBaseUrl = "https://api.media.ccc.de/public";

    /// <summary>
    /// Name of the <see cref="IHttpClientFactory"/> client used to resolve CDN redirects.
    /// It must be configured with <c>AllowAutoRedirect = false</c>.
    /// </summary>
    public const string RedirectClientName = "Chaosflix.CdnRedirect";

    private const string ConferencesCacheKey = "conferences";

    /// <summary>
    /// How long the conference list and conference details stay cached.
    /// <see cref="Channel.ChaosflixSyncTask"/> rewrites both on every run, so this only has to
    /// outlive <see cref="Channel.ChaosflixSyncTask.SyncInterval"/>; the extra hour of margin keeps
    /// the pre-warmed data usable when a run is late, skipped or still in progress.
    /// </summary>
    public static readonly TimeSpan ConferenceCacheTtl = TimeSpan.FromHours(7);

    /// <summary>Event detail with recordings — cache for 15 minutes.</summary>
    private static readonly TimeSpan EventTtl = TimeSpan.FromMinutes(15);

    /// <summary>Search results — cache for 10 minutes.</summary>
    private static readonly TimeSpan SearchTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long the configuration page's live check waits. Short on purpose: an endpoint
    /// that needs longer than this is the answer the page is asking for.
    /// </summary>
    private static readonly TimeSpan ReachabilityTimeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpClient _httpClient;
    private readonly ILogger<CccApiClient> _logger;
    private readonly MemoryCache _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CccApiClient"/> class.
    /// </summary>
    public CccApiClient(IHttpClientFactory httpClientFactory, ILogger<CccApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _httpClient = httpClientFactory.CreateClient(nameof(CccApiClient));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        _logger = logger;
    }

    /// <summary>
    /// Gets all conferences (cached).
    /// </summary>
    public Task<List<CccConference>> GetConferencesAsync(CancellationToken cancellationToken)
    {
        return _cache.GetOrCreateAsync(ConferencesCacheKey, ConferenceCacheTtl, async ct =>
        {
            _logger.LogDebug("Fetching conferences from CCC API");
            var response = await _httpClient
                .GetFromJsonAsync<CccConferencesResponse>(Url("/conferences"), ct)
                .ConfigureAwait(false);
            return response?.Conferences ?? new List<CccConference>();
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a single conference with its events (cached).
    /// </summary>
    public Task<CccConference?> GetConferenceAsync(string acronym, CancellationToken cancellationToken)
    {
        return _cache.GetOrCreateAsync(ConferenceCacheKey(acronym), ConferenceCacheTtl, async ct =>
        {
            _logger.LogDebug("Fetching conference {Acronym} from CCC API", acronym);
            return await _httpClient
                .GetFromJsonAsync<CccConference>(Url($"/conferences/{acronym}"), ct)
                .ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>
    /// Refetches the conference list and replaces the cached copy, leaving every other
    /// cache entry (events, CDN redirects) untouched.
    /// </summary>
    public Task<List<CccConference>> RefreshConferencesAsync(CancellationToken cancellationToken)
    {
        _cache.Remove(ConferencesCacheKey);
        return GetConferencesAsync(cancellationToken);
    }

    /// <summary>
    /// Refetches a single conference and replaces the cached copy, leaving every other
    /// cache entry untouched.
    /// </summary>
    public Task<CccConference?> RefreshConferenceAsync(string acronym, CancellationToken cancellationToken)
    {
        _cache.Remove(ConferenceCacheKey(acronym));
        return GetConferenceAsync(acronym, cancellationToken);
    }

    /// <summary>
    /// Gets a single event with its recordings (cached).
    /// </summary>
    public Task<CccEvent?> GetEventAsync(string guid, CancellationToken cancellationToken)
    {
        return _cache.GetOrCreateAsync($"event:{guid}", EventTtl, async ct =>
        {
            _logger.LogDebug("Fetching event {Guid} from CCC API", guid);
            try
            {
                return await _httpClient
                    .GetFromJsonAsync<CccEvent>(Url($"/events/{guid}"), ct)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug("Event {Guid} is unknown to the CCC API", guid);
                return null;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Searches for events (cached).
    /// </summary>
    public Task<List<CccEvent>> SearchEventsAsync(string query, CancellationToken cancellationToken)
    {
        return _cache.GetOrCreateAsync($"search:{query}", SearchTtl, async ct =>
        {
            _logger.LogDebug("Searching CCC API for {Query}", query);
            var encoded = Uri.EscapeDataString(query);
            var response = await _httpClient
                .GetFromJsonAsync<CccEventsResponse>(Url($"/events/search?q={encoded}"), ct)
                .ConfigureAwait(false);
            return response?.Events ?? new List<CccEvent>();
        }, cancellationToken);
    }

    /// <summary>
    /// Gets the endpoint requests actually go to: the configured one, or the public default.
    /// </summary>
    public static string BaseUrl
    {
        get
        {
            var configured = Plugin.Instance?.Configuration.ApiBaseUrl;
            return string.IsNullOrWhiteSpace(configured) ? DefaultBaseUrl : configured.TrimEnd('/');
        }
    }

    private static string Url(string path) => BaseUrl + path;

    /// <summary>
    /// Asks the configured endpoint for the conference list, bypassing the cache, and
    /// reports whether it answered and how long it took. Used by the configuration page.
    /// </summary>
    internal async Task<ReachabilityResult> CheckReachabilityAsync(CancellationToken cancellationToken)
    {
        var url = Url("/conferences");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReachabilityTimeout);

        var started = Stopwatch.GetTimestamp();
        try
        {
            using var response = await _httpClient.GetAsync(url, timeout.Token).ConfigureAwait(false);
            int? conferences = null;
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content
                    .ReadFromJsonAsync<CccConferencesResponse>(timeout.Token).ConfigureAwait(false);
                conferences = payload?.Conferences?.Count ?? 0;
            }

            return new ReachabilityResult(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                Elapsed(started),
                conferences,
                response.IsSuccessStatusCode ? null : response.ReasonPhrase,
                url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var reason = ex is OperationCanceledException
                ? $"no answer within {ReachabilityTimeout.TotalSeconds:N0}s"
                : ex.Message;
            _logger.LogWarning(ex, "Reachability check for {Url} failed", url);
            return new ReachabilityResult(false, null, Elapsed(started), null, reason, url);
        }
    }

    /// <summary>
    /// Counts what the cache currently holds, per kind of entry.
    /// </summary>
    internal CacheSnapshot GetCacheSnapshot() => new(
        _cache.CountLive(),
        _cache.CountLive(key => key == ConferencesCacheKey || key.StartsWith("conf:", StringComparison.Ordinal)),
        _cache.CountLive(key => key.StartsWith("event:", StringComparison.Ordinal)),
        _cache.CountLive(key => key.StartsWith("search:", StringComparison.Ordinal)),
        _cache.CountLive(key => key.StartsWith("redirect:", StringComparison.Ordinal)),
        _cache.Capacity,
        _cache.Hits,
        _cache.Misses);

    private static double Elapsed(long startedAt) =>
        Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 1);

    /// <summary>Outcome of <see cref="CheckReachabilityAsync"/>.</summary>
    internal sealed record ReachabilityResult(
        bool Ok,
        int? StatusCode,
        double ElapsedMs,
        int? ConferenceCount,
        string? Error,
        string Url);

    /// <summary>What the API cache holds right now.</summary>
    internal sealed record CacheSnapshot(
        int Entries,
        int Conferences,
        int Events,
        int Searches,
        int Redirects,
        int Capacity,
        long Hits,
        long Misses);

    /// <summary>
    /// Clears the entire cache.
    /// </summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// Resolves a CDN URL by following redirects and returning the final URL.
    /// CCC CDN (cdn.media.ccc.de) does 302 redirects to mirror servers.
    /// Some clients (e.g. Android ExoPlayer) struggle with cross-domain redirects.
    /// </summary>
    public async Task<string> ResolveRedirectAsync(string url, CancellationToken cancellationToken)
    {
        return await _cache.GetOrCreateAsync(RedirectCacheKey(url), TimeSpan.FromHours(2), async ct =>
        {
            try
            {
                var client = _httpClientFactory.CreateClient(RedirectClientName);
                using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(url));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if ((int)response.StatusCode is >= 301 and <= 308
                    && response.Headers.Location is { } location)
                {
                    var resolved = location.IsAbsoluteUri
                        ? location.ToString()
                        : new Uri(new Uri(url), location).ToString();
                    _logger.LogDebug("Resolved CDN redirect {Url} -> {Resolved}", url, resolved);
                    return resolved;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to resolve redirect for {Url}, using original", url);
            }

            return url;
        }, cancellationToken);
    }

    /// <summary>
    /// Forgets the cached mirror for <paramref name="url"/> so that the next resolution
    /// asks the CDN again. Used when the cached mirror stopped serving the recording.
    /// </summary>
    public void InvalidateRedirect(string url) => _cache.Remove(RedirectCacheKey(url));

    private static string ConferenceCacheKey(string acronym) => $"conf:{acronym}";

    private static string RedirectCacheKey(string url) => $"redirect:{url}";

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
