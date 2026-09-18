using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Chaosflix.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaosflix.Api;

/// <summary>
/// API controller that acts as a transparent reverse proxy for CCC recordings.
/// Proxies CDN content with full HTTP Range request support, enabling seeking.
/// By serving content through a local endpoint, the Jellyfin server treats it
/// as a non-remote source and can use DirectStream instead of forced transcoding.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ChaosflixStreamController : ControllerBase
{
    private readonly CccApiClient _apiClient;
    private readonly ILogger<ChaosflixStreamController> _logger;
    private static readonly HttpClient _proxyClient = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };

    /// <summary>
    /// Initializes a new instance of the <see cref="ChaosflixStreamController"/> class.
    /// </summary>
    public ChaosflixStreamController(CccApiClient apiClient, ILogger<ChaosflixStreamController> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    /// <summary>
    /// Proxies a CCC recording from the CDN with full Range request support.
    /// This enables seeking in both browser and Android clients.
    /// </summary>
    [HttpGet("proxy/{eventGuid}")]
    [HttpHead("proxy/{eventGuid}")]
    public async Task ProxyStream(
        [FromRoute] string eventGuid,
        [FromQuery] string? recordingFolder = null,
        [FromQuery] string? language = null,
        [FromQuery(Name = ProxySignature.QueryParameter)] string? t = null)
    {
        // The endpoint is anonymous because the server's own ffmpeg fetches this
        // url; the signature is what keeps it from being a general purpose relay.
        if (!ProxySignature.Verify(eventGuid, recordingFolder, language, t))
        {
            _logger.LogWarning("Rejected unsigned proxy request for {EventGuid}", eventGuid);
            Response.StatusCode = 401;
            return;
        }

        var cancellationToken = HttpContext.RequestAborted;

        var cccEvent = await _apiClient.GetEventAsync(eventGuid, cancellationToken).ConfigureAwait(false);
        if (cccEvent?.Recordings == null)
        {
            Response.StatusCode = 404;
            return;
        }

        var videoRecordings = cccEvent.Recordings
            .Where(r => r.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (videoRecordings.Count == 0)
        {
            Response.StatusCode = 404;
            return;
        }

        var recording = videoRecordings.FirstOrDefault(r =>
            (recordingFolder == null || r.Folder.Equals(recordingFolder, StringComparison.OrdinalIgnoreCase)) &&
            (language == null || r.Language.Equals(language, StringComparison.OrdinalIgnoreCase)));

        recording ??= videoRecordings
            .OrderByDescending(r => r.MimeType.Contains("mp4", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(r => r.HighQuality ? 1 : 0)
            .First();

        var originalUrl = recording.RecordingUrl;
        var resolvedUrl = await _apiClient.ResolveRedirectAsync(originalUrl, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug("Proxying {EventGuid} from {Url}", eventGuid, resolvedUrl);

        RangeHeaderValue? range = null;
        if (Request.Headers.TryGetValue("Range", out var rangeHeader)
            && !RangeHeaderValue.TryParse(rangeHeader.ToString(), out range))
        {
            // RFC 9110: a Range header with invalid syntax is ignored, not rejected.
            _logger.LogDebug("Ignoring malformed Range header {Range}", rangeHeader.ToString());
            range = null;
        }

        var method = HttpContext.Request.Method == "HEAD" ? HttpMethod.Head : HttpMethod.Get;

        var upstreamResponse = await SendUpstreamAsync(method, resolvedUrl, range, cancellationToken)
            .ConfigureAwait(false);

        // A cached mirror can die long before its two-hour cache entry expires; drop it
        // and give the CDN one chance to hand out a mirror that still has the recording.
        if (upstreamResponse?.IsSuccessStatusCode != true
            && !string.Equals(resolvedUrl, originalUrl, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Mirror {Url} failed for {EventGuid} ({Status}), retrying via {Original}",
                resolvedUrl,
                eventGuid,
                upstreamResponse?.StatusCode.ToString() ?? "no response",
                originalUrl);

            upstreamResponse?.Dispose();
            _apiClient.InvalidateRedirect(originalUrl);
            upstreamResponse = await SendUpstreamAsync(method, originalUrl, range, cancellationToken)
                .ConfigureAwait(false);
        }

        if (upstreamResponse == null)
        {
            Response.StatusCode = 502;
            return;
        }

        using var finalResponse = upstreamResponse;

        // Set response status (200 or 206)
        Response.StatusCode = (int)finalResponse.StatusCode;

        if (!finalResponse.IsSuccessStatusCode)
        {
            // An error body is not media, so it gets neither Accept-Ranges nor a video type.
            return;
        }

        // Forward relevant headers
        if (finalResponse.Content.Headers.ContentLength.HasValue)
        {
            Response.ContentLength = finalResponse.Content.Headers.ContentLength.Value;
        }

        Response.Headers["Accept-Ranges"] = "bytes";

        if (finalResponse.Content.Headers.ContentType != null)
        {
            Response.ContentType = finalResponse.Content.Headers.ContentType.ToString();
        }
        else
        {
            Response.ContentType = recording.MimeType.Contains("mp4", StringComparison.OrdinalIgnoreCase)
                ? "video/mp4" : "video/webm";
        }

        if (finalResponse.Content.Headers.ContentRange != null)
        {
            Response.Headers["Content-Range"] = finalResponse.Content.Headers.ContentRange.ToString();
        }

        // Stream body (skip for HEAD requests)
        if (HttpContext.Request.Method != "HEAD")
        {
            await using var upstreamStream = await finalResponse.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await upstreamStream.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage?> SendUpstreamAsync(
        HttpMethod method,
        string url,
        RangeHeaderValue? range,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        if (range != null)
        {
            request.Headers.Range = range;
        }

        try
        {
            return await _proxyClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Upstream request to {Url} failed", url);
            return null;
        }
    }
}
