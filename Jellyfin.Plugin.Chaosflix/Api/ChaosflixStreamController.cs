using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Chaosflix.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaosflix.Api;

/// <summary>
/// API controller that generates a minimal HLS playlist for CCC recordings.
/// This works around the Jellyfin Android client's behavior of using
/// HlsMediaSource for all HTTP DirectPlay URLs.
/// The playlist contains a single segment pointing to the resolved CDN mirror URL.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ChaosflixStreamController : ControllerBase
{
    private readonly CccApiClient _apiClient;
    private readonly ILogger<ChaosflixStreamController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChaosflixStreamController"/> class.
    /// </summary>
    public ChaosflixStreamController(CccApiClient apiClient, ILogger<ChaosflixStreamController> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    /// <summary>
    /// Gets a minimal HLS playlist that wraps a CCC recording URL.
    /// ExoPlayer on Android can parse this as HLS and play the progressive MP4 as a single segment.
    /// </summary>
    /// <param name="eventGuid">The CCC event GUID.</param>
    /// <param name="recordingFolder">The recording folder (e.g. "h264-hd").</param>
    /// <param name="language">The recording language (e.g. "deu").</param>
    /// <returns>An HLS playlist (.m3u8) with one segment.</returns>
    [HttpGet("stream/{eventGuid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetStream(
        [FromRoute] string eventGuid,
        [FromQuery] string? recordingFolder = null,
        [FromQuery] string? language = null)
    {
        var cccEvent = await _apiClient.GetEventAsync(eventGuid, CancellationToken.None).ConfigureAwait(false);
        if (cccEvent?.Recordings == null)
        {
            return NotFound("Event not found");
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        // Find the best matching recording
        var videoRecordings = cccEvent.Recordings
            .Where(r => r.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (videoRecordings.Count == 0)
        {
            return NotFound("No video recordings found");
        }

        var recording = videoRecordings.FirstOrDefault(r =>
            (recordingFolder == null || r.Folder.Equals(recordingFolder, StringComparison.OrdinalIgnoreCase)) &&
            (language == null || r.Language.Equals(language, StringComparison.OrdinalIgnoreCase)));

        // Fallback to best available
        recording ??= videoRecordings
            .OrderByDescending(r => r.MimeType.Contains("mp4", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(r => r.HighQuality ? 1 : 0)
            .First();

        // Resolve CDN redirect to get direct mirror URL
        var resolvedUrl = await _apiClient.ResolveRedirectAsync(recording.RecordingUrl, CancellationToken.None)
            .ConfigureAwait(false);

        var duration = cccEvent.Duration > 0 ? cccEvent.Duration : recording.Length;

        _logger.LogDebug("Generating HLS playlist for {EventGuid}: {Url}", eventGuid, resolvedUrl);

        // Generate minimal HLS VOD playlist with single segment
        var playlist = $"""
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:{duration}
            #EXT-X-MEDIA-SEQUENCE:0
            #EXT-X-PLAYLIST-TYPE:VOD
            #EXTINF:{duration}.000,
            {resolvedUrl}
            #EXT-X-ENDLIST
            """.Replace("            ", "");

        return Content(playlist, "application/vnd.apple.mpegurl");
    }
}
