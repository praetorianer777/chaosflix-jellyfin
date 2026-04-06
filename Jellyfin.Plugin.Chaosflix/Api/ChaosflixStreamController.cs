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
        [FromQuery] string? language = null)
    {
        var cccEvent = await _apiClient.GetEventAsync(eventGuid, CancellationToken.None).ConfigureAwait(false);
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

        var resolvedUrl = await _apiClient.ResolveRedirectAsync(recording.RecordingUrl, CancellationToken.None)
            .ConfigureAwait(false);

        _logger.LogDebug("Proxying {EventGuid} from {Url}", eventGuid, resolvedUrl);

        // Build upstream request, forwarding Range header if present
        using var upstreamRequest = new HttpRequestMessage(
            HttpContext.Request.Method == "HEAD" ? HttpMethod.Head : HttpMethod.Get,
            resolvedUrl);

        if (Request.Headers.TryGetValue("Range", out var rangeHeader))
        {
            upstreamRequest.Headers.Range = RangeHeaderValue.Parse(rangeHeader.ToString());
        }

        using var upstreamResponse = await _proxyClient.SendAsync(
            upstreamRequest,
            HttpCompletionOption.ResponseHeadersRead,
            HttpContext.RequestAborted).ConfigureAwait(false);

        // Set response status (200 or 206)
        Response.StatusCode = (int)upstreamResponse.StatusCode;

        // Forward relevant headers
        if (upstreamResponse.Content.Headers.ContentLength.HasValue)
        {
            Response.ContentLength = upstreamResponse.Content.Headers.ContentLength.Value;
        }

        Response.Headers["Accept-Ranges"] = "bytes";

        if (upstreamResponse.Content.Headers.ContentType != null)
        {
            Response.ContentType = upstreamResponse.Content.Headers.ContentType.ToString();
        }
        else
        {
            Response.ContentType = recording.MimeType.Contains("mp4", StringComparison.OrdinalIgnoreCase)
                ? "video/mp4" : "video/webm";
        }

        if (upstreamResponse.Content.Headers.ContentRange != null)
        {
            Response.Headers["Content-Range"] = upstreamResponse.Content.Headers.ContentRange.ToString();
        }

        // Stream body (skip for HEAD requests)
        if (HttpContext.Request.Method != "HEAD")
        {
            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(HttpContext.RequestAborted)
                .ConfigureAwait(false);
            await upstreamStream.CopyToAsync(Response.Body, HttpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
