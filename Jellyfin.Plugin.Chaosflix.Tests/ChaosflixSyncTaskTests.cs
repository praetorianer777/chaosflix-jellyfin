using System.Net;
using Jellyfin.Plugin.Chaosflix.Api.Models;
using Jellyfin.Plugin.Chaosflix.Channel;
using Jellyfin.Plugin.Chaosflix.Tests.Fakes;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using static Jellyfin.Plugin.Chaosflix.Tests.Fakes.TestData;

namespace Jellyfin.Plugin.Chaosflix.Tests;

public class ChaosflixSyncTaskTests
{
    private readonly FakeCccApi _api = new();

    [Fact]
    public void RunsEverySixHoursByDefault()
    {
        var trigger = Assert.Single(CreateTask().GetDefaultTriggers());

        Assert.Equal(TaskTriggerInfoType.IntervalTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(6).Ticks, trigger.IntervalTicks);
    }

    [Fact]
    public async Task PrewarmsTwentyMostRecentConferences()
    {
        var confs = Enumerable.Range(1, 25).Select(i => Conference($"c{i}", Day(2000 + i))).ToList();
        confs.Add(Conference("unreleased", null));
        _api.Json("/public/conferences", new CccConferencesResponse { Conferences = confs });
        foreach (var c in confs)
        {
            _api.Json($"/public/conferences/{c.Acronym}", c);
        }

        await CreateTask().ExecuteAsync(new Progress(), CancellationToken.None);

        Assert.Equal(20, _api.Requests.Count(r => r.StartsWith("/public/conferences/", StringComparison.Ordinal)));
        Assert.Equal(1, _api.CountRequests("/public/conferences/c25"));
        Assert.Equal(0, _api.CountRequests("/public/conferences/c5"));
        Assert.Equal(0, _api.CountRequests("/public/conferences/unreleased"));
    }

    [Fact]
    public async Task ClearsCacheBeforeRefreshing()
    {
        _api.Json("/public/conferences", new CccConferencesResponse());
        var client = _api.CreateApiClient();
        await client.GetConferencesAsync(CancellationToken.None);

        await new ChaosflixSyncTask(client, NullLogger<ChaosflixSyncTask>.Instance).ExecuteAsync(new Progress(), CancellationToken.None);

        Assert.Equal(2, _api.CountRequests("/public/conferences"));
    }

    [Fact]
    public async Task FailingConferenceDoesNotAbortRunAndProgressReachesHundred()
    {
        _api.Json("/public/conferences", new CccConferencesResponse
        {
            Conferences = [Conference("ok1", Day(2023)), Conference("broken", Day(2024)), Conference("ok2", Day(2022))]
        });
        _api.Json("/public/conferences/ok1", Conference("ok1", Day(2023)));
        _api.Status("/public/conferences/broken", HttpStatusCode.InternalServerError);
        _api.Json("/public/conferences/ok2", Conference("ok2", Day(2022)));
        var progress = new Progress();

        await CreateTask().ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(1, _api.CountRequests("/public/conferences/ok2"));
        Assert.Equal(0, progress.Values[0]);
        Assert.Equal(100, progress.Values[^1]);
        Assert.Equal(progress.Values.Order(), progress.Values);
    }

    [Fact]
    public async Task HonoursCancellation()
    {
        _api.Json("/public/conferences", new CccConferencesResponse { Conferences = [Conference("c", Day(2024))] });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateTask().ExecuteAsync(new Progress(), cts.Token));
    }

    private ChaosflixSyncTask CreateTask() => new(_api.CreateApiClient(), NullLogger<ChaosflixSyncTask>.Instance);

    // Progress<T> reports on the thread pool; tests need the values synchronously.
    private sealed class Progress : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value) => Values.Add(value);
    }
}
