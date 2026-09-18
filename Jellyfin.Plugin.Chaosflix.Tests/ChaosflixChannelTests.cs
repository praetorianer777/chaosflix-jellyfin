using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Chaosflix.Api.Models;
using Jellyfin.Plugin.Chaosflix.Channel;
using Jellyfin.Plugin.Chaosflix.Configuration;
using Jellyfin.Plugin.Chaosflix.Tests.Fakes;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using static Jellyfin.Plugin.Chaosflix.Tests.Fakes.TestData;

namespace Jellyfin.Plugin.Chaosflix.Tests;

[Collection(PluginCollection.Name)]
public class ChaosflixChannelTests
{
    private readonly FakeCccApi _api = new();
    private readonly IMediaEncoder _encoder = Substitute.For<IMediaEncoder>();
    private readonly FakeTime _time = new();
    private readonly MemoryCache _mediaSourceCache = new(new MemoryCacheOptions());
    private readonly Plugin _plugin;
    private readonly ChaosflixChannel _channel;

    public ChaosflixChannelTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        _plugin = TestPlugin.Configure();

        var host = Substitute.For<IServerApplicationHost>();
        host.GetSmartApiUrl(Arg.Any<string>()).Returns("http://jellyfin:8096/");
        _encoder.GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MediaInfo { MediaStreams = Streams(MediaStreamType.Video, MediaStreamType.Audio) });

        _channel = new ChaosflixChannel(_api.CreateApiClient(), NullLogger<ChaosflixChannel>.Instance, host, _encoder, _time, _mediaSourceCache);
    }

    [Fact]
    public async Task RootListsVirtualFolders()
    {
        var result = await Items(null);

        Assert.Equal(new[] { "virtual:popular", "virtual:recommended", "virtual:years" }, result.Items.Select(i => i.Id));
        Assert.All(result.Items, i => Assert.Equal(ChannelItemType.Folder, i.Type));
        Assert.Equal(3, result.TotalRecordCount);
    }

    [Fact]
    public async Task UnknownFolderIsEmpty()
    {
        var result = await Items("bogus:1");

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task YearFoldersAreDistinctAndDescending()
    {
        Conferences(
            Conference("a", Day(2023)),
            Conference("b", Day(2024, 3)),
            Conference("c", Day(2024, 12)),
            Conference("unreleased", null));

        var result = await Items("virtual:years");

        Assert.Equal(new[] { "year:2024", "year:2023" }, result.Items.Select(i => i.Id));
        Assert.Equal(new[] { "2024", "2023" }, result.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task YearListsItsConferencesNewestFirst()
    {
        Conferences(
            Conference("old", Day(2024, 1)),
            Conference("other-year", Day(2023)),
            Conference("new", Day(2024, 12)));

        var result = await Items("year:2024");

        Assert.Equal(new[] { "conf:new", "conf:old" }, result.Items.Select(i => i.Id));
        var first = result.Items[0];
        Assert.Equal("NEW", first.Name);
        Assert.Equal("https://static.media.ccc.de/new.png", first.ImageUrl);
        Assert.Equal(ChannelItemType.Folder, first.Type);
    }

    [Fact]
    public async Task InvalidYearIsEmpty()
    {
        Assert.Empty((await Items("year:abc")).Items);
    }

    [Fact]
    public async Task ConferenceListsEventsNewestFirst()
    {
        _api.Json("/public/conferences/38c3", Conference("38c3", Day(2025),
            Event("e-old", date: Day(2024, 12, 27)),
            Event("e-new", date: Day(2024, 12, 30))));

        var result = await Items("conf:38c3");

        Assert.Equal(new[] { "event:conf-38c3:e-new", "event:conf-38c3:e-old" }, result.Items.Select(i => i.Id));
        Assert.All(result.Items, i => Assert.Equal(ChannelItemType.Media, i.Type));
    }

    [Fact]
    public async Task EventMappingCarriesMetadata()
    {
        var ev = Event("e1", views: 1000, date: Day(2024, 12, 28), related: [new CccRelatedEvent { EventGuid = "x" }, new CccRelatedEvent { EventGuid = "y" }]);
        ev.Title = "Hacking the planet";
        ev.Subtitle = "Sub";
        ev.Description = "Talk description";
        ev.ConferenceTitle = "38C3";
        ev.OriginalLanguage = "deu";
        ev.Persons = ["Alice", "Bob"];
        ev.Tags = ["Security", "2024", "38c3", "2024c3", "Stage HUFF", "ab", "123", "Ethics"];
        ev.PosterUrl = "https://static/poster.jpg";
        ev.ThumbUrl = "https://static/thumb.jpg";
        ev.FrontendLink = "https://media.ccc.de/v/e1";
        _api.Json("/public/conferences/38c3", Conference("38c3", Day(2025), ev));

        var item = Assert.Single((await Items("conf:38c3")).Items);

        Assert.Equal("Hacking the planet", item.Name);
        Assert.Equal("Sub", item.OriginalTitle);
        Assert.Equal("38C3", item.SeriesName);
        Assert.Equal("https://static/poster.jpg", item.ImageUrl);
        Assert.Equal("https://media.ccc.de/v/e1", item.HomePageUrl);
        Assert.Equal(TimeSpan.FromHours(1).Ticks, item.RunTimeTicks);
        Assert.Equal(Day(2024, 12, 28).DateTime, item.DateCreated);
        Assert.Equal(6f, item.CommunityRating!.Value, 3);
        Assert.Equal(new[] { "Security", "38c3", "Ethics" }, item.Genres);
        Assert.Equal(new[] { "Alice", "Bob" }, item.People.Select(p => p.Name));
        Assert.All(item.People, p => Assert.Equal(PersonKind.Actor, p.Type));
        Assert.Equal(
            "38C3 · Speaker: Alice, Bob · 1,000 views · Language: deu\n\nTalk description\n\n→ 2 related talks available",
            item.Overview);
    }

    [Fact]
    public async Task EventWithoutViewsHasNoRatingAndPlainOverview()
    {
        var ev = Event("e1");
        ev.ThumbUrl = "https://static/thumb.jpg";
        ev.Description = "Only description";
        _api.Json("/public/conferences/c", Conference("c", Day(2025), ev));

        var item = Assert.Single((await Items("conf:c")).Items);

        Assert.Null(item.CommunityRating);
        Assert.Equal("https://static/thumb.jpg", item.ImageUrl);
        Assert.Equal("Only description", item.Overview);
    }

    [Fact]
    public async Task CopiesOutsideTheConferenceFolderAreBackdated()
    {
        var date = Day(2024, 12, 28);
        Conferences(Conference("c", Day(2025)));
        _api.Json("/public/conferences/c", Conference("c", Day(2025),
            Event("e1", views: 1000, date: date),
            Event("e2", views: 2000, date: date)));

        var conference = Assert.Single((await Items("conf:c")).Items, i => i.Id.EndsWith(":e1", StringComparison.Ordinal));
        var popular = Assert.Single((await Items("virtual:popular")).Items, i => i.Id.EndsWith(":e1", StringComparison.Ordinal));

        Assert.Equal(date.DateTime, conference.DateCreated);
        Assert.Equal(date.DateTime - ChaosflixChannel.NonCanonicalBackdate, popular.DateCreated);
    }

    [Fact]
    public async Task PopularUsesFiveMostRecentConferencesSortedByViews()
    {
        var confs = Enumerable.Range(1, 6)
            .Select(i => Conference($"c{i}", Day(2020 + i)))
            .ToArray();
        Conferences(confs);
        for (var i = 1; i <= 6; i++)
        {
            _api.Json($"/public/conferences/c{i}", Conference($"c{i}", Day(2020 + i), Event($"e{i}", views: i * 10)));
        }

        var result = await Items("virtual:popular");

        Assert.Equal(
            new[] { "event:popular:e6", "event:popular:e5", "event:popular:e4", "event:popular:e3", "event:popular:e2" },
            result.Items.Select(i => i.Id));
        Assert.Equal(0, _api.CountRequests("/public/conferences/c1"));
    }

    [Fact]
    public async Task PopularIsCappedAtFifty()
    {
        Conferences(Conference("big", Day(2025)));
        _api.Json("/public/conferences/big", Conference("big", Day(2025),
            Enumerable.Range(0, 80).Select(i => Event($"e{i}", views: i)).ToArray()));

        var result = await Items("virtual:popular");

        Assert.Equal(50, result.Items.Count);
        Assert.Equal("event:popular:e79", result.Items[0].Id);
    }

    [Fact]
    public async Task RecommendedFiltersLowViewsAndFavoursRecentTalks()
    {
        var now = DateTimeOffset.UtcNow;
        Conferences(Conference("c", Day(2025)));
        _api.Json("/public/conferences/c", Conference("c", Day(2025),
            Event("too-few-views", views: 100, releaseDate: now.AddDays(-1)),
            Event("old-popular", views: 10_000, releaseDate: now.AddDays(-400)),
            Event("fresh", views: 2_000, releaseDate: now.AddDays(-2))));

        var result = await Items("virtual:recommended");

        Assert.Equal(new[] { "event:recommended:fresh", "event:recommended:old-popular" }, result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task RelatedFetchesTopFifteenByWeight()
    {
        var related = Enumerable.Range(1, 20)
            .Select(i => new CccRelatedEvent { EventGuid = $"r{i}", Weight = i })
            .ToList();
        _api.Json("/public/events/main", Event("main", related: related));
        for (var i = 1; i <= 20; i++)
        {
            _api.Json($"/public/events/r{i}", Event($"r{i}"));
        }

        var result = await Items("related:main");

        Assert.Equal(15, result.Items.Count);
        Assert.Equal("event:related-main:r20", result.Items[0].Id);
        Assert.Equal("event:related-main:r6", result.Items[^1].Id);
        Assert.Equal(0, _api.CountRequests("/public/events/r5"));
    }

    [Fact]
    public async Task RelatedWithoutRelationsIsEmpty()
    {
        _api.Json("/public/events/main", Event("main"));

        Assert.Empty((await Items("related:main")).Items);
    }

    [Fact]
    public async Task LatestMediaTakesTwentyNewestFromThreeRecentConferences()
    {
        Conferences(
            Conference("c1", Day(2021)),
            Conference("c2", Day(2022)),
            Conference("c3", Day(2023)),
            Conference("c4", Day(2024)));
        for (var c = 2; c <= 4; c++)
        {
            _api.Json($"/public/conferences/c{c}", Conference($"c{c}", Day(2020 + c),
                Enumerable.Range(1, 10).Select(d => Event($"c{c}-{d}", releaseDate: Day(2020 + c, 1, d))).ToArray()));
        }

        var latest = (await _channel.GetLatestMedia(new ChannelLatestMediaSearch(), CancellationToken.None)).ToList();

        Assert.Equal(20, latest.Count);
        Assert.Equal("event:conf-c4:c4-10", latest[0].Id);
        Assert.DoesNotContain(latest, i => i.Id.StartsWith("event:conf-c2:", StringComparison.Ordinal));
        Assert.Equal(0, _api.CountRequests("/public/conferences/c1"));
    }

    [Fact]
    public async Task MediaInfoIsEmptyWithoutVideoRecordings()
    {
        EventWithRecordings("e1", Recording("mp3", mimeType: "audio/mpeg"));

        Assert.Empty(await Sources("event:e1"));
        await _encoder.DidNotReceiveWithAnyArgs().GetMediaInfo(default!, default);
    }

    [Fact]
    public async Task MediaInfoPointsAtLocalProxy()
    {
        EventWithRecordings("e1", Recording("h264-hd", language: "deu-eng"));

        var source = Assert.Single(await Sources("event:e1"));

        Assert.StartsWith(
            "http://jellyfin:8096/api/ChaosflixStream/proxy/e1?recordingFolder=h264-hd&language=deu-eng&t=",
            source.Path,
            StringComparison.Ordinal);
        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.False(source.IsRemote);
        Assert.False(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.Equal("mp4", source.Container);
        Assert.Equal(100L * 1024 * 1024, source.Size);
        Assert.Equal(TimeSpan.FromSeconds(60).Ticks, source.RunTimeTicks);
        Assert.Equal((int)(100L * 1024 * 1024 * 8 / 60), source.Bitrate);
        Assert.Equal("HD 1920x1080 (MP4) [deu-eng]", source.Name);
    }

    [Fact]
    public async Task MediaSourceIdIsStableGuid()
    {
        EventWithRecordings("e1", Recording("h264-hd"));

        var first = Assert.Single(await Sources("e1"));
        var second = Assert.Single(await Sources("event:e1"));

        Assert.True(Guid.TryParseExact(first.Id, "N", out _));
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task MediaSourceIdIsTheItemIdWhenTheLibraryKnowsIt()
    {
        EventWithRecordings("e1", Recording("h264-hd"));
        var itemId = Guid.Parse("11112222-3333-4444-5555-666677778888");
        var library = Substitute.For<ILibraryManager>();
        library.GetItemIds(Arg.Is<InternalItemsQuery>(q => q.ExternalId == "event:e1")).Returns(new[] { itemId });
        var channel = Channel(library);

        var source = Assert.Single(await channel.GetChannelItemMediaInfo("event:e1", CancellationToken.None));

        Assert.Equal(itemId.ToString("N"), source.Id);
    }

    [Fact]
    public async Task MediaSourceIdFallsBackWhenTheItemIsUnknown()
    {
        EventWithRecordings("e1", Recording("h264-hd"));
        var library = Substitute.For<ILibraryManager>();
        library.GetItemIds(Arg.Any<InternalItemsQuery>()).Returns(Array.Empty<Guid>());
        var channel = Channel(library);

        var source = Assert.Single(await channel.GetChannelItemMediaInfo("event:e1", CancellationToken.None));

        Assert.Equal(Assert.Single(await Sources("event:e1")).Id, source.Id);
    }

    [Theory]
    [InlineData(VideoFormat.Mp4, VideoQuality.High, "", "h264-hd")]
    [InlineData(VideoFormat.Mp4, VideoQuality.Standard, "", "h264-sd")]
    [InlineData(VideoFormat.WebM, VideoQuality.High, "", "webm-hd")]
    [InlineData(VideoFormat.WebM, VideoQuality.Standard, "", "webm-sd")]
    public async Task SelectsRecordingByFormatAndQuality(VideoFormat format, VideoQuality quality, string language, string expectedFolder)
    {
        TestPlugin.Configure(c =>
        {
            c.PreferredFormat = format;
            c.PreferredQuality = quality;
            c.PreferredLanguage = language;
        });
        EventWithRecordings("e1",
            Recording("webm-sd", "video/webm", highQuality: false, width: 720),
            Recording("h264-sd", highQuality: false, width: 720),
            Recording("webm-hd", "video/webm"),
            Recording("h264-hd"));

        var source = Assert.Single(await Sources("event:e1"));

        Assert.Contains($"recordingFolder={expectedFolder}&", source.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreferredLanguageWinsBetweenEqualRecordings()
    {
        TestPlugin.Configure(c => c.PreferredLanguage = "eng");
        EventWithRecordings("e1",
            Recording("h264-hd", language: "deu"),
            Recording("h264-hd", language: "eng"));

        var source = Assert.Single(await Sources("event:e1"));

        Assert.Contains("language=eng&t=", source.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Av1IsRankedBelowEveryOtherFormat()
    {
        EventWithRecordings("e1",
            Recording("av1-hd"),
            Recording("webm-sd", "video/webm", highQuality: false, width: 720));

        var source = Assert.Single(await Sources("event:e1"));

        Assert.Contains("recordingFolder=webm-sd", source.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HigherResolutionBreaksTies()
    {
        EventWithRecordings("e1",
            Recording("h264-hd-small", width: 1280),
            Recording("h264-hd-large", width: 1920));

        var source = Assert.Single(await Sources("event:e1"));

        Assert.Contains("recordingFolder=h264-hd-large", source.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThreeStreamMp4KeepsAllStreamsAndPointsAtRealAudioIndex()
    {
        _encoder.GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MediaInfo { MediaStreams = Streams(MediaStreamType.Video, MediaStreamType.Video, MediaStreamType.Audio) });
        EventWithRecordings("e1", Recording("h264-hd"));

        var source = Assert.Single(await Sources("event:e1"));

        Assert.Equal(3, source.MediaStreams.Count);
        Assert.Equal(2, source.DefaultAudioStreamIndex);
    }

    [Fact]
    public async Task TwoStreamMp4PointsAtAudioIndexOne()
    {
        EventWithRecordings("e1", Recording("h264-hd"));

        var source = Assert.Single(await Sources("event:e1"));

        Assert.Equal(2, source.MediaStreams.Count);
        Assert.Equal(1, source.DefaultAudioStreamIndex);
    }

    [Fact]
    public async Task ProbesThroughProxyUrlOncePerEvent()
    {
        EventWithRecordings("e1", Recording("h264-hd"));

        await Sources("event:e1");
        await Sources("event:e1");

        await _encoder.Received(1).GetMediaInfo(
            Arg.Is<MediaInfoRequest>(r => r.MediaSource.Path.StartsWith("http://jellyfin:8096/api/ChaosflixStream/proxy/e1?", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProbeFailureYieldsSourceWithoutStreamsAndIsRetried()
    {
        _encoder.GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ffprobe failed"));
        EventWithRecordings("e1", Recording("h264-hd"));

        var source = Assert.Single(await Sources("event:e1"));
        await Sources("event:e1");

        Assert.Empty(source.MediaStreams);
        Assert.Null(source.DefaultAudioStreamIndex);
        await _encoder.Received(2).GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangingPreferredFormatProbesTheNewRecording()
    {
        EventWithRecordings("e1", Recording("h264-hd"), Recording("webm-hd", "video/webm"));
        await Sources("event:e1");

        TestPlugin.Configure(c => c.PreferredFormat = VideoFormat.WebM);
        await Sources("event:e1");

        await _encoder.Received(1).GetMediaInfo(
            Arg.Is<MediaInfoRequest>(r => r.MediaSource.Path.Contains("recordingFolder=webm-hd", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EveryFolderHandsOutItsOwnIdForTheSameTalk()
    {
        Conferences(Conference("c", Day(2025)));
        _api.Json("/public/conferences/c", Conference("c", Day(2025), Event("shared", views: 5_000, releaseDate: DateTimeOffset.UtcNow.AddDays(-1))));
        _api.Json("/public/events/main", Event("main", related: [new CccRelatedEvent { EventGuid = "shared", Weight = 1 }]));
        _api.Json("/public/events/shared", Event("shared"));

        var ids = new[]
        {
            Assert.Single((await Items("conf:c")).Items).Id,
            Assert.Single((await Items("virtual:popular")).Items).Id,
            Assert.Single((await Items("virtual:recommended")).Items).Id,
            Assert.Single((await Items("related:main")).Items).Id
        };

        Assert.Equal(
            new[] { "event:conf-c:shared", "event:popular:shared", "event:recommended:shared", "event:related-main:shared" },
            ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public async Task LatestMediaReusesTheConferenceFolderIds()
    {
        Conferences(Conference("c", Day(2025)));
        _api.Json("/public/conferences/c", Conference("c", Day(2025), Event("e1", releaseDate: Day(2025, 1, 2))));

        var latest = await _channel.GetLatestMedia(new ChannelLatestMediaSearch(), CancellationToken.None);

        Assert.Equal(Assert.Single((await Items("conf:c")).Items).Id, Assert.Single(latest).Id);
    }

    [Theory]
    [InlineData("event:conf-38c3:e1")]
    [InlineData("event:popular:e1")]
    [InlineData("event:recommended:e1")]
    [InlineData("event:related-other:e1")]
    [InlineData("event:e1")]
    [InlineData("e1")]
    public async Task MediaInfoResolvesTheEventGuidFromAnyFolderId(string itemId)
    {
        EventWithRecordings("e1", Recording("h264-hd"));

        var source = Assert.Single(await Sources(itemId));

        Assert.StartsWith("http://jellyfin:8096/api/ChaosflixStream/proxy/e1?", source.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeCacheIsSharedBetweenFoldersOfTheSameEvent()
    {
        EventWithRecordings("e1", Recording("h264-hd"));

        await Sources("event:conf-c:e1");
        await Sources("event:popular:e1");

        await _encoder.Received(1).GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProbeIsRepeatedOnceTheCachedEntryHasExpired()
    {
        EventWithRecordings("e1", Recording("h264-hd"));

        await Sources("event:e1");
        _time.Advance(ChaosflixChannel.ProbeCacheTtl + TimeSpan.FromMinutes(1));
        await Sources("event:e1");

        await _encoder.Received(2).GetMediaInfo(Arg.Any<MediaInfoRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProbeCacheDropsTheOldestEntryOnceItIsFull()
    {
        for (var i = 0; i <= ChaosflixChannel.ProbeCacheCapacity; i++)
        {
            EventWithRecordings($"e{i}", Recording("h264-hd"));
            await Sources($"event:e{i}");
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        await Sources("event:e0");

        await _encoder.Received(2).GetMediaInfo(
            Arg.Is<MediaInfoRequest>(r => r.MediaSource.Path.StartsWith("http://jellyfin:8096/api/ChaosflixStream/proxy/e0?", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _encoder.Received(1).GetMediaInfo(
            Arg.Is<MediaInfoRequest>(r => r.MediaSource.Path.StartsWith("http://jellyfin:8096/api/ChaosflixStream/proxy/e1?", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfigurationChangeDropsTheMediaSourcesJellyfinCached()
    {
        EventWithRecordings("e1", Recording("h264-hd"));
        await Sources("event:conf-c:e1");
        CacheAsChannelManagerWould("event:conf-c:e1");

        _plugin.UpdateConfiguration(new PluginConfiguration { PreferredFormat = VideoFormat.WebM });

        Assert.False(_mediaSourceCache.TryGetValue("event:conf-c:e1", out _));
    }

    [Fact]
    public async Task ConfigurationChangeLeavesCacheEntriesOfOtherPluginsAlone()
    {
        EventWithRecordings("e1", Recording("h264-hd"));
        await Sources("event:conf-c:e1");
        CacheAsChannelManagerWould("someone-elses-key");

        _plugin.UpdateConfiguration(new PluginConfiguration { PreferredFormat = VideoFormat.WebM });

        Assert.True(_mediaSourceCache.TryGetValue("someone-elses-key", out _));
    }

    [Fact]
    public async Task ConfigurationChangeIgnoresTalksWhoseCacheEntryHasLongExpired()
    {
        EventWithRecordings("e1", Recording("h264-hd"));
        await Sources("event:conf-c:e1");
        _time.Advance(ChaosflixChannel.ServedIdRetention + TimeSpan.FromMinutes(1));
        EventWithRecordings("e2", Recording("h264-hd"));
        await Sources("event:conf-c:e2");
        CacheAsChannelManagerWould("event:conf-c:e1");

        _plugin.UpdateConfiguration(new PluginConfiguration { PreferredFormat = VideoFormat.WebM });

        Assert.True(_mediaSourceCache.TryGetValue("event:conf-c:e1", out _));
    }

    private void CacheAsChannelManagerWould(string id) =>
        _mediaSourceCache.Set(id, new List<MediaSourceInfo>(), DateTimeOffset.UtcNow.AddMinutes(5));

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static List<MediaStream> Streams(params MediaStreamType[] types) =>
        types.Select((t, i) => new MediaStream { Type = t, Index = i }).ToList();

    private void Conferences(params CccConference[] conferences) =>
        _api.Json("/public/conferences", new CccConferencesResponse { Conferences = conferences.ToList() });

    private void EventWithRecordings(string guid, params CccRecording[] recordings) =>
        _api.Json($"/public/events/{guid}", Event(guid, recordings: recordings.ToList()));

    private ChaosflixChannel Channel(ILibraryManager libraryManager)
    {
        var host = Substitute.For<IServerApplicationHost>();
        host.GetSmartApiUrl(Arg.Any<string>()).Returns("http://jellyfin:8096/");
        return new ChaosflixChannel(
            _api.CreateApiClient(), NullLogger<ChaosflixChannel>.Instance, host, _encoder, _time, _mediaSourceCache, libraryManager);
    }

    private Task<ChannelItemResult> Items(string? folderId) =>
        _channel.GetChannelItems(new InternalChannelItemQuery { FolderId = folderId }, CancellationToken.None);

    private async Task<List<MediaBrowser.Model.Dto.MediaSourceInfo>> Sources(string id) =>
        (await _channel.GetChannelItemMediaInfo(id, CancellationToken.None)).ToList();
}
