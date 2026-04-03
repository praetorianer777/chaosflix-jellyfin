using System;
using System.Collections.Generic;
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
    private const string BaseUrl = "https://api.media.ccc.de/public";

    /// <summary>Conference list changes rarely — cache for 1 hour.</summary>
    private static readonly TimeSpan ConferenceListTtl = TimeSpan.FromHours(1);

    /// <summary>Conference detail (event list) — cache for 30 minutes.</summary>
    private static readonly TimeSpan ConferenceDetailTtl = TimeSpan.FromMinutes(30);

    /// <summary>Event detail with recordings — cache for 15 minutes.</summary>
    private static readonly TimeSpan EventTtl = TimeSpan.FromMinutes(15);

    /// <summary>Search results — cache for 10 minutes.</summary>
    private static readonly TimeSpan SearchTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;
    private readonly ILogger<CccApiClient> _logger;
    private readonly MemoryCache _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CccApiClient"/> class.
    /// </summary>
    public CccApiClient(IHttpClientFactory httpClientFactory, ILogger<CccApiClient> logger)
    {
        _httpClient = httpClientFactory.CreateClient(nameof(CccApiClient));
        _httpClient.BaseAddress = new Uri(BaseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        _logger = logger;
    }

    /// <summary>
    /// Gets all conferences (cached).
    /// </summary>
    public Task<List<CccConference>> GetConferencesAsync(CancellationToken cancellationToken)
    {
        return _cache.GetOrCreateAsync("conferences", ConferenceListTtl, async ct =>
        {
            _logger.LogDebug("Fetching conferences from CCC API");
            var response = await _httpClient
                .GetFromJsonAsync<CccConferencesResponse>("/public/conferences", ct)
                .ConfigureAwait(false);
            return response?.Conferences ?? new List<CccConference>();
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a single conference with its events (cached).
    /// </summary>
    public Task<CccConference?> GetConferenceAsync(string acronym, CancellationToken cancellationToken)
    {
        return _cache.GetOrCreateAsync($"conf:{acronym}", ConferenceDetailTtl, async ct =>
        {
            _logger.LogDebug("Fetching conference {Acronym} from CCC API", acronym);
            return await _httpClient
                .GetFromJsonAsync<CccConference>($"/public/conferences/{acronym}", ct)
                .ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a single event with its recordings (cached).
    /// </summary>
    public Task<CccEvent?> GetEventAsync(string guid, CancellationToken cancellationToken)
    {
        return _cache.GetOrCreateAsync($"event:{guid}", EventTtl, async ct =>
        {
            _logger.LogDebug("Fetching event {Guid} from CCC API", guid);
            return await _httpClient
                .GetFromJsonAsync<CccEvent>($"/public/events/{guid}", ct)
                .ConfigureAwait(false);
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
                .GetFromJsonAsync<CccEventsResponse>($"/public/events/search?q={encoded}", ct)
                .ConfigureAwait(false);
            return response?.Events ?? new List<CccEvent>();
        }, cancellationToken);
    }

    /// <summary>
    /// Clears the entire cache.
    /// </summary>
    public void ClearCache() => _cache.Clear();

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
