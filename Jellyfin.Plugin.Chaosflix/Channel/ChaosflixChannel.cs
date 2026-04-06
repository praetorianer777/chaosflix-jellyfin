using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Chaosflix.Api;
using Jellyfin.Plugin.Chaosflix.Api.Models;
using Jellyfin.Plugin.Chaosflix.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaosflix.Channel;

/// <summary>
/// Jellyfin channel that provides CCC media content.
/// </summary>
public partial class ChaosflixChannel : IChannel, IRequiresMediaInfoCallback, ISupportsLatestMedia
{
    private const string FolderPopular = "virtual:popular";
    private const string FolderBrowseByYear = "virtual:years";
    private const string FolderRecommended = "virtual:recommended";
    private const string PrefixConference = "conf:";
    private const string PrefixEvent = "event:";
    private const string PrefixYear = "year:";
    private const string PrefixRelated = "related:";

    private readonly CccApiClient _apiClient;
    private readonly ILogger<ChaosflixChannel> _logger;
    private readonly IServerApplicationHost _appHost;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ConcurrentDictionary<string, List<MediaStream>> _probeCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ChaosflixChannel"/> class.
    /// </summary>
    public ChaosflixChannel(CccApiClient apiClient, ILogger<ChaosflixChannel> logger, IServerApplicationHost appHost, IMediaEncoder mediaEncoder)
    {
        _apiClient = apiClient;
        _logger = logger;
        _appHost = appHost;
        _mediaEncoder = mediaEncoder;
    }

    /// <inheritdoc />
    public string Name => "Chaosflix";

    /// <inheritdoc />
    public string Description => "Chaos Computer Club talks from media.ccc.de";

    /// <inheritdoc />
    public string DataVersion => "6";

    /// <inheritdoc />
    public string HomePageUrl => "https://media.ccc.de";

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
            ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Clip },
            MaxPageSize = 100,
            DefaultSortFields = new List<ChannelItemSortField>
            {
                ChannelItemSortField.DateCreated,
                ChannelItemSortField.Name,
                ChannelItemSortField.CommunityRating
            },
            SupportsContentDownloading = true,
            SupportsSortOrderToggle = true,
            AutoRefreshLevels = 3
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => true;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages()
    {
        return new[] { ImageType.Primary, ImageType.Thumb };
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        return Task.FromResult(new DynamicImageResponse
        {
            Path = "https://raw.githubusercontent.com/praetorianer777/chaosflix-jellyfin/main/assets/logo.svg",
            Protocol = MediaProtocol.Http,
            HasImage = true
        });
    }

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        _logger.LogDebug("GetChannelItems: FolderId={FolderId}", query.FolderId);

        if (string.IsNullOrEmpty(query.FolderId))
        {
            return GetRootItems();
        }

        return query.FolderId switch
        {
            FolderPopular => await GetPopularItems(cancellationToken).ConfigureAwait(false),
            FolderBrowseByYear => await GetYearFolders(cancellationToken).ConfigureAwait(false),
            FolderRecommended => await GetRecommendedItems(cancellationToken).ConfigureAwait(false),
            _ when query.FolderId.StartsWith(PrefixYear, StringComparison.Ordinal)
                => await GetConferencesByYear(query.FolderId[5..], cancellationToken).ConfigureAwait(false),
            _ when query.FolderId.StartsWith(PrefixConference, StringComparison.Ordinal)
                => await GetConferenceItems(query.FolderId[5..], cancellationToken).ConfigureAwait(false),
            _ when query.FolderId.StartsWith(PrefixRelated, StringComparison.Ordinal)
                => await GetRelatedItems(query.FolderId[8..], cancellationToken).ConfigureAwait(false),
            _ => new ChannelItemResult()
        };
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        _logger.LogDebug("GetChannelItemMediaInfo: {Id}", id);

        var eventGuid = id.StartsWith(PrefixEvent, StringComparison.Ordinal) ? id[6..] : id;
        var cccEvent = await _apiClient.GetEventAsync(eventGuid, cancellationToken).ConfigureAwait(false);

        if (cccEvent?.Recordings == null)
        {
            return Enumerable.Empty<MediaSourceInfo>();
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var serverUrl = _appHost.GetSmartApiUrl(string.Empty).TrimEnd('/');
        return await SelectRecordingsAsync(cccEvent.Recordings, config, eventGuid, serverUrl, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(ChannelLatestMediaSearch request, CancellationToken cancellationToken)
    {
        var conferences = await _apiClient.GetConferencesAsync(cancellationToken).ConfigureAwait(false);

        // Get the 3 most recently updated conferences for a broader latest view
        var recentConferences = conferences
            .Where(c => c.EventLastReleasedAt != null)
            .OrderByDescending(c => c.EventLastReleasedAt)
            .Take(3)
            .ToList();

        var allEvents = new List<CccEvent>();
        foreach (var conf in recentConferences)
        {
            var detail = await _apiClient.GetConferenceAsync(conf.Acronym, cancellationToken).ConfigureAwait(false);
            if (detail?.Events != null)
            {
                allEvents.AddRange(detail.Events);
            }
        }

        return allEvents
            .OrderByDescending(e => e.ReleaseDate ?? e.Date)
            .Take(20)
            .Select(MapEventToChannelItem);
    }

    // ── Root ──────────────────────────────────────────────

    private static ChannelItemResult GetRootItems()
    {
        var items = new List<ChannelItemInfo>
        {
            new ChannelItemInfo
            {
                Name = "🔥 Popular Talks",
                Id = FolderPopular,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "Most viewed talks across all conferences"
            },
            new ChannelItemInfo
            {
                Name = "⭐ Recommended",
                Id = FolderRecommended,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "Highly rated recent talks"
            },
            new ChannelItemInfo
            {
                Name = "📅 Browse by Year",
                Id = FolderBrowseByYear,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "All conferences grouped by year"
            }
        };

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

    // ── Popular ──────────────────────────────────────────

    private async Task<ChannelItemResult> GetPopularItems(CancellationToken cancellationToken)
    {
        var conferences = await _apiClient.GetConferencesAsync(cancellationToken).ConfigureAwait(false);

        // Fetch events from the 5 most recent conferences
        var recentConferences = conferences
            .Where(c => c.EventLastReleasedAt != null)
            .OrderByDescending(c => c.EventLastReleasedAt)
            .Take(5)
            .ToList();

        var allEvents = new List<CccEvent>();
        foreach (var conf in recentConferences)
        {
            var detail = await _apiClient.GetConferenceAsync(conf.Acronym, cancellationToken).ConfigureAwait(false);
            if (detail?.Events != null)
            {
                allEvents.AddRange(detail.Events);
            }
        }

        var items = allEvents
            .OrderByDescending(e => e.ViewCount)
            .Take(50)
            .Select(MapEventToChannelItem)
            .ToList();

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    // ── Recommended (high views + recent) ────────────────

    private async Task<ChannelItemResult> GetRecommendedItems(CancellationToken cancellationToken)
    {
        var conferences = await _apiClient.GetConferencesAsync(cancellationToken).ConfigureAwait(false);

        var recentConferences = conferences
            .Where(c => c.EventLastReleasedAt != null)
            .OrderByDescending(c => c.EventLastReleasedAt)
            .Take(3)
            .ToList();

        var allEvents = new List<CccEvent>();
        foreach (var conf in recentConferences)
        {
            var detail = await _apiClient.GetConferenceAsync(conf.Acronym, cancellationToken).ConfigureAwait(false);
            if (detail?.Events != null)
            {
                allEvents.AddRange(detail.Events);
            }
        }

        // Score: views * recency boost
        var now = DateTimeOffset.UtcNow;
        var items = allEvents
            .Where(e => e.ViewCount > 100)
            .OrderByDescending(e =>
            {
                var ageDays = Math.Max(1, (now - (e.ReleaseDate ?? e.Date ?? now)).TotalDays);
                return e.ViewCount / Math.Sqrt(ageDays);
            })
            .Take(30)
            .Select(MapEventToChannelItem)
            .ToList();

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    // ── Year grouping ────────────────────────────────────

    private async Task<ChannelItemResult> GetYearFolders(CancellationToken cancellationToken)
    {
        var conferences = await _apiClient.GetConferencesAsync(cancellationToken).ConfigureAwait(false);

        var years = conferences
            .Where(c => c.EventLastReleasedAt != null)
            .Select(c => c.EventLastReleasedAt!.Value.Year)
            .Distinct()
            .OrderByDescending(y => y)
            .Select(y => new ChannelItemInfo
            {
                Name = y.ToString(CultureInfo.InvariantCulture),
                Id = $"{PrefixYear}{y}",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container
            })
            .ToList();

        return new ChannelItemResult { Items = years, TotalRecordCount = years.Count };
    }

    private async Task<ChannelItemResult> GetConferencesByYear(string yearStr, CancellationToken cancellationToken)
    {
        if (!int.TryParse(yearStr, out var year))
        {
            return new ChannelItemResult();
        }

        var conferences = await _apiClient.GetConferencesAsync(cancellationToken).ConfigureAwait(false);

        var items = conferences
            .Where(c => c.EventLastReleasedAt != null && c.EventLastReleasedAt.Value.Year == year)
            .OrderByDescending(c => c.EventLastReleasedAt)
            .Select(c => new ChannelItemInfo
            {
                Name = c.Title,
                Id = $"{PrefixConference}{c.Acronym}",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                ImageUrl = c.LogoUrl,
                Overview = c.Description,
                DateCreated = c.EventLastReleasedAt?.DateTime
            })
            .ToList();

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    // ── Conference events ────────────────────────────────

    private async Task<ChannelItemResult> GetConferenceItems(string acronym, CancellationToken cancellationToken)
    {
        var conference = await _apiClient.GetConferenceAsync(acronym, cancellationToken).ConfigureAwait(false);
        if (conference?.Events == null)
        {
            return new ChannelItemResult();
        }

        var items = conference.Events
            .OrderByDescending(e => e.Date)
            .Select(MapEventToChannelItem)
            .ToList();

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    // ── Related talks ────────────────────────────────────

    private async Task<ChannelItemResult> GetRelatedItems(string eventGuid, CancellationToken cancellationToken)
    {
        var cccEvent = await _apiClient.GetEventAsync(eventGuid, cancellationToken).ConfigureAwait(false);

        if (cccEvent?.Related == null || cccEvent.Related.Count == 0)
        {
            return new ChannelItemResult();
        }

        var relatedGuids = cccEvent.Related
            .OrderByDescending(r => r.Weight)
            .Take(15)
            .Select(r => r.EventGuid)
            .ToList();

        var items = new List<ChannelItemInfo>();
        foreach (var guid in relatedGuids)
        {
            var related = await _apiClient.GetEventAsync(guid, cancellationToken).ConfigureAwait(false);
            if (related != null)
            {
                items.Add(MapEventToChannelItem(related));
            }
        }

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    // ── Mapping ──────────────────────────────────────────

    private static ChannelItemInfo MapEventToChannelItem(CccEvent e)
    {
        var info = new ChannelItemInfo
        {
            Name = e.Title,
            Id = $"{PrefixEvent}{e.Guid}",
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Clip,
            ImageUrl = e.PosterUrl ?? e.ThumbUrl,
            Overview = BuildOverview(e),
            RunTimeTicks = (long)e.Duration * TimeSpan.TicksPerSecond,
            DateCreated = e.Date?.DateTime ?? e.ReleaseDate?.DateTime,
            CommunityRating = e.ViewCount > 0
                ? Math.Min(10f, (float)Math.Log10(e.ViewCount) * 2)
                : null,
            HomePageUrl = e.FrontendLink,
            OriginalTitle = e.Subtitle,
            SeriesName = e.ConferenceTitle
        };

        // Tags as genres (filter noise)
        foreach (var tag in e.Tags.Where(t =>
            !int.TryParse(t, out _) &&
            !t.StartsWith("Stage", StringComparison.OrdinalIgnoreCase) &&
            !YearPattern().IsMatch(t) &&
            t.Length > 2))
        {
            info.Genres.Add(tag);
        }

        // Speakers
        foreach (var person in e.Persons)
        {
            info.People.Add(new MediaBrowser.Controller.Entities.PersonInfo
            {
                Name = person,
                Type = Jellyfin.Data.Enums.PersonKind.Actor
            });
        }

        return info;
    }

    private static string BuildOverview(CccEvent e)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(e.ConferenceTitle))
        {
            parts.Add(e.ConferenceTitle);
        }

        if (e.Persons.Count > 0)
        {
            parts.Add($"Speaker: {string.Join(", ", e.Persons)}");
        }

        if (e.ViewCount > 0)
        {
            parts.Add($"{e.ViewCount:N0} views");
        }

        if (!string.IsNullOrEmpty(e.OriginalLanguage))
        {
            parts.Add($"Language: {e.OriginalLanguage}");
        }

        var header = string.Join(" · ", parts);
        var desc = e.Description ?? string.Empty;

        // Add "Related Talks" hint if event has related content
        if (e.Related != null && e.Related.Count > 0)
        {
            desc += $"\n\n→ {e.Related.Count} related talks available";
        }

        return string.IsNullOrEmpty(header) ? desc : $"{header}\n\n{desc}";
    }

    // ── Recording selection ──────────────────────────────

    private async Task<List<MediaSourceInfo>> SelectRecordingsAsync(
        List<CccRecording> recordings,
        PluginConfiguration config,
        string eventGuid,
        string serverUrl,
        CancellationToken cancellationToken)
    {
        var videoRecordings = recordings
            .Where(r => r.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (videoRecordings.Count == 0)
        {
            return new List<MediaSourceInfo>();
        }

        var preferredMime = config.PreferredFormat == VideoFormat.WebM ? "video/webm" : "video/mp4";

        var sorted = videoRecordings
            .OrderByDescending(r =>
            {
                if (IsAv1(r)) return -1;
                return r.MimeType.StartsWith(preferredMime, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            })
            .ThenByDescending(r =>
                (config.PreferredQuality == VideoQuality.High && r.HighQuality) ||
                (config.PreferredQuality == VideoQuality.Standard && !r.HighQuality) ? 1 : 0)
            .ThenByDescending(r =>
            {
                if (string.IsNullOrEmpty(config.PreferredLanguage))
                {
                    return 0;
                }

                return r.Language.Contains(config.PreferredLanguage, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            })
            .ThenByDescending(r => r.Width)
            .ToList();

        var bestRecording = sorted[0];

        var proxyUrl = $"{serverUrl}/api/ChaosflixStream/proxy/{eventGuid}"
            + $"?recordingFolder={Uri.EscapeDataString(bestRecording.Folder)}"
            + $"&language={Uri.EscapeDataString(bestRecording.Language)}";

        // Probe the actual file to discover the real stream layout.
        // CCC MP4s vary: some have 2 streams (video+audio), some have 3
        // (video+video[visual impaired]+audio). We must declare the correct
        // indices so the server generates proper ffmpeg -map flags.
        var mediaStreams = await ProbeMediaStreamsAsync(proxyUrl, eventGuid, cancellationToken).ConfigureAwait(false);
        var audioStream = mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);

        var result = new List<MediaSourceInfo>
        {
            new MediaSourceInfo
            {
                Id = DeterministicGuid($"{bestRecording.RecordingUrl}").ToString("N"),
                Name = FormatRecordingName(bestRecording),
                Path = proxyUrl,
                Protocol = MediaProtocol.Http,
                Container = DetectContainer(bestRecording),
                Size = (long)bestRecording.Size * 1024 * 1024,
                RunTimeTicks = (long)bestRecording.Length * TimeSpan.TicksPerSecond,
                Bitrate = bestRecording.Length > 0 ? (int)((long)bestRecording.Size * 1024 * 1024 * 8 / bestRecording.Length) : null,
                VideoType = VideoType.VideoFile,
                DefaultAudioStreamIndex = audioStream?.Index,
                IsRemote = false,
                ReadAtNativeFramerate = false,
                SupportsProbing = false,
                SupportsDirectPlay = false,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                MediaStreams = mediaStreams
            }
        };

        return result;
    }

    private async Task<List<MediaStream>> ProbeMediaStreamsAsync(
        string proxyUrl, string cacheKey, CancellationToken cancellationToken)
    {
        if (_probeCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            _logger.LogInformation("Probing media streams for {CacheKey} via {Url}", cacheKey, proxyUrl);
            var request = new MediaInfoRequest
            {
                MediaSource = new MediaSourceInfo
                {
                    Path = proxyUrl,
                    Protocol = MediaProtocol.Http
                },
                MediaType = DlnaProfileType.Video
            };

            var info = await _mediaEncoder.GetMediaInfo(request, cancellationToken).ConfigureAwait(false);
            var streams = info.MediaStreams?.ToList() ?? new List<MediaStream>();

            _logger.LogInformation("Probed {Count} streams for {CacheKey}: {Types}",
                streams.Count, cacheKey,
                string.Join(", ", streams.Select(s => $"{s.Type}@{s.Index}")));

            _probeCache.TryAdd(cacheKey, streams);
            return streams;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe {CacheKey}, returning empty streams", cacheKey);
            return new List<MediaStream>();
        }
    }

    private static bool IsAv1(CccRecording r) =>
        r.MimeType.Contains("av01", StringComparison.OrdinalIgnoreCase) ||
        r.Folder.StartsWith("av1", StringComparison.OrdinalIgnoreCase);

    private static string DetectContainer(CccRecording r) =>
        r.MimeType.Contains("mp4", StringComparison.OrdinalIgnoreCase) ? "mp4" : "webm";

    private static string DetectVideoCodec(CccRecording r)
    {
        if (r.MimeType.Contains("mp4", StringComparison.OrdinalIgnoreCase))
            return "h264";
        if (IsAv1(r))
            return "av1";
        return "vp9";
    }

    private static string DetectAudioCodec(CccRecording r) =>
        r.MimeType.Contains("mp4", StringComparison.OrdinalIgnoreCase) ? "aac" : "opus";

    private static string FormatRecordingName(CccRecording r)
    {
        var quality = r.HighQuality ? "HD" : "SD";
        var resolution = r.Height > 0 ? $"{r.Width}x{r.Height}" : "?";
        var format = IsAv1(r) ? "AV1" : DetectContainer(r).ToUpperInvariant();
        return $"{quality} {resolution} ({format}) [{r.Language}]";
    }

    [GeneratedRegex(@"^\d{4}c\d$|^\d{4}$")]
    private static partial Regex YearPattern();

    /// <summary>
    /// Creates a deterministic GUID from a string (MD5-based, namespace v3 style).
    /// Jellyfin's streaming pipeline requires MediaSourceInfo.Id to be GUID-parseable.
    /// </summary>
    private static Guid DeterministicGuid(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }
}
