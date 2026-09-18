using System.Net;
using Jellyfin.Plugin.Chaosflix.Api;
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

    // The cache expires against the wall clock, so this checks the TTL contract instead of
    // waiting six hours: whatever the run caches must still be valid when the next run is due.
    [Fact]
    public void ConferenceCacheOutlivesTheIntervalBetweenRuns()
    {
        var trigger = Assert.Single(CreateTask().GetDefaultTriggers());

        Assert.Equal(ChaosflixSyncTask.SyncInterval.Ticks, trigger.IntervalTicks);
        Assert.True(
            CccApiClient.ConferenceCacheTtl > ChaosflixSyncTask.SyncInterval,
            $"conference TTL {CccApiClient.ConferenceCacheTtl} must outlive the {ChaosflixSyncTask.SyncInterval} sync interval");
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
    public async Task PrewarmedConferencesAreServedFromTheCacheAfterwards()
    {
        _api.Json("/public/conferences", new CccConferencesResponse { Conferences = [Conference("c", Day(2024))] });
        _api.Json("/public/conferences/c", Conference("c", Day(2024)));
        var client = _api.CreateApiClient();

        await SyncTask(client).ExecuteAsync(new Progress(), CancellationToken.None);
        await client.GetConferencesAsync(CancellationToken.None);
        await client.GetConferenceAsync("c", CancellationToken.None);

        Assert.Equal(1, _api.CountRequests("/public/conferences"));
        Assert.Equal(1, _api.CountRequests("/public/conferences/c"));
    }

    [Fact]
    public async Task ReplacesCachedConferenceDataWithFreshData()
    {
        _api.Json("/public/conferences", new CccConferencesResponse { Conferences = [Conference("c", Day(2024))] });
        _api.Json("/public/conferences/c", Conference("c", Day(2024)));
        var client = _api.CreateApiClient();
        await client.GetConferencesAsync(CancellationToken.None);
        await client.GetConferenceAsync("c", CancellationToken.None);

        await SyncTask(client).ExecuteAsync(new Progress(), CancellationToken.None);

        Assert.Equal(2, _api.CountRequests("/public/conferences"));
        Assert.Equal(2, _api.CountRequests("/public/conferences/c"));
    }

    [Fact]
    public async Task KeepsUnrelatedCacheEntriesInsteadOfWipingEverything()
    {
        using var cdn = new FakeCdn();
        var cdnUrl = cdn.AddRedirect("/talk.mp4", "http://mirror.example/talk.mp4");
        _api.Json("/public/conferences", new CccConferencesResponse { Conferences = [Conference("c", Day(2024))] });
        _api.Json("/public/conferences/c", Conference("c", Day(2024)));
        _api.Json("/public/events/e1", Event("e1"));
        var client = _api.CreateApiClient();
        await client.GetEventAsync("e1", CancellationToken.None);
        await client.ResolveRedirectAsync(cdnUrl, CancellationToken.None);

        await SyncTask(client).ExecuteAsync(new Progress(), CancellationToken.None);
        await client.GetEventAsync("e1", CancellationToken.None);
        await client.ResolveRedirectAsync(cdnUrl, CancellationToken.None);

        Assert.Equal(1, _api.CountRequests("/public/events/e1"));
        Assert.Single(cdn.Requests);
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
        var client = _api.CreateApiClient();

        await SyncTask(client).ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(1, _api.CountRequests("/public/conferences/ok2"));
        Assert.Equal(0, progress.Values[0]);
        Assert.Equal(100, progress.Values[^1]);
        Assert.Equal(progress.Values.Order(), progress.Values);

        await client.GetConferenceAsync("ok1", CancellationToken.None);
        await client.GetConferenceAsync("ok2", CancellationToken.None);
        Assert.Equal(1, _api.CountRequests("/public/conferences/ok1"));
        Assert.Equal(1, _api.CountRequests("/public/conferences/ok2"));
    }

    [Fact]
    public async Task HonoursCancellation()
    {
        _api.Json("/public/conferences", new CccConferencesResponse { Conferences = [Conference("c", Day(2024))] });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateTask().ExecuteAsync(new Progress(), cts.Token));
    }

    private ChaosflixSyncTask CreateTask() => SyncTask(_api.CreateApiClient());

    private static ChaosflixSyncTask SyncTask(CccApiClient client) => new(client, NullLogger<ChaosflixSyncTask>.Instance);

    // Progress<T> reports on the thread pool; tests need the values synchronously.
    private sealed class Progress : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value) => Values.Add(value);
    }
}
