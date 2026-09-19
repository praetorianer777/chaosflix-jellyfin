using System.Net;
using System.Text;
using Jellyfin.Plugin.Chaosflix.Api.Models;
using Jellyfin.Plugin.Chaosflix.Tests.Fakes;

namespace Jellyfin.Plugin.Chaosflix.Tests;

public class CccApiClientTests
{
    private readonly FakeCccApi _api = new();

    [Fact]
    public async Task GetConferencesMapsSnakeCaseJson()
    {
        _api.Json("/public/conferences", JsonDocumentFrom("""
            {"conferences":[{"acronym":"38c3","title":"38C3","slug":"congress/2024","logo_url":"https://x/logo.png",
             "event_last_released_at":"2025-01-02T10:00:00+01:00","url":"https://api/38c3"}]}
            """));
        var client = _api.CreateApiClient();

        var conferences = await client.GetConferencesAsync(CancellationToken.None);

        var conf = Assert.Single(conferences);
        Assert.Equal("38c3", conf.Acronym);
        Assert.Equal("https://x/logo.png", conf.LogoUrl);
        Assert.Equal(new DateTimeOffset(2025, 1, 2, 9, 0, 0, TimeSpan.Zero), conf.EventLastReleasedAt);
    }

    [Fact]
    public async Task GetEventMapsRecordingsAndRelated()
    {
        _api.Json("/public/events/abc", JsonDocumentFrom("""
            {"guid":"abc","title":"T","persons":["Alice"],"tags":["Security"],"view_count":1234,"duration":2700,
             "original_language":"deu","recordings":[{"folder":"h264-hd","mime_type":"video/mp4","high_quality":true,
             "language":"deu","width":1920,"height":1080,"size":512,"length":2700,"recording_url":"https://cdn/x.mp4"}],
             "related":[{"event_guid":"def","weight":7}]}
            """));
        var client = _api.CreateApiClient();

        var ev = await client.GetEventAsync("abc", CancellationToken.None);

        Assert.NotNull(ev);
        Assert.Equal(new[] { "Alice" }, ev.Persons);
        Assert.Equal(1234, ev.ViewCount);
        var rec = Assert.Single(ev.Recordings!);
        Assert.Equal("h264-hd", rec.Folder);
        Assert.True(rec.HighQuality);
        Assert.Equal("https://cdn/x.mp4", rec.RecordingUrl);
        Assert.Equal(7, Assert.Single(ev.Related!).Weight);
    }

    [Fact]
    public async Task ResponsesAreCachedPerKey()
    {
        _api.Json("/public/conferences/38c3", TestData.Conference("38c3", null));
        _api.Json("/public/conferences/37c3", TestData.Conference("37c3", null));
        var client = _api.CreateApiClient();

        await client.GetConferenceAsync("38c3", CancellationToken.None);
        await client.GetConferenceAsync("38c3", CancellationToken.None);
        await client.GetConferenceAsync("37c3", CancellationToken.None);

        Assert.Equal(1, _api.CountRequests("/public/conferences/38c3"));
        Assert.Equal(1, _api.CountRequests("/public/conferences/37c3"));
    }

    [Fact]
    public async Task ClearCacheRefetches()
    {
        _api.Json("/public/conferences", new CccConferencesResponse());
        var client = _api.CreateApiClient();

        await client.GetConferencesAsync(CancellationToken.None);
        client.ClearCache();
        await client.GetConferencesAsync(CancellationToken.None);

        Assert.Equal(2, _api.CountRequests("/public/conferences"));
    }

    [Fact]
    public async Task SearchEscapesQuery()
    {
        _api.Json("/public/events/search?q=rust%20%26%20c", new CccEventsResponse { Events = [TestData.Event("e1")] });
        var client = _api.CreateApiClient();

        var events = await client.SearchEventsAsync("rust & c", CancellationToken.None);

        Assert.Equal("e1", Assert.Single(events).Guid);
    }

    [Fact]
    public async Task GetConferenceEscapesTheAcronym()
    {
        // 'Panoptische _Prinzip' is live on media.ccc.de and answers only as %20 (#102).
        _api.Json(
            "/public/conferences/Panoptische%20_Prinzip",
            TestData.Conference("Panoptische _Prinzip", null, TestData.Event("e1")));
        var client = _api.CreateApiClient();

        var conference = await client.GetConferenceAsync("Panoptische _Prinzip", CancellationToken.None);

        Assert.Equal("e1", Assert.Single(conference!.Events!).Guid);
        Assert.Equal("/public/conferences/Panoptische%20_Prinzip", _api.Requests.Single());
    }

    [Fact]
    public async Task GetEventEscapesTheGuid()
    {
        _api.Json("/public/events/weird%20guid%2F1", TestData.Event("weird guid/1"));
        var client = _api.CreateApiClient();

        var cccEvent = await client.GetEventAsync("weird guid/1", CancellationToken.None);

        Assert.Equal("weird guid/1", cccEvent!.Guid);
        Assert.Equal("/public/events/weird%20guid%2F1", _api.Requests.Single());
    }

    [Fact]
    public async Task ServerErrorsPropagateAndAreNotCached()
    {
        _api.Status("/public/conferences", HttpStatusCode.InternalServerError);
        var client = _api.CreateApiClient();

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetConferencesAsync(CancellationToken.None));
        _api.Json("/public/conferences", new CccConferencesResponse { Conferences = [TestData.Conference("x", null)] });

        Assert.Single(await client.GetConferencesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ResolveRedirectFollowsAbsoluteLocation()
    {
        using var cdn = new FakeCdn();
        var target = cdn.AddFile("/mirror/talk.mp4", [1, 2, 3]);
        var url = cdn.AddRedirect("/cdn/talk.mp4", target);
        var client = _api.CreateApiClient();

        Assert.Equal(target, await client.ResolveRedirectAsync(url, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveRedirectResolvesRelativeLocation()
    {
        using var cdn = new FakeCdn();
        cdn.AddFile("/mirror/talk.mp4", [1, 2, 3]);
        var url = cdn.AddRedirect("/cdn/talk.mp4", "/mirror/talk.mp4");
        var client = _api.CreateApiClient();

        Assert.Equal($"{cdn.BaseUrl}/mirror/talk.mp4", await client.ResolveRedirectAsync(url, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveRedirectKeepsUrlWithoutRedirect()
    {
        using var cdn = new FakeCdn();
        var url = cdn.AddFile("/talk.mp4", [1]);
        var client = _api.CreateApiClient();

        Assert.Equal(url, await client.ResolveRedirectAsync(url, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveRedirectFallsBackToOriginalOnConnectionError()
    {
        string url;
        using (var cdn = new FakeCdn())
        {
            url = $"{cdn.BaseUrl}/gone.mp4";
        }

        var client = _api.CreateApiClient();

        Assert.Equal(url, await client.ResolveRedirectAsync(url, CancellationToken.None));
    }

    [Fact]
    public async Task SubtitleRecordingWithoutSizeOrLengthIsParsed()
    {
        // Verbatim from api.media.ccc.de: a subtitle recording carries null where a
        // video carries numbers, which a non-nullable int would refuse outright and
        // take the whole talk down with it.
        _api.Json("/public/events/e1", JsonDocumentFrom("""
            {"guid":"e1","title":"Talk","recordings":[
              {"length":null,"mime_type":"application/x-subrip","language":"fin",
               "filename":"talk.fi.srt","state":"translated","folder":"","high_quality":true,
               "width":null,"height":null,"size":null,
               "recording_url":"https://cdn.media.ccc.de/congress/2024/talk.fi.srt"}]}
            """).RootElement);

        var recording = Assert.Single((await _api.CreateApiClient().GetEventAsync("e1", CancellationToken.None))!.Recordings!);

        Assert.Null(recording.Size);
        Assert.Null(recording.Length);
        Assert.Null(recording.Width);
        Assert.Equal("translated", recording.State);
    }

    private static System.Text.Json.JsonDocument JsonDocumentFrom(string json) =>
        System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetBytes(json));
}
