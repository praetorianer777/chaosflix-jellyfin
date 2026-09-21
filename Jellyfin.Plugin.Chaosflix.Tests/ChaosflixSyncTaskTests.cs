using System.Net;
using Jellyfin.Plugin.Chaosflix.Api;
using Jellyfin.Plugin.Chaosflix.Api.Models;
using Jellyfin.Plugin.Chaosflix.Channel;
using Jellyfin.Plugin.Chaosflix.Tests.Fakes;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Jellyfin.Plugin.Chaosflix.Tests.Fakes.TestData;

namespace Jellyfin.Plugin.Chaosflix.Tests;

// The task reads the conference filter out of the plugin singleton, which every test
// in this collection shares; a fresh configuration per test keeps them independent.
[Collection(PluginCollection.Name)]
public class ChaosflixSyncTaskTests
{
    private readonly FakeCccApi _api = new();

    public ChaosflixSyncTaskTests() => TestPlugin.Configure();

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
    public async Task WalksTheConferencesTheUserFollowsIntoTheLibrary()
    {
        TestPlugin.Configure(c => c.ConferenceFilter = "congress");
        _api.Json("/public/conferences", new CccConferencesResponse
        {
            Conferences =
            [
                Conference("38c3", Day(2024), "congress/2024"),
                Conference("gpn22", Day(2023), "conferences/gpn/gpn22")
            ]
        });

        var channels = new FakeChannels();
        var years = channels.AddFolder(Guid.Empty, ChaosflixChannel.FolderBrowseByYear);
        var year2024 = channels.AddFolder(years, "year:2024");
        var kept = channels.AddFolder(year2024, ChaosflixChannel.ConferenceFolderId("38c3"));
        var dropped = channels.AddFolder(year2024, ChaosflixChannel.ConferenceFolderId("gpn22"));

        await SyncTask(_api.CreateApiClient(), channels).ExecuteAsync(new Progress(), CancellationToken.None);

        Assert.Contains(kept, channels.Asked);
        Assert.DoesNotContain(dropped, channels.Asked);
    }

    [Fact]
    public async Task WithoutAFilterOnlyTheNewestConferencesAreWalked()
    {
        var many = Enumerable.Range(1, ChaosflixSyncTask.DefaultConferenceCount + 5)
            .Select(i => Conference($"c{i:D2}", Day(2000 + i)))
            .ToList();
        _api.Json("/public/conferences", new CccConferencesResponse { Conferences = many });

        var channels = new FakeChannels();
        var years = channels.AddFolder(Guid.Empty, ChaosflixChannel.FolderBrowseByYear);
        var folders = many.ToDictionary(
            c => c.Acronym,
            c => channels.AddFolder(
                channels.AddFolder(years, $"year:{c.EventLastReleasedAt!.Value.Year}"),
                ChaosflixChannel.ConferenceFolderId(c.Acronym)));

        await SyncTask(_api.CreateApiClient(), channels).ExecuteAsync(new Progress(), CancellationToken.None);

        Assert.Contains(folders["c25"], channels.Asked);
        Assert.DoesNotContain(folders["c01"], channels.Asked);
        Assert.Equal(
            ChaosflixSyncTask.DefaultConferenceCount,
            folders.Values.Count(id => channels.Asked.Contains(id)));
    }

    [Fact]
    public async Task AChannelJellyfinHasNotRegisteredYetIsNotAnError()
    {
        _api.Json("/public/conferences", new CccConferencesResponse
        {
            Conferences = [Conference("c", Day(2024))]
        });

        var channels = new FakeChannels(channelRegistered: false);

        await SyncTask(_api.CreateApiClient(), channels).ExecuteAsync(new Progress(), CancellationToken.None);

        Assert.Empty(channels.Asked);
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

    private static ChaosflixSyncTask SyncTask(CccApiClient client) =>
        SyncTask(client, new FakeChannels());

    private static ChaosflixSyncTask SyncTask(CccApiClient client, FakeChannels channels) =>
        new(client, channels.Manager, NullLogger<ChaosflixSyncTask>.Instance);

    /// <summary>
    /// Stands in for Jellyfin's channel manager. It answers children by parent id and
    /// records every parent it was asked about, which is what the walk is judged on:
    /// asking for a folder's children is exactly what makes Jellyfin store them.
    /// </summary>
    private sealed class FakeChannels
    {
        private readonly Dictionary<Guid, List<BaseItem>> _children = new();

        public FakeChannels(bool channelRegistered = true)
        {
            Manager = Substitute.For<IChannelManager>();

            var channels = channelRegistered
                ? new List<MediaBrowser.Controller.Channels.Channel>
                {
                    new() { Id = ChannelId, Name = ChaosflixChannel.ChannelName }
                }
                : [];

            Manager.GetChannelsInternalAsync(Arg.Any<ChannelQuery>())
                .Returns(Task.FromResult(new QueryResult<MediaBrowser.Controller.Channels.Channel>(channels)));

            Manager.GetChannelItemsInternal(
                    Arg.Any<InternalItemsQuery>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var parent = call.Arg<InternalItemsQuery>().ParentId;
                    Asked.Add(parent);
                    return Task.FromResult(new QueryResult<BaseItem>(
                        _children.TryGetValue(parent, out var items) ? items : []));
                });
        }

        public static Guid ChannelId { get; } = Guid.NewGuid();

        public IChannelManager Manager { get; }

        /// <summary>Every parent the walk asked for children of, in order.</summary>
        public List<Guid> Asked { get; } = new();

        public Guid AddFolder(Guid parent, string externalId)
        {
            var item = new Folder { Id = Guid.NewGuid(), Name = externalId, ExternalId = externalId };
            if (!_children.TryGetValue(parent, out var siblings))
            {
                siblings = _children[parent] = new List<BaseItem>();
            }

            siblings.Add(item);
            return item.Id;
        }
    }

    // Progress<T> reports on the thread pool; tests need the values synchronously.
    private sealed class Progress : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value) => Values.Add(value);
    }
}
