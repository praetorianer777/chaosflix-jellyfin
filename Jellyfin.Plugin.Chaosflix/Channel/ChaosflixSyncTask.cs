using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Chaosflix.Api;
using Jellyfin.Plugin.Chaosflix.Api.Models;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaosflix.Channel;

/// <summary>
/// Scheduled task that refreshes the CCC API cache for the conferences the user follows
/// and then walks them once, so Jellyfin stores their talks as library items. Entries are
/// replaced one at a time, so cached event details and resolved CDN redirects survive a run.
///
/// The walk is what makes a talk findable by name: Jellyfin creates a channel item the
/// first time a client asks for the folder holding it and never before (measured — neither
/// its own channel refresh nor a library scan descends into channel content), so without
/// this a talk is only searchable once somebody has clicked their way to it (#8).
/// </summary>
public class ChaosflixSyncTask : IScheduledTask
{
    /// <summary>
    /// Interval between runs. <see cref="CccApiClient.ConferenceCacheTtl"/> outlives it, so the
    /// data this task caches is still there when the next run becomes due.
    /// </summary>
    public static readonly TimeSpan SyncInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// Identifier the server files this task under; the status endpoint looks it up by it.
    /// </summary>
    public const string TaskKey = "ChaosflixSync";

    /// <summary>
    /// How many conferences are refreshed and walked when the user has named none. The
    /// whole catalogue is several hundred conferences, and expanding every one of them
    /// four times a day would be a poor way to treat an API the CCC runs on donated time.
    /// Naming conferences in the settings lifts this: then exactly those are walked (#86).
    /// </summary>
    internal const int DefaultConferenceCount = 20;

    private readonly CccApiClient _apiClient;
    private readonly IChannelManager _channelManager;
    private readonly ILogger<ChaosflixSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChaosflixSyncTask"/> class.
    /// </summary>
    public ChaosflixSyncTask(
        CccApiClient apiClient, IChannelManager channelManager, ILogger<ChaosflixSyncTask> logger)
    {
        _apiClient = apiClient;
        _channelManager = channelManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Chaosflix: Sync CCC Media";

    /// <inheritdoc />
    public string Key => TaskKey;

    /// <inheritdoc />
    public string Description => "Refreshes the conference and talk cache from media.ccc.de";

    /// <inheritdoc />
    public string Category => "Chaosflix";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = SyncInterval.Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Chaosflix sync: starting cache refresh");
        progress.Report(0);

        var selected = await RefreshConferencesAsync(progress, cancellationToken).ConfigureAwait(false);
        await IngestAsync(selected, progress, cancellationToken).ConfigureAwait(false);

        progress.Report(100);
    }

    private async Task<List<CccConference>> RefreshConferencesAsync(
        IProgress<double> progress, CancellationToken cancellationToken)
    {
        var all = await _apiClient.RefreshConferencesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Chaosflix sync: found {Count} conferences", all.Count);
        progress.Report(5);

        var filter = Plugin.Instance?.Configuration.ConferenceFilter;
        var selected = ChaosflixChannel.SelectConferences(all, filter)
            .Where(c => c.EventLastReleasedAt != null)
            .OrderByDescending(c => c.EventLastReleasedAt)
            .ToList();

        if (string.IsNullOrWhiteSpace(filter))
        {
            selected = selected.Take(DefaultConferenceCount).ToList();
        }

        var refreshed = 0;
        for (var i = 0; i < selected.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var conf = selected[i];
            try
            {
                var detail = await _apiClient.RefreshConferenceAsync(conf.Acronym, cancellationToken).ConfigureAwait(false);
                refreshed++;
                _logger.LogDebug("Chaosflix sync: cached {Title} ({EventCount} events)",
                    conf.Title, detail?.Events?.Count ?? 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Chaosflix sync: failed to fetch {Acronym}", conf.Acronym);
            }

            progress.Report(5 + (55.0 * (i + 1) / selected.Count));
        }

        _logger.LogInformation("Chaosflix sync: completed — {Count} of {Total} conferences cached",
            refreshed, selected.Count);
        return selected;
    }

    /// <summary>
    /// Asks Jellyfin for the children of every selected conference folder, which is what
    /// makes it store their talks. The walk goes top down because a folder can only be
    /// addressed by the item id Jellyfin gave it, and that id does not exist until its
    /// parent has been listed once.
    /// </summary>
    private async Task IngestAsync(
        List<CccConference> conferences, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (conferences.Count == 0)
        {
            return;
        }

        var channels = await _channelManager.GetChannelsInternalAsync(new ChannelQuery()).ConfigureAwait(false);
        var channel = channels.Items
            .FirstOrDefault(c => string.Equals(c.Name, ChaosflixChannel.ChannelName, StringComparison.Ordinal));

        if (channel == null)
        {
            _logger.LogDebug("Chaosflix sync: the channel is not registered yet, nothing to ingest");
            return;
        }

        var wanted = conferences
            .Select(c => ChaosflixChannel.ConferenceFolderId(c.Acronym))
            .ToHashSet(StringComparer.Ordinal);

        var root = await ChildrenAsync(channel.Id, Guid.Empty, cancellationToken).ConfigureAwait(false);
        var years = root.FirstOrDefault(i =>
            string.Equals(i.ExternalId, ChaosflixChannel.FolderBrowseByYear, StringComparison.Ordinal));

        if (years == null)
        {
            _logger.LogWarning("Chaosflix sync: the channel root has no year folder, skipping the walk");
            return;
        }

        var yearFolders = await ChildrenAsync(channel.Id, years.Id, cancellationToken).ConfigureAwait(false);

        var walked = 0;
        foreach (var yearFolder in yearFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var conferenceFolders = await ChildrenAsync(channel.Id, yearFolder.Id, cancellationToken)
                .ConfigureAwait(false);

            foreach (var folder in conferenceFolders.Where(f => wanted.Contains(f.ExternalId ?? string.Empty)))
            {
                var talks = await ChildrenAsync(channel.Id, folder.Id, cancellationToken).ConfigureAwait(false);
                walked++;
                _logger.LogDebug("Chaosflix sync: stored {Count} talks of {Conference}",
                    talks.Count, folder.Name);

                progress.Report(60 + (40.0 * walked / conferences.Count));
            }
        }

        _logger.LogInformation("Chaosflix sync: walked {Count} of {Total} conferences into the library",
            walked, conferences.Count);
    }

    private async Task<IReadOnlyList<BaseItem>> ChildrenAsync(
        Guid channelId, Guid parentId, CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery
        {
            ChannelIds = [channelId],
            ParentId = parentId
        };

        var result = await _channelManager
            .GetChannelItemsInternal(query, new Progress<double>(), cancellationToken)
            .ConfigureAwait(false);

        return result.Items;
    }
}
