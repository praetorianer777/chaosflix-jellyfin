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

    private static System.Text.Json.JsonDocument JsonDocumentFrom(string json) =>
        System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetBytes(json));
}
