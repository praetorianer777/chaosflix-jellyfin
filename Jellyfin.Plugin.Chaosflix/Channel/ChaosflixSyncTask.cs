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
/// Scheduled task that pre-warms the CCC API cache by fetching
/// all conferences and their events in the background.
/// </summary>
public class ChaosflixSyncTask : IScheduledTask
{
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
        // Run every 6 hours
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromHours(6).Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Chaosflix sync: starting cache refresh");
        progress.Report(0);

        // Clear existing cache to force fresh data
        _apiClient.ClearCache();

        // Fetch conference list
        var conferences = await _apiClient.GetConferencesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Chaosflix sync: found {Count} conferences", conferences.Count);
        progress.Report(10);

        // Pre-warm the 20 most recent conferences (they're the most likely to be browsed)
        var recentConferences = conferences
            .Where(c => c.EventLastReleasedAt != null)
            .OrderByDescending(c => c.EventLastReleasedAt)
            .Take(20)
            .ToList();

        for (var i = 0; i < recentConferences.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var conf = recentConferences[i];
            try
            {
                var detail = await _apiClient.GetConferenceAsync(conf.Acronym, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Chaosflix sync: cached {Title} ({EventCount} events)",
                    conf.Title, detail?.Events?.Count ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Chaosflix sync: failed to fetch {Acronym}", conf.Acronym);
            }

            progress.Report(10 + (90.0 * (i + 1) / recentConferences.Count));
        }

        _logger.LogInformation("Chaosflix sync: completed — {Count} conferences cached", recentConferences.Count);
        progress.Report(100);
    }
}
