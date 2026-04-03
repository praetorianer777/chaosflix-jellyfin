using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Chaosflix.Api;
using Jellyfin.Plugin.Chaosflix.Api.Models;
using Jellyfin.Plugin.Chaosflix.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaosflix.Channel;

/// <summary>
/// Jellyfin channel that provides CCC media content.
/// </summary>
public class ChaosflixChannel : IChannel, IRequiresMediaInfoCallback, ISupportsLatestMedia
{
    private readonly CccApiClient _apiClient;
    private readonly ILogger<ChaosflixChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChaosflixChannel"/> class.
    /// </summary>
    public ChaosflixChannel(CccApiClient apiClient, ILogger<ChaosflixChannel> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Chaosflix";

    /// <inheritdoc />
    public string Description => "Chaos Computer Club talks from media.ccc.de";

    /// <inheritdoc />
    public string DataVersion => "4";

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
            AutoRefreshLevels = 2
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
            Path = "https://media.ccc.de/images/logo.png",
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
            return await GetRootItems(cancellationToken).ConfigureAwait(false);
        }

        if (query.FolderId.StartsWith("conf:", StringComparison.Ordinal))
        {
            var acronym = query.FolderId.Substring(5);
            return await GetConferenceItems(acronym, cancellationToken).ConfigureAwait(false);
        }

        return new ChannelItemResult();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        _logger.LogDebug("GetChannelItemMediaInfo: {Id}", id);

        var eventGuid = id.StartsWith("event:", StringComparison.Ordinal) ? id.Substring(6) : id;
        var cccEvent = await _apiClient.GetEventAsync(eventGuid, cancellationToken).ConfigureAwait(false);

        if (cccEvent?.Recordings == null)
        {
            return Enumerable.Empty<MediaSourceInfo>();
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return SelectRecordings(cccEvent.Recordings, config);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(ChannelLatestMediaSearch request, CancellationToken cancellationToken)
    {
        var conferences = await _apiClient.GetConferencesAsync(cancellationToken).ConfigureAwait(false);

        // Get the most recently updated conference
        var latest = conferences
            .Where(c => c.EventLastReleasedAt != null)
            .OrderByDescending(c => c.EventLastReleasedAt)
            .FirstOrDefault();

        if (latest == null)
        {
            return Enumerable.Empty<ChannelItemInfo>();
        }

        var conference = await _apiClient.GetConferenceAsync(latest.Acronym, cancellationToken).ConfigureAwait(false);

        return (conference?.Events ?? new List<CccEvent>())
            .OrderByDescending(e => e.ReleaseDate ?? e.Date)
            .Take(20)
            .Select(MapEventToChannelItem);
    }

    private async Task<ChannelItemResult> GetRootItems(CancellationToken cancellationToken)
    {
        var conferences = await _apiClient.GetConferencesAsync(cancellationToken).ConfigureAwait(false);

        var items = conferences
            .Where(c => c.EventLastReleasedAt != null)
            .OrderByDescending(c => c.EventLastReleasedAt)
            .Select(c => new ChannelItemInfo
            {
                Name = c.Title,
                Id = $"conf:{c.Acronym}",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                ImageUrl = c.LogoUrl,
                Overview = c.Description,
                DateCreated = c.EventLastReleasedAt?.DateTime
            })
            .ToList();

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

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

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

    private static ChannelItemInfo MapEventToChannelItem(CccEvent e)
    {
        var info = new ChannelItemInfo
        {
            Name = e.Title,
            Id = $"event:{e.Guid}",
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Clip,
            ImageUrl = e.PosterUrl ?? e.ThumbUrl,
            Overview = e.Description,
            RunTimeTicks = (long)e.Duration * TimeSpan.TicksPerSecond,
            DateCreated = e.Date?.DateTime ?? e.ReleaseDate?.DateTime,
            CommunityRating = e.ViewCount > 0
                ? Math.Min(10f, (float)Math.Log10(e.ViewCount) * 2)
                : null,
            HomePageUrl = e.FrontendLink,
            OriginalTitle = e.Subtitle
        };

        // Tags as genres
        foreach (var tag in e.Tags.Where(t =>
            !int.TryParse(t, out _) &&
            !t.StartsWith("Stage", StringComparison.OrdinalIgnoreCase) &&
            t.Length > 2))
        {
            info.Genres.Add(tag);
        }

        // Speakers as people
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

    private static List<MediaSourceInfo> SelectRecordings(List<CccRecording> recordings, PluginConfiguration config)
    {
        // Filter to video recordings only
        var videoRecordings = recordings
            .Where(r => r.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (videoRecordings.Count == 0)
        {
            return new List<MediaSourceInfo>();
        }

        // Determine preferred MIME type
        var preferredMime = config.PreferredFormat == VideoFormat.WebM ? "video/webm" : "video/mp4";

        // Score and sort recordings by preference
        var sorted = videoRecordings
            .OrderByDescending(r => r.MimeType.Equals(preferredMime, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
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

        return sorted.Select((r, i) => new MediaSourceInfo
        {
            Id = $"{r.Folder}_{r.Language}",
            Name = FormatRecordingName(r),
            Path = r.RecordingUrl,
            Protocol = MediaProtocol.Http,
            Container = r.MimeType.Contains("webm") ? "webm" : "mp4",
            Size = (long)r.Size * 1024 * 1024,
            IsRemote = true,
            ReadAtNativeFramerate = false,
            SupportsDirectStream = true,
            SupportsDirectPlay = true,
            SupportsTranscoding = true,
            MediaStreams = new List<MediaStream>
            {
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Width = r.Width,
                    Height = r.Height,
                    Codec = r.MimeType.Contains("webm") ? "vp9" : "h264"
                }
            }
        }).ToList();
    }

    private static string FormatRecordingName(CccRecording r)
    {
        var quality = r.HighQuality ? "HD" : "SD";
        var resolution = r.Height > 0 ? $"{r.Width}x{r.Height}" : "?";
        var format = r.MimeType.Contains("webm") ? "WebM" : "MP4";
        return $"{quality} {resolution} ({format}) [{r.Language}]";
    }
}
