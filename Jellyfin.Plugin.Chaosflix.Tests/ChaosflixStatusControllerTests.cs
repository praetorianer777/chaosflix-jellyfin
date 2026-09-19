using Jellyfin.Plugin.Chaosflix.Api;
using Jellyfin.Plugin.Chaosflix.Api.Models;
using Jellyfin.Plugin.Chaosflix.Channel;
using Jellyfin.Plugin.Chaosflix.Tests.Fakes;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Jellyfin.Plugin.Chaosflix.Tests.Fakes.TestData;

namespace Jellyfin.Plugin.Chaosflix.Tests;

[Collection(PluginCollection.Name)]
public sealed class ChaosflixStatusControllerTests : IDisposable
{
    private readonly FakeCccApi _api = new();
    private readonly FakeCdn _cdn = new();
    private readonly IMediaEncoder _encoder = Substitute.For<IMediaEncoder>();
    private readonly ITaskManager _taskManager = Substitute.For<ITaskManager>();
    private readonly CccApiClient _apiClient;
    private readonly ChaosflixChannel _channel;
    private readonly ChaosflixStatusController _controller;

    public ChaosflixStatusControllerTests()
    {
        TestPlugin.Configure(config => config.ApiBaseUrl = "http://fake/public");

        _apiClient = _api.CreateApiClient();
        var host = Substitute.For<IServerApplicationHost>();
        host.GetSmartApiUrl(Arg.Any<string>()).Returns("http://jellyfin:8096/");
        _encoder.GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MediaInfo
            {
                MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" }]
            });

        _channel = new ChaosflixChannel(_apiClient, NullLogger<ChaosflixChannel>.Instance, host, _encoder);
        _taskManager.ScheduledTasks.Returns([]);

        _controller = new ChaosflixStatusController(
            _apiClient, _channel, _taskManager, NullLogger<ChaosflixStatusController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    public void Dispose()
    {
        _cdn.Dispose();
        _apiClient.Dispose();
    }

    private PluginStatus Status() => Assert.IsType<PluginStatus>(_controller.GetStatus().Value);

    [Fact]
    public void ReportsTheConfiguredEndpoint()
    {
        var status = Status();

        Assert.Equal("http://fake/public", status.ApiBaseUrl);
        Assert.False(status.ApiBaseUrlIsDefault);
    }

    [Fact]
    public void ReportsThePublicEndpointAsTheDefault()
    {
        TestPlugin.Configure();

        var status = Status();

        Assert.Equal(CccApiClient.DefaultBaseUrl, status.ApiBaseUrl);
        Assert.True(status.ApiBaseUrlIsDefault);
    }

    [Fact]
    public async Task CountsCachedConferencesTalksAndMirrors()
    {
        _api.Json("/public/conferences", new CccConferencesResponse { Conferences = [Conference("c", Day(2024))] });
        _api.Json("/public/conferences/c", Conference("c", Day(2024)));
        _api.Json("/public/events/e1", Event("e1"));

        await _apiClient.GetConferencesAsync(CancellationToken.None);
        await _apiClient.GetConferenceAsync("c", CancellationToken.None);
        await _apiClient.GetEventAsync("e1", CancellationToken.None);
        await _apiClient.ResolveRedirectAsync(_cdn.AddFile("/hd.mp4", [1, 2, 3]), CancellationToken.None);

        var cache = Status().ApiCache;

        Assert.Equal(2, cache.Conferences);
        Assert.Equal(1, cache.Events);
        Assert.Equal(1, cache.Redirects);
        Assert.Equal(4, cache.Entries);
    }

    [Fact]
    public async Task ReportsTheCacheHitRate()
    {
        _api.Json("/public/events/e1", Event("e1"));

        await _apiClient.GetEventAsync("e1", CancellationToken.None);
        await _apiClient.GetEventAsync("e1", CancellationToken.None);
        await _apiClient.GetEventAsync("e1", CancellationToken.None);

        var cache = Status().ApiCache;

        Assert.Equal(2, cache.Hits);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(66.7, cache.HitRatePercent);
    }

    [Fact]
    public void HitRateIsUnknownBeforeAnythingWasRead()
    {
        Assert.Null(Status().ApiCache.HitRatePercent);
        Assert.Null(Status().ProbeCache.HitRatePercent);
    }

    [Fact]
    public async Task CountsProbedStreamLayoutsAndServedTalks()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd")]));

        await _channel.GetChannelItemMediaInfo("event:e1", CancellationToken.None);
        await _channel.GetChannelItemMediaInfo("event:e1", CancellationToken.None);

        var probe = Status().ProbeCache;

        Assert.Equal(1, probe.Entries);
        Assert.Equal(ChaosflixChannel.ProbeCacheCapacity, probe.Capacity);
        Assert.Equal(1, probe.Hits);
        Assert.Equal(1, probe.Misses);
        Assert.Equal(1, probe.ServedItems);
    }

    [Fact]
    public void ReportsTheSyncTasksLastRun()
    {
        var start = new DateTime(2025, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        var other = Worker("Other", TaskCompletionStatus.Failed, start, start.AddMinutes(9));
        var chaosflix = Worker(ChaosflixSyncTask.TaskKey, TaskCompletionStatus.Completed, start, start.AddSeconds(42));
        _taskManager.ScheduledTasks.Returns([other, chaosflix]);

        var sync = Status().Sync;

        Assert.True(sync.Registered);
        Assert.Equal(start, sync.LastRunUtc);
        Assert.Equal(42, sync.DurationSeconds);
        Assert.Equal("Completed", sync.LastResult);
        Assert.Equal("Idle", sync.State);
        Assert.Null(sync.Error);
    }

    [Fact]
    public void ReportsAFailedSyncRunWithItsError()
    {
        var start = new DateTime(2025, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        var failed = Worker(ChaosflixSyncTask.TaskKey, TaskCompletionStatus.Failed, start, start.AddSeconds(3), "api unreachable");
        _taskManager.ScheduledTasks.Returns([failed]);

        var sync = Status().Sync;

        Assert.Equal("Failed", sync.LastResult);
        Assert.Equal("api unreachable", sync.Error);
    }

    [Fact]
    public void ReportsAnUnregisteredSyncTask()
    {
        var sync = Status().Sync;

        Assert.False(sync.Registered);
        Assert.Null(sync.LastRunUtc);
    }

    [Fact]
    public void ReportsASyncTaskThatHasNeverRun()
    {
        var worker = Substitute.For<IScheduledTaskWorker>();
        var task = Substitute.For<IScheduledTask>();
        task.Key.Returns(ChaosflixSyncTask.TaskKey);
        worker.ScheduledTask.Returns(task);
        worker.State.Returns(TaskState.Idle);
        worker.LastExecutionResult.Returns((TaskResult?)null);
        _taskManager.ScheduledTasks.Returns([worker]);

        var sync = Status().Sync;

        Assert.True(sync.Registered);
        Assert.Null(sync.LastRunUtc);
        Assert.Null(sync.LastResult);
    }

    [Fact]
    public async Task ReachabilityCheckReportsTheRoundTrip()
    {
        _api.Json("/public/conferences", new CccConferencesResponse
        {
            Conferences = [Conference("a", Day(2024)), Conference("b", Day(2023))]
        });

        var result = Assert.IsType<ApiCheck>((await _controller.CheckApi()).Value);

        Assert.True(result.Ok);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, result.ConferenceCount);
        Assert.Equal("http://fake/public/conferences", result.Url);
        Assert.Null(result.Error);
        Assert.True(result.ElapsedMs >= 0);
    }

    [Fact]
    public async Task ReachabilityCheckReportsAnEndpointThatSaysNo()
    {
        var result = Assert.IsType<ApiCheck>((await _controller.CheckApi()).Value);

        Assert.False(result.Ok);
        Assert.Equal(404, result.StatusCode);
        Assert.Null(result.ConferenceCount);
    }

    [Fact]
    public async Task ReachabilityCheckDoesNotFillTheCache()
    {
        _api.Json("/public/conferences", new CccConferencesResponse { Conferences = [] });

        await _controller.CheckApi();

        Assert.Equal(0, Status().ApiCache.Entries);
    }

    [Fact]
    public async Task ClearingCachesEmptiesEveryCacheThePluginKeeps()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd")]));
        await _channel.GetChannelItemMediaInfo("event:e1", CancellationToken.None);
        Assert.Equal(1, Status().ApiCache.Events);

        var after = Assert.IsType<PluginStatus>(_controller.ClearCaches().Value);

        Assert.Equal(0, after.ApiCache.Entries);
        Assert.Equal(0, after.ProbeCache.Entries);
        Assert.Equal(0, after.ProbeCache.ServedItems);
    }

    [Fact]
    public async Task TalkCheckWalksEveryStageAndStopsWhereItFails()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd")]));

        var result = Assert.IsType<TalkCheck>((await _controller.CheckTalk("e1")).Value);

        // The proxy url points at the server this plugin runs in, which no unit test
        // hosts, so the walk gets as far as the signature and fails at the proxy.
        Assert.False(result.Ok);
        Assert.Equal("Proxy", result.FailedStage);
        Assert.Equal(["API lookup", "Recording choice", "Signed proxy url", "Proxy"], result.Stages.Select(s => s.Name));
        Assert.Equal("Talk e1", result.Title);
        Assert.Equal([true, true, true, false], result.Stages.Select(s => s.Ok));
    }

    [Fact]
    public async Task TalkCheckReportsAnUnknownTalk()
    {
        var result = Assert.IsType<TalkCheck>((await _controller.CheckTalk("nope")).Value);

        Assert.False(result.Ok);
        Assert.Equal("API lookup", result.FailedStage);
        Assert.Null(result.Title);
        Assert.Single(result.Stages);
    }

    [Fact]
    public async Task TalkCheckReportsATalkWithoutRecordings()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: []));

        var result = Assert.IsType<TalkCheck>((await _controller.CheckTalk("e1")).Value);

        Assert.Equal("API lookup", result.FailedStage);
        Assert.Contains("no recordings", Assert.Single(result.Stages).Detail);
    }

    [Fact]
    public async Task TalkCheckReportsAudioOnlyTalksAsNoUsableRecording()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("mp3", mimeType: "audio/mpeg")]));

        var result = Assert.IsType<TalkCheck>((await _controller.CheckTalk("e1")).Value);

        Assert.Equal("Recording choice", result.FailedStage);
    }

    [Fact]
    public async Task TalkCheckNeverShowsASignedUrl()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd")]));

        var result = Assert.IsType<TalkCheck>((await _controller.CheckTalk("e1")).Value);

        var signature = ProxySignature.Create("e1", "h264-hd", "eng");
        var rendered = string.Join("\n", result.Stages.Select(s => s.Detail));
        Assert.DoesNotContain(signature, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ProxySignature.Secret, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("t=<redacted>", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TalkCheckRejectsAnEmptyIdentifier()
    {
        Assert.IsType<BadRequestObjectResult>((await _controller.CheckTalk("  ")).Result);
    }

    [Theory]
    [InlineData("e1", "e1")]
    [InlineData("event:e1", "e1")]
    [InlineData(" event:e1 ", "e1")]
    [InlineData("https://media.ccc.de/v/38c3-talk", "38c3-talk")]
    [InlineData("https://media.ccc.de/v/38c3-talk/", "38c3-talk")]
    [InlineData("https://media.ccc.de/v/38c3-talk#t=10", "38c3-talk")]
    [InlineData("", "")]
    public void TalkIdentifiersAreNormalizedToWhatTheApiTakes(string input, string expected)
    {
        Assert.Equal(expected, ChaosflixStatusController.NormalizeTalkId(input));
    }

    private static IScheduledTaskWorker Worker(
        string key,
        TaskCompletionStatus status,
        DateTime start,
        DateTime end,
        string? error = null)
    {
        var task = Substitute.For<IScheduledTask>();
        task.Key.Returns(key);

        var worker = Substitute.For<IScheduledTaskWorker>();
        worker.ScheduledTask.Returns(task);
        worker.State.Returns(TaskState.Idle);
        worker.LastExecutionResult.Returns(new TaskResult
        {
            Key = key,
            StartTimeUtc = start,
            EndTimeUtc = end,
            Status = status,
            ErrorMessage = error
        });
        return worker;
    }
}
