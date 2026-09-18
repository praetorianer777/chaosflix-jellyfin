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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Caching.Memory;
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
    private const string ScopePopular = "popular";
    private const string ScopeRecommended = "recommended";
    private const string ConferenceScopePrefix = "conf-";

    internal const int ProbeCacheCapacity = 128;

    internal static readonly TimeSpan ProbeCacheTtl = TimeSpan.FromHours(6);

    // Jellyfin's ChannelManager keeps what GetChannelItemMediaInfo returned in
    // the shared IMemoryCache under the plain channel item id for five minutes
    // and nothing — not even a full metadata refresh — drops it (#36). We keep
    // the ids we handed out a little longer than that so a configuration change
    // can evict exactly those entries.
    internal static readonly TimeSpan ServedIdRetention = TimeSpan.FromMinutes(10);

    /// <summary>How far the copies outside the conference folders are filed in the past; see <see cref="ScopedDateCreated"/>.</summary>
    internal static readonly TimeSpan NonCanonicalBackdate = TimeSpan.FromDays(365 * 100);

    private readonly CccApiClient _apiClient;
    private readonly ILogger<ChaosflixChannel> _logger;
    private readonly IServerApplicationHost _appHost;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly TimeProvider _timeProvider;
    private readonly IMemoryCache? _mediaSourceCache;
    private readonly ILibraryManager? _libraryManager;
    private readonly ConcurrentDictionary<string, ProbeCacheEntry> _probeCache = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _servedItemIds = new();
    private readonly Lock _subscriptionLock = new();
    private Plugin? _subscribedPlugin;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChaosflixChannel"/> class.
    /// </summary>
    public ChaosflixChannel(
        CccApiClient apiClient,
        ILogger<ChaosflixChannel> logger,
        IServerApplicationHost appHost,
        IMediaEncoder mediaEncoder,
        TimeProvider? timeProvider = null,
        IMemoryCache? mediaSourceCache = null,
        ILibraryManager? libraryManager = null)
    {
        _apiClient = apiClient;
        _logger = logger;
        _appHost = appHost;
        _mediaEncoder = mediaEncoder;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _mediaSourceCache = mediaSourceCache;
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public string Name => "Chaosflix";

    /// <inheritdoc />
    public string Description => "Chaos Computer Club talks from media.ccc.de";

    /// <inheritdoc />
    public string DataVersion => "7";

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

        SubscribeToConfigurationChanges();
        RememberServedId(id);

        var eventGuid = ExtractEventGuid(id);
        var cccEvent = await _apiClient.GetEventAsync(eventGuid, cancellationToken).ConfigureAwait(false);

        if (cccEvent?.Recordings == null)
        {
            return Enumerable.Empty<MediaSourceInfo>();
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var serverUrl = _appHost.GetSmartApiUrl(string.Empty).TrimEnd('/');
        return await SelectRecordingsAsync(cccEvent.Recordings, config, eventGuid, serverUrl, ResolveItemId(id), cancellationToken).ConfigureAwait(false);
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

        var allEvents = new List<(CccEvent Event, string Acronym)>();
        foreach (var conf in recentConferences)
        {
            var detail = await _apiClient.GetConferenceAsync(conf.Acronym, cancellationToken).ConfigureAwait(false);
            if (detail?.Events != null)
            {
                allEvents.AddRange(detail.Events.Select(e => (e, conf.Acronym)));
            }
        }

        // The latest row points at the items the conference folders own, so it
        // does not create a second copy of the same talk.
        return allEvents
            .OrderByDescending(x => x.Event.ReleaseDate ?? x.Event.Date)
            .Take(20)
            .Select(x => MapEventToChannelItem(x.Event, ConferenceScope(x.Acronym)));
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
            .Select(e => MapEventToChannelItem(e, ScopePopular))
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
            .Select(e => MapEventToChannelItem(e, ScopeRecommended))
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
            .Select(e => MapEventToChannelItem(e, ConferenceScope(acronym)))
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
                items.Add(MapEventToChannelItem(related, RelatedScope(eventGuid)));
            }
        }

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    // ── Item ids ─────────────────────────────────────────

    // Jellyfin stores a channel item as one library item with exactly one
    // parent, so a talk listed in several folders under the same id is moved to
    // whichever folder was listed last and vanishes from the others (#15).
    // Every folder therefore hands out its own id for a talk.
    private static string ConferenceScope(string acronym) => $"{ConferenceScopePrefix}{acronym}";

    private static string RelatedScope(string eventGuid) => $"related-{eventGuid}";

    private static bool IsConferenceScope(string scope) => scope.StartsWith(ConferenceScopePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Reads the CCC event guid back out of a channel item id. Accepts the
    /// scoped form <c>event:&lt;scope&gt;:&lt;guid&gt;</c>, the unscoped
    /// <c>event:&lt;guid&gt;</c> and a bare guid.
    /// </summary>
    private static string ExtractEventGuid(string id)
    {
        var separator = id.LastIndexOf(':');
        return separator < 0 ? id : id[(separator + 1)..];
    }

    /// <summary>
    /// Reads the CCC event guid out of a channel item id that the channel handed
    /// out, or returns <see langword="null"/> for anything else.
    /// </summary>
    internal static string? EventGuidOf(string? channelItemId)
    {
        if (string.IsNullOrEmpty(channelItemId) || !channelItemId.StartsWith(PrefixEvent, StringComparison.Ordinal))
        {
            return null;
        }

        var guid = ExtractEventGuid(channelItemId);
        return string.IsNullOrEmpty(guid) ? null : guid;
    }

    // ── Mapping ──────────────────────────────────────────

    /// <summary>
    /// The date a talk is filed under in its folder's copy. "Recently added in
    /// Chaosflix" is a plain library query — measured on 10.11.7 and 12.1 it is
    /// <c>/Users/{id}/Items/Latest?ParentId=&lt;channel&gt;</c> — so it lists every
    /// folder-scoped copy of a talk (#54, #15). Backdating the copies outside the
    /// conference folders keeps them out of any recency-sorted view while the
    /// conference copy, the one <see cref="GetLatestMedia"/> also points at, stays
    /// where it belongs. The shift is constant, so the order inside Popular,
    /// Recommended and Related is untouched. A talk the API gives no date for is
    /// filed at the very bottom instead: left empty, Jellyfin stamps the item
    /// with the moment it was stored, which puts it at the top of the row.
    /// </summary>
    private static DateTime? ScopedDateCreated(DateTime? releasedAt, string scope)
    {
        if (IsConferenceScope(scope))
        {
            return releasedAt;
        }

        if (releasedAt is not DateTime date || date - DateTime.MinValue <= NonCanonicalBackdate)
        {
            return DateTime.MinValue;
        }

        return date - NonCanonicalBackdate;
    }

    private static ChannelItemInfo MapEventToChannelItem(CccEvent e, string scope)
    {
        var info = new ChannelItemInfo
        {
            Name = e.Title,
            Id = $"{PrefixEvent}{scope}:{e.Guid}",
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Clip,
            ImageUrl = e.PosterUrl ?? e.ThumbUrl,
            Overview = BuildOverview(e),
            RunTimeTicks = (long)e.Duration * TimeSpan.TicksPerSecond,
            DateCreated = ScopedDateCreated(e.Date?.DateTime ?? e.ReleaseDate?.DateTime, scope),
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
        Guid? itemId,
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

        // Signed so the proxy only serves recordings this plugin picked (#2).
        var signature = ProxySignature.Create(eventGuid, bestRecording.Folder, bestRecording.Language);

        var proxyUrl = $"{serverUrl}/api/ChaosflixStream/proxy/{eventGuid}"
            + $"?recordingFolder={Uri.EscapeDataString(bestRecording.Folder)}"
            + $"&language={Uri.EscapeDataString(bestRecording.Language)}"
            + $"&{ProxySignature.QueryParameter}={signature}";

        // Probe the actual file to discover the real stream layout.
        // CCC MP4s vary: some have 2 streams (video+audio), some have 3
        // (video+video[visual impaired]+audio). We must declare the correct
        // indices so the server generates proper ffmpeg -map flags.
        // Keyed by the recording that is actually probed, not by the event: which
        // recording wins depends on the configuration, and a configuration change
        // must not hand out the stream layout of the previously chosen file (#3).
        var cacheKey = $"{eventGuid}|{bestRecording.Folder}|{bestRecording.Language}";

        var mediaStreams = await ProbeMediaStreamsAsync(proxyUrl, cacheKey, cancellationToken).ConfigureAwait(false);
        var audioStream = mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);

        var result = new List<MediaSourceInfo>
        {
            new MediaSourceInfo
            {
                Id = (itemId ?? DeterministicGuid($"{bestRecording.RecordingUrl}")).ToString("N"),
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
        var now = _timeProvider.GetUtcNow();
        if (_probeCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Streams;
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

            _probeCache[cacheKey] = new ProbeCacheEntry(streams, _timeProvider.GetUtcNow().Add(ProbeCacheTtl));
            EvictProbeCache();
            return streams;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe {CacheKey}, returning empty streams", cacheKey);
            return new List<MediaStream>();
        }
    }

    private void EvictProbeCache()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var entry in _probeCache)
        {
            if (entry.Value.ExpiresAt <= now)
            {
                _probeCache.TryRemove(entry.Key, out _);
            }
        }

        // All entries share one TTL, so the earliest expiry is the oldest entry.
        while (_probeCache.Count > ProbeCacheCapacity)
        {
            var oldest = _probeCache.OrderBy(e => e.Value.ExpiresAt).Select(e => e.Key).FirstOrDefault();
            if (oldest is null || !_probeCache.TryRemove(oldest, out _))
            {
                break;
            }
        }
    }

    /// <summary>
    /// Drops the media sources Jellyfin cached for every talk this channel has
    /// served, so the next request is answered with the current configuration.
    /// </summary>
    internal void InvalidateCachedMediaSources()
    {
        var count = 0;
        foreach (var id in _servedItemIds.Keys)
        {
            _servedItemIds.TryRemove(id, out _);
            _mediaSourceCache?.Remove(id);
            count++;
        }

        _logger.LogInformation("Configuration changed, dropped {Count} cached media sources", count);
    }

    // The channel is constructed by DI, which may happen before the plugin
    // instance exists; subscribing on first use is both late enough to find it
    // and early enough, because nothing is cached before this call.
    private void SubscribeToConfigurationChanges()
    {
        var plugin = Plugin.Instance;
        if (plugin is null || ReferenceEquals(plugin, _subscribedPlugin))
        {
            return;
        }

        lock (_subscriptionLock)
        {
            if (ReferenceEquals(plugin, _subscribedPlugin))
            {
                return;
            }

            if (_subscribedPlugin is not null)
            {
                _subscribedPlugin.ConfigurationChanged -= OnConfigurationChanged;
            }

            plugin.ConfigurationChanged += OnConfigurationChanged;
            _subscribedPlugin = plugin;
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
        => InvalidateCachedMediaSources();

    private void RememberServedId(string id)
    {
        var now = _timeProvider.GetUtcNow();
        _servedItemIds[id] = now.Add(ServedIdRetention);

        foreach (var entry in _servedItemIds)
        {
            if (entry.Value <= now)
            {
                _servedItemIds.TryRemove(entry.Key, out _);
            }
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
    /// Looks up the Jellyfin item id behind a channel item's external id.
    /// </summary>
    /// <remarks>
    /// The DTO of a channel item carries a placeholder media source whose id is the
    /// item id, because Jellyfin only resolves the real sources in PlaybackInfo and
    /// drops the placeholder there. jellyfin-androidtv takes that placeholder id from
    /// the DTO and sends it back as MediaSourceId, so a source id of our own making
    /// matches nothing, PlaybackInfo answers NoCompatibleStream and the app spins
    /// forever without reporting anything (#55). Handing out the item id is also what
    /// an ordinary library item does.
    /// </remarks>
    private Guid? ResolveItemId(string externalId)
    {
        if (_libraryManager is null)
        {
            return null;
        }

        try
        {
            var ids = _libraryManager.GetItemIds(new InternalItemsQuery { ExternalId = externalId, Limit = 1 });
            return ids.Count > 0 ? ids[0] : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve the item id for {ExternalId}", externalId);
            return null;
        }
    }

    /// <summary>
    /// Creates a deterministic GUID from a string (MD5-based, namespace v3 style).
    /// Jellyfin's streaming pipeline requires MediaSourceInfo.Id to be GUID-parseable.
    /// </summary>
    private static Guid DeterministicGuid(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }

    private sealed record ProbeCacheEntry(List<MediaStream> Streams, DateTimeOffset ExpiresAt);
}
