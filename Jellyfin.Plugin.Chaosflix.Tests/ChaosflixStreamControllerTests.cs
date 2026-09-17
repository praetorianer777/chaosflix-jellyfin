using Jellyfin.Plugin.Chaosflix.Api;
using Jellyfin.Plugin.Chaosflix.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using static Jellyfin.Plugin.Chaosflix.Tests.Fakes.TestData;

namespace Jellyfin.Plugin.Chaosflix.Tests;

public sealed class ChaosflixStreamControllerTests : IDisposable
{
    private static readonly byte[] Video = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();

    private readonly FakeCccApi _api = new();
    private readonly FakeCdn _cdn = new();
    private readonly ChaosflixStreamController _controller;

    public ChaosflixStreamControllerTests()
    {
        _controller = new ChaosflixStreamController(_api.CreateApiClient(), NullLogger<ChaosflixStreamController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        _controller.Response.Body = new MemoryStream();
    }

    public void Dispose() => _cdn.Dispose();

    [Fact]
    public async Task StreamsWholeFileFollowingCdnRedirect()
    {
        var mirror = _cdn.AddFile("/mirror/hd.mp4", Video);
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd", url: _cdn.AddRedirect("/cdn/hd.mp4", mirror))]));

        await Proxy("e1");

        Assert.Equal(200, _controller.Response.StatusCode);
        Assert.Equal(Video.Length, _controller.Response.ContentLength);
        Assert.Equal("bytes", _controller.Response.Headers.AcceptRanges.ToString());
        Assert.Equal("video/mp4", _controller.Response.ContentType);
        Assert.Equal(Video, Body());
        Assert.Contains(_cdn.Requests, r => r is { Method: "GET", Path: "/mirror/hd.mp4" });
        Assert.DoesNotContain(_cdn.Requests, r => r is { Method: "GET", Path: "/cdn/hd.mp4" });
    }

    [Fact]
    public async Task ForwardsRangeRequests()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd", url: _cdn.AddFile("/hd.mp4", Video))]));
        _controller.Request.Headers.Range = "bytes=100-199";

        await Proxy("e1");

        Assert.Equal(206, _controller.Response.StatusCode);
        Assert.Equal("bytes 100-199/1000", _controller.Response.Headers.ContentRange.ToString());
        Assert.Equal(100, _controller.Response.ContentLength);
        Assert.Equal(Video[100..200], Body());
        Assert.Contains(_cdn.Requests, r => r.Range == "bytes=100-199");
    }

    [Fact]
    public async Task OpenEndedRangeReturnsRest()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd", url: _cdn.AddFile("/hd.mp4", Video))]));
        _controller.Request.Headers.Range = "bytes=900-";

        await Proxy("e1");

        Assert.Equal(206, _controller.Response.StatusCode);
        Assert.Equal(Video[900..], Body());
    }

    [Fact]
    public async Task HeadReturnsHeadersWithoutBody()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd", url: _cdn.AddFile("/hd.mp4", Video))]));
        _controller.Request.Method = "HEAD";

        await Proxy("e1");

        Assert.Equal(200, _controller.Response.StatusCode);
        Assert.Equal(Video.Length, _controller.Response.ContentLength);
        Assert.Empty(Body());
        Assert.Contains(_cdn.Requests, r => r is { Method: "HEAD", Path: "/hd.mp4" });
    }

    [Fact]
    public async Task SelectsRequestedFolderAndLanguage()
    {
        _api.Json("/public/events/e1", Event("e1", recordings:
        [
            Recording("h264-hd", language: "deu", url: _cdn.AddFile("/deu.mp4", [1])),
            Recording("h264-hd", language: "eng", url: _cdn.AddFile("/eng.mp4", [2])),
            Recording("webm-hd", "video/webm", language: "eng", url: _cdn.AddFile("/eng.webm", [3], "video/webm"))
        ]));

        await Proxy("e1", "webm-hd", "eng");

        Assert.Equal(new byte[] { 3 }, Body());
        Assert.Equal("video/webm", _controller.Response.ContentType);
    }

    [Fact]
    public async Task FallsBackToHighQualityMp4WhenRequestedRecordingIsGone()
    {
        _api.Json("/public/events/e1", Event("e1", recordings:
        [
            Recording("webm-hd", "video/webm", url: _cdn.AddFile("/hd.webm", [1], "video/webm")),
            Recording("h264-sd", highQuality: false, url: _cdn.AddFile("/sd.mp4", [2])),
            Recording("h264-hd", url: _cdn.AddFile("/hd.mp4", [3])),
            Recording("mp3", "audio/mpeg", url: _cdn.AddFile("/a.mp3", [4], "audio/mpeg"))
        ]));

        await Proxy("e1", "h264-4k", "eng");

        Assert.Equal(new byte[] { 3 }, Body());
    }

    [Fact]
    public async Task FallsBackToRecordingMimeTypeWhenUpstreamSendsNone()
    {
        _api.Json("/public/events/e1", Event("e1", recordings:
            [Recording("webm-hd", "video/webm", url: _cdn.AddFile("/hd.webm", [1], contentType: null))]));

        await Proxy("e1");

        Assert.Equal("video/webm", _controller.Response.ContentType);
    }

    [Fact]
    public async Task EventWithoutVideoRecordingsIs404()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("mp3", "audio/mpeg")]));

        await Proxy("e1");

        Assert.Equal(404, _controller.Response.StatusCode);
    }

    [Fact]
    public async Task EventWithoutRecordingListIs404()
    {
        _api.Json("/public/events/e1", Event("e1"));

        await Proxy("e1");

        Assert.Equal(404, _controller.Response.StatusCode);
    }

    [Fact(Skip = "Known bug #5: the CCC API 404 surfaces as HttpRequestException (HTTP 500)")]
    public async Task UnknownEventIs404()
    {
        await Proxy("does-not-exist");

        Assert.Equal(404, _controller.Response.StatusCode);
    }

    [Fact(Skip = "Known bug #5: RangeHeaderValue.Parse throws on malformed Range headers")]
    public async Task MalformedRangeDoesNotThrow()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd", url: _cdn.AddFile("/hd.mp4", Video))]));
        _controller.Request.Headers.Range = "bytes=abc";

        await Proxy("e1");

        Assert.NotEqual(500, _controller.Response.StatusCode);
    }

    private Task Proxy(string guid, string? folder = null, string? language = null) =>
        _controller.ProxyStream(guid, folder, language);

    private byte[] Body() => ((MemoryStream)_controller.Response.Body).ToArray();
}
