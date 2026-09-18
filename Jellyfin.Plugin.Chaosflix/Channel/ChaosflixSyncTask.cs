using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Chaosflix.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaosflix.Channel;

/// <summary>
/// Scheduled task that pre-warms the CCC API cache by refreshing the conference list and
/// the most recent conferences in the background. Entries are replaced one at a time, so
/// cached event details and resolved CDN redirects survive a run.
/// </summary>
public class ChaosflixSyncTask : IScheduledTask
{
    /// <summary>
    /// Interval between runs. <see cref="CccApiClient.ConferenceCacheTtl"/> outlives it, so the
    /// data this task caches is still there when the next run becomes due.
    /// </summary>
    public static readonly TimeSpan SyncInterval = TimeSpan.FromHours(6);

    private readonly CccApiClient _apiClient;
    private readonly ILogger<ChaosflixSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChaosflixSyncTask"/> class.
    /// </summary>
    public ChaosflixSyncTask(CccApiClient apiClient, ILogger<ChaosflixSyncTask> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Chaosflix: Sync CCC Media";

    /// <inheritdoc />
    public string Key => "ChaosflixSync";

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

        var conferences = await _apiClient.RefreshConferencesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Chaosflix sync: found {Count} conferences", conferences.Count);
        progress.Report(10);

        // Pre-warm the 20 most recent conferences (they're the most likely to be browsed)
        var recentConferences = conferences
            .Where(c => c.EventLastReleasedAt != null)
            .OrderByDescending(c => c.EventLastReleasedAt)
            .Take(20)
            .ToList();

        var refreshed = 0;
        for (var i = 0; i < recentConferences.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var conf = recentConferences[i];
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

            progress.Report(10 + (90.0 * (i + 1) / recentConferences.Count));
        }

        _logger.LogInformation("Chaosflix sync: completed — {Count} of {Total} conferences cached",
            refreshed, recentConferences.Count);
        progress.Report(100);
    }
}
