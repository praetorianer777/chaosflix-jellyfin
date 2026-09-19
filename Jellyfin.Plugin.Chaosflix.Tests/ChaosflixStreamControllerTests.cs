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
    private readonly CccApiClient _apiClient;
    private readonly ChaosflixStreamController _controller;

    public ChaosflixStreamControllerTests()
    {
        _apiClient = _api.CreateApiClient();
        _controller = NewController();
    }

    // Every request gets its own controller, but they share the api client so that
    // its redirect cache survives across requests the way it does on a live server.
    private ChaosflixStreamController NewController()
    {
        var controller = new ChaosflixStreamController(_apiClient, NullLogger<ChaosflixStreamController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Response.Body = new MemoryStream();
        return controller;
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
    public async Task UnsatisfiableRangeKeepsItsContentRange()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd", url: _cdn.AddFile("/hd.mp4", Video))]));
        _controller.Request.Headers.Range = "bytes=5000-";

        await Proxy("e1");

        Assert.Equal(416, _controller.Response.StatusCode);
        Assert.Equal("bytes */1000", _controller.Response.Headers.ContentRange.ToString());
        Assert.Empty(_controller.Response.Headers.AcceptRanges.ToString());
        Assert.Null(_controller.Response.ContentType);
        Assert.Empty(Body());
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

    [Fact]
    public async Task UnknownEventIs404()
    {
        await Proxy("does-not-exist");

        Assert.Equal(404, _controller.Response.StatusCode);
    }

    [Fact]
    public async Task MalformedRangeDoesNotThrow()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd", url: _cdn.AddFile("/hd.mp4", Video))]));
        _controller.Request.Headers.Range = "bytes=abc";

        await Proxy("e1");

        Assert.NotEqual(500, _controller.Response.StatusCode);
    }

    [Fact]
    public async Task DeadMirrorIsDroppedSoTheNextPlaybackSucceeds()
    {
        string deadMirror;
        using (var goneMirror = new FakeCdn())
        {
            deadMirror = goneMirror.BaseUrl + "/mirror-a/hd.mp4";
        }

        var cdnUrl = _cdn.AddRedirect("/cdn/hd.mp4", deadMirror);
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd", url: cdnUrl)]));

        await Proxy("e1");

        Assert.Equal(502, _controller.Response.StatusCode);

        _cdn.AddRedirect("/cdn/hd.mp4", _cdn.AddFile("/mirror-b/hd.mp4", Video));
        var second = NewController();

        await second.ProxyStream("e1", t: Sign("e1", null, null));

        Assert.Equal(200, second.Response.StatusCode);
        Assert.Equal(Video, ((MemoryStream)second.Response.Body).ToArray());
    }

    [Fact]
    public async Task MirrorErrorIsRetriedThroughTheCdn()
    {
        var mirror = _cdn.AddStatus("/mirror-a/hd.mp4", 503);
        var cdnUrl = _cdn.AddRedirect("/cdn/hd.mp4", mirror);
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd", url: cdnUrl)]));

        await Proxy("e1");

        Assert.Contains(_cdn.Requests, r => r is { Method: "GET", Path: "/cdn/hd.mp4" });
        Assert.Equal(503, _controller.Response.StatusCode);
    }

    [Fact]
    public async Task UpstreamErrorIsNotDressedUpAsMedia()
    {
        _api.Json("/public/events/e1", Event("e1", recordings:
            [Recording("h264-hd", url: _cdn.AddStatus("/hd.mp4", 500))]));

        await Proxy("e1");

        Assert.Equal(500, _controller.Response.StatusCode);
        Assert.Empty(_controller.Response.Headers.AcceptRanges.ToString());
        Assert.Null(_controller.Response.ContentType);
        Assert.Empty(Body());
    }

    [Fact]
    public async Task AbortedRequestStopsBeforeTheCdnIsAsked()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd", url: _cdn.AddFile("/hd.mp4", Video))]));
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        _controller.ControllerContext.HttpContext.RequestAborted = aborted.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Proxy("e1"));

        Assert.Empty(_cdn.Requests);
    }

    // Once the body is on the wire the exception middleware can no longer turn an
    // exception into a response and only logs that the response has already started
    // (#58) — so a client leaving mid-file has to end the request quietly.
    [Fact]
    public async Task ClientHangingUpMidBodyEndsTheStreamQuietly()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd", url: _cdn.AddFile("/hd.mp4", Video))]));
        using var aborted = new CancellationTokenSource();
        _controller.ControllerContext.HttpContext.RequestAborted = aborted.Token;
        _controller.Response.Body = new HangUpOnWriteStream(aborted);

        await Proxy("e1");

        Assert.Equal(200, _controller.Response.StatusCode);
    }

    /// <summary>Kestrel's response body once the client is gone: the request is
    /// aborted and the write fails with that token.</summary>
    private sealed class HangUpOnWriteStream : Stream
    {
        private readonly CancellationTokenSource _aborted;

        public HangUpOnWriteStream(CancellationTokenSource aborted) => _aborted = aborted;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            _aborted.Cancel();
            throw new OperationCanceledException(_aborted.Token);
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Flush()
        {
        }
    }

    // The proxy only serves urls it signed itself (#2); tests go through the
    // same signature the channel puts on the media source path.
    [Fact]
    public async Task UnsignedRequestIsRejected()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd", url: _cdn.AddFile("/hd.mp4", Video))]));

        await _controller.ProxyStream("e1", recordingFolder: "h264-hd", language: "eng", t: null);

        Assert.Equal(401, _controller.Response.StatusCode);
        Assert.Empty(Body());
        Assert.Empty(_cdn.Requests);
    }

    [Fact]
    public async Task SignatureForAnotherRecordingIsRejected()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd", url: _cdn.AddFile("/hd.mp4", Video))]));

        // Signed for the SD recording, used for the HD one.
        await _controller.ProxyStream("e1", recordingFolder: "h264-hd", language: "eng", t: Sign("e1", "h264-sd", "eng"));

        Assert.Equal(401, _controller.Response.StatusCode);
        Assert.Empty(_cdn.Requests);
    }

    [Fact]
    public async Task SignatureOfAnotherEventIsRejected()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd", url: _cdn.AddFile("/hd.mp4", Video))]));

        await _controller.ProxyStream("e1", recordingFolder: "h264-hd", language: "eng", t: Sign("other-event", "h264-hd", "eng"));

        Assert.Equal(401, _controller.Response.StatusCode);
        Assert.Empty(_cdn.Requests);
    }

    [Fact]
    public async Task ServesASubtitleWithTheMimeTypeTheApiDeclares()
    {
        var srt = "1\n00:00:00,000 --> 00:00:04,000\nHello\n"u8.ToArray();
        var mirror = _cdn.AddFile("/mirror/talk.fi.srt", srt, "application/octet-stream");
        _api.Json("/public/events/e1", Event("e1", recordings:
        [
            Recording("h264-hd", url: _cdn.AddFile("/hd.mp4", Video)),
            Subtitle("talk.fi.srt", language: "fin", url: _cdn.AddRedirect("/cdn/talk.fi.srt", mirror))
        ]));

        await ProxySubtitle("e1", "talk.fi.srt");

        Assert.Equal(200, _controller.Response.StatusCode);
        Assert.Equal(srt, Body());
        // The mirror answers application/octet-stream, which no client renders as
        // a caption track.
        Assert.Equal("application/x-subrip", _controller.Response.ContentType);
    }

    [Fact]
    public async Task SubtitleOfAnotherFilenameIsRejected()
    {
        _api.Json("/public/events/e1", Event("e1", recordings:
        [
            Subtitle("a.srt", url: _cdn.AddFile("/a.srt", [1])),
            Subtitle("b.srt", language: "fin", url: _cdn.AddFile("/b.srt", [2]))
        ]));

        await ProxySubtitle("e1", "b.srt", ProxySignature.Create("e1", null, null, "a.srt"));

        Assert.Equal(401, _controller.Response.StatusCode);
        Assert.Empty(_cdn.Requests);
    }

    [Fact]
    public async Task UnknownSubtitleFilenameIs404()
    {
        _api.Json("/public/events/e1", Event("e1", recordings: [Recording("h264-hd", url: _cdn.AddFile("/hd.mp4", Video))]));

        await ProxySubtitle("e1", "missing.srt");

        Assert.Equal(404, _controller.Response.StatusCode);
        Assert.Empty(_cdn.Requests);
    }

    private static string Sign(string guid, string? folder, string? language) =>
        ProxySignature.Create(guid, folder, language);

    private Task Proxy(string guid, string? folder = null, string? language = null) =>
        _controller.ProxyStream(guid, recordingFolder: folder, language: language, t: Sign(guid, folder, language));

    // Subtitles carry their signature and filename in the path, so the url ends in
    // the file extension clients derive the subtitle format from.
    private Task ProxySubtitle(string guid, string filename, string? signature = null) =>
        _controller.ProxyStream(
            guid,
            signature: signature ?? ProxySignature.Create(guid, null, null, filename),
            filename: filename);

    private byte[] Body() => ((MemoryStream)_controller.Response.Body).ToArray();
}
