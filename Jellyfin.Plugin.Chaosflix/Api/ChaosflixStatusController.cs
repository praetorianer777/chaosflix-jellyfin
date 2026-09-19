using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Chaosflix.Channel;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaosflix.Api;

/// <summary>
/// Reports what the plugin currently believes to be true, for its configuration page:
/// whether the CCC API answers, when the sync task last ran and what the caches hold.
/// Nothing here is expensive unless it is asked for explicitly.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = ElevationPolicy)]
public class ChaosflixStatusController : ControllerBase
{
    /// <summary>
    /// Jellyfin's administrator policy. The constant lives in Jellyfin.Api, which plugins
    /// do not reference, so the name is repeated here; the server registers it by name.
    /// </summary>
    private const string ElevationPolicy = "RequiresElevation";

    private static readonly HttpClient _probeClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly CccApiClient _apiClient;
    private readonly ChaosflixChannel _channel;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<ChaosflixStatusController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChaosflixStatusController"/> class.
    /// </summary>
    public ChaosflixStatusController(
        CccApiClient apiClient,
        ChaosflixChannel channel,
        ITaskManager taskManager,
        ILogger<ChaosflixStatusController> logger)
    {
        _apiClient = apiClient;
        _channel = channel;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <summary>
    /// Returns the plugin's state without touching the network.
    /// </summary>
    [HttpGet]
    public ActionResult<PluginStatus> GetStatus() => BuildStatus();

    /// <summary>
    /// Asks the configured endpoint whether it answers, and how fast.
    /// </summary>
    [HttpPost("CheckApi")]
    public async Task<ActionResult<ApiCheck>> CheckApi()
    {
        var result = await _apiClient.CheckReachabilityAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return new ApiCheck(
            result.Ok,
            result.Url,
            result.StatusCode,
            result.ElapsedMs,
            result.ConferenceCount,
            result.Error);
    }

    /// <summary>
    /// Empties every cache the plugin keeps, along the same path a configuration change
    /// takes, and returns the state afterwards.
    /// </summary>
    [HttpPost("ClearCaches")]
    public ActionResult<PluginStatus> ClearCaches()
    {
        var before = BuildStatus();

        _apiClient.ClearCache();
        _channel.ClearProbeCache();
        _channel.InvalidateCachedMediaSources();

        _logger.LogInformation(
            "Caches cleared from the configuration page: {Api} api entries, {Probe} probes",
            before.ApiCache.Entries,
            before.ProbeCache.Entries);

        return BuildStatus();
    }

    /// <summary>
    /// Resolves one talk the whole way the channel does — API lookup, recording choice,
    /// signed proxy url, proxy request, probe — and reports the stage that fails.
    /// </summary>
    /// <param name="id">A talk guid, slug, channel item id or media.ccc.de url.</param>
    [HttpPost("CheckTalk")]
    public async Task<ActionResult<TalkCheck>> CheckTalk([FromQuery] string? id)
    {
        var guid = NormalizeTalkId(id);
        if (string.IsNullOrEmpty(guid))
        {
            return BadRequest("Give a talk guid, slug or media.ccc.de url.");
        }

        var cancellationToken = HttpContext.RequestAborted;
        var stages = new List<TalkCheckStage>();

        var started = Stopwatch.GetTimestamp();
        var cccEvent = await _apiClient.GetEventAsync(guid, cancellationToken).ConfigureAwait(false);
        if (cccEvent?.Recordings is null || cccEvent.Recordings.Count == 0)
        {
            stages.Add(new TalkCheckStage(
                "API lookup",
                false,
                cccEvent is null
                    ? $"{CccApiClient.BaseUrl}/events/{guid} returned no event"
                    : "the event has no recordings",
                Elapsed(started)));
            return Report(guid, null, stages);
        }

        stages.Add(new TalkCheckStage(
            "API lookup",
            true,
            $"{cccEvent.Title} — {cccEvent.Recordings.Count} recordings",
            Elapsed(started)));

        started = Stopwatch.GetTimestamp();
        var sources = (await _channel
            .GetChannelItemMediaInfo($"event:{guid}", cancellationToken)
            .ConfigureAwait(false))
            .ToList();

        if (sources.Count == 0)
        {
            stages.Add(new TalkCheckStage(
                "Recording choice",
                false,
                "no recording survived the configured quality, format and language preferences",
                Elapsed(started)));
            return Report(guid, cccEvent.Title, stages);
        }

        var chosen = sources[0];
        stages.Add(new TalkCheckStage(
            "Recording choice",
            true,
            $"{chosen.Name} ({chosen.Container}), {sources.Count} versions offered",
            Elapsed(started)));

        var proxyUrl = chosen.Path ?? string.Empty;
        var signed = ProxyUrl.IsSignedCorrectly(proxyUrl);
        stages.Add(new TalkCheckStage(
            "Signed proxy url",
            signed,
            signed
                ? ProxyUrl.Redact(proxyUrl)
                : $"the url this plugin just produced does not carry a valid signature: {ProxyUrl.Redact(proxyUrl)}",
            0));

        if (!signed)
        {
            return Report(guid, cccEvent.Title, stages);
        }

        started = Stopwatch.GetTimestamp();
        var proxy = await HeadProxyAsync(proxyUrl, cancellationToken).ConfigureAwait(false);
        stages.Add(proxy with { ElapsedMs = Elapsed(started) });
        if (!proxy.Ok)
        {
            return Report(guid, cccEvent.Title, stages);
        }

        var streams = chosen.MediaStreams ?? new List<MediaStream>();
        stages.Add(new TalkCheckStage(
            "Probe",
            streams.Count > 0,
            streams.Count > 0
                ? string.Join(", ", streams.Select(s => $"{s.Type}@{s.Index} {s.Codec}"))
                : "ffprobe reported no streams for the chosen recording",
            0));

        return Report(guid, cccEvent.Title, stages);
    }

    private static TalkCheck Report(string guid, string? title, List<TalkCheckStage> stages)
    {
        var failed = stages.FirstOrDefault(s => !s.Ok);
        return new TalkCheck(failed is null, guid, title, failed?.Name, stages);
    }

    private async Task<TalkCheckStage> HeadProxyAsync(string proxyUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, proxyUrl);
            using var response = await _probeClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var size = response.Content.Headers.ContentLength;
            var length = size.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"{size.Value / 1048576.0:N1} MiB")
                : "no length announced";

            return new TalkCheckStage(
                "Proxy",
                response.IsSuccessStatusCode,
                response.IsSuccessStatusCode
                    ? $"{(int)response.StatusCode} {response.Content.Headers.ContentType}, {length}"
                    : $"the proxy answered {(int)response.StatusCode} {response.ReasonPhrase}",
                0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Talk check could not reach the proxy");
            return new TalkCheckStage("Proxy", false, ex.Message, 0);
        }
    }

    private PluginStatus BuildStatus()
    {
        var cache = _apiClient.GetCacheSnapshot();
        var probe = _channel.GetProbeCacheStats();

        return new PluginStatus(
            Plugin.Instance?.Version?.ToString() ?? "unknown",
            CccApiClient.BaseUrl,
            string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.ApiBaseUrl),
            SyncStatus(),
            new CacheStatus(
                cache.Entries,
                cache.Capacity,
                cache.Conferences,
                cache.Events,
                cache.Searches,
                cache.Redirects,
                cache.Hits,
                cache.Misses,
                HitRate(cache.Hits, cache.Misses)),
            new ProbeStatus(
                probe.Entries,
                probe.Capacity,
                probe.Hits,
                probe.Misses,
                HitRate(probe.Hits, probe.Misses),
                probe.ServedItems));
    }

    private SyncStatus SyncStatus()
    {
        var worker = _taskManager.ScheduledTasks
            .FirstOrDefault(t => string.Equals(t.ScheduledTask.Key, ChaosflixSyncTask.TaskKey, StringComparison.Ordinal));

        if (worker is null)
        {
            return new SyncStatus(false, null, null, null, null, null);
        }

        var last = worker.LastExecutionResult;
        return new SyncStatus(
            true,
            worker.State.ToString(),
            last?.StartTimeUtc,
            last is null ? null : Math.Round((last.EndTimeUtc - last.StartTimeUtc).TotalSeconds, 1),
            last?.Status.ToString(),
            last?.ErrorMessage);
    }

    private static double? HitRate(long hits, long misses) =>
        hits + misses == 0 ? null : Math.Round(100.0 * hits / (hits + misses), 1);

    private static double Elapsed(long startedAt) =>
        Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 1);

    /// <summary>
    /// Reduces whatever a reader pasted to the identifier the API takes: a bare guid, a
    /// channel item id, or the last segment of a media.ccc.de url.
    /// </summary>
    internal static string NormalizeTalkId(string? id)
    {
        var value = id?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var query = value.IndexOfAny(['?', '#']);
        if (query >= 0)
        {
            value = value[..query];
        }

        value = value.TrimEnd('/');
        var slash = value.LastIndexOf('/');
        if (slash >= 0)
        {
            value = value[(slash + 1)..];
        }

        var colon = value.LastIndexOf(':');
        return colon >= 0 ? value[(colon + 1)..] : value;
    }
}

/// <summary>The plugin's state, as the configuration page shows it.</summary>
/// <param name="Version">Plugin version.</param>
/// <param name="ApiBaseUrl">Endpoint requests go to.</param>
/// <param name="ApiBaseUrlIsDefault">Whether that is the public default rather than a configured mirror.</param>
/// <param name="Sync">The scheduled sync task's last run.</param>
/// <param name="ApiCache">What the CCC API cache holds.</param>
/// <param name="ProbeCache">What the probe cache holds.</param>
public record PluginStatus(
    string Version,
    string ApiBaseUrl,
    bool ApiBaseUrlIsDefault,
    SyncStatus Sync,
    CacheStatus ApiCache,
    ProbeStatus ProbeCache);

/// <summary>The sync task's last run.</summary>
/// <param name="Registered">Whether the server knows the task at all.</param>
/// <param name="State">Idle, running or cancelled.</param>
/// <param name="LastRunUtc">When the last run started.</param>
/// <param name="DurationSeconds">How long it took.</param>
/// <param name="LastResult">Completed, failed, aborted or cancelled.</param>
/// <param name="Error">The failure message, when there was one.</param>
public record SyncStatus(
    bool Registered,
    string? State,
    DateTime? LastRunUtc,
    double? DurationSeconds,
    string? LastResult,
    string? Error);

/// <summary>What the CCC API cache holds.</summary>
/// <param name="Entries">Entries that would still be served.</param>
/// <param name="Capacity">Entries kept before the soonest-expiring ones are evicted.</param>
/// <param name="Conferences">Conference list and conference detail entries.</param>
/// <param name="Events">Talk detail entries.</param>
/// <param name="Searches">Cached searches.</param>
/// <param name="Redirects">Resolved CDN mirrors.</param>
/// <param name="Hits">Reads answered from the cache since the server started.</param>
/// <param name="Misses">Reads that had to call the API.</param>
/// <param name="HitRatePercent">Hits as a percentage, or null when nothing was read yet.</param>
public record CacheStatus(
    int Entries,
    int Capacity,
    int Conferences,
    int Events,
    int Searches,
    int Redirects,
    long Hits,
    long Misses,
    double? HitRatePercent);

/// <summary>What the probe cache holds.</summary>
/// <param name="Entries">Probed stream layouts still valid.</param>
/// <param name="Capacity">Entries kept before the oldest are evicted.</param>
/// <param name="Hits">Playbacks answered from a cached probe.</param>
/// <param name="Misses">Playbacks that had to run ffprobe.</param>
/// <param name="HitRatePercent">Hits as a percentage, or null when nothing was probed yet.</param>
/// <param name="ServedItems">Talks whose media sources a configuration change would invalidate.</param>
public record ProbeStatus(
    int Entries,
    int Capacity,
    long Hits,
    long Misses,
    double? HitRatePercent,
    int ServedItems);

/// <summary>Outcome of the explicit endpoint check.</summary>
/// <param name="Ok">Whether the endpoint answered with a success status.</param>
/// <param name="Url">The url that was asked.</param>
/// <param name="StatusCode">The HTTP status, when there was an answer.</param>
/// <param name="ElapsedMs">Round-trip time.</param>
/// <param name="ConferenceCount">Conferences the endpoint listed.</param>
/// <param name="Error">Why it failed, when it did.</param>
public record ApiCheck(
    bool Ok,
    string Url,
    int? StatusCode,
    double ElapsedMs,
    int? ConferenceCount,
    string? Error);

/// <summary>Outcome of resolving one talk end to end.</summary>
/// <param name="Ok">Whether every stage passed.</param>
/// <param name="TalkId">The identifier that was resolved.</param>
/// <param name="Title">The talk's title, once the API answered.</param>
/// <param name="FailedStage">The first stage that failed.</param>
/// <param name="Stages">Every stage that was reached, in order.</param>
public record TalkCheck(
    bool Ok,
    string TalkId,
    string? Title,
    string? FailedStage,
    IReadOnlyList<TalkCheckStage> Stages);

/// <summary>One stage of the talk check.</summary>
/// <param name="Name">Stage name.</param>
/// <param name="Ok">Whether it passed.</param>
/// <param name="Detail">What it found, or why it failed.</param>
/// <param name="ElapsedMs">How long it took.</param>
public record TalkCheckStage(string Name, bool Ok, string Detail, double ElapsedMs);
