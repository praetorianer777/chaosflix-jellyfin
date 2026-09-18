using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Chaosflix.Tests.Fakes;

/// <summary>
/// Real HTTP server on loopback standing in for cdn.media.ccc.de and its mirrors.
/// Needed because the stream proxy and redirect resolution use their own HttpClient.
/// </summary>
public sealed partial class FakeCdn : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _redirects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _contentTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _statuses = new(StringComparer.Ordinal);

    public FakeCdn()
    {
        Port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{Port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    public string BaseUrl { get; }

    public List<(string Method, string Path, string? Range)> Requests { get; } = new();

    public string AddFile(string path, byte[] content, string? contentType = "video/mp4")
    {
        _files[path] = content;
        _statuses.Remove(path);
        if (contentType != null)
        {
            _contentTypes[path] = contentType;
        }

        return BaseUrl + path;
    }

    /// <summary>Serves an error status for <paramref name="path"/>, like a mirror that lost the file.</summary>
    public string AddStatus(string path, int statusCode)
    {
        _files.Remove(path);
        _statuses[path] = statusCode;
        return BaseUrl + path;
    }

    public string AddRedirect(string path, string location)
    {
        _redirects[path] = location;
        return BaseUrl + path;
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }

    private static int GetFreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (!_listener.IsListening)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }

            try
            {
                Handle(ctx);
            }
            catch (HttpListenerException)
            {
                // Client (e.g. a HEAD request) went away mid-response.
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        var range = ctx.Request.Headers["Range"];
        lock (Requests)
        {
            Requests.Add((ctx.Request.HttpMethod, path, range));
        }

        using var response = ctx.Response;

        if (_statuses.TryGetValue(path, out var status))
        {
            response.StatusCode = status;
            return;
        }

        if (_redirects.TryGetValue(path, out var location))
        {
            response.StatusCode = 302;
            response.Headers["Location"] = location;
            return;
        }

        if (!_files.TryGetValue(path, out var content))
        {
            response.StatusCode = 404;
            return;
        }

        var start = 0L;
        var end = content.Length - 1L;
        response.StatusCode = 200;

        if (range != null && RangePattern().Match(range) is { Success: true } m)
        {
            start = long.Parse(m.Groups[1].Value);
            if (m.Groups[2].Success)
            {
                end = Math.Min(end, long.Parse(m.Groups[2].Value));
            }

            response.StatusCode = 206;
            response.Headers["Content-Range"] = $"bytes {start}-{end}/{content.Length}";
        }

        if (_contentTypes.TryGetValue(path, out var type))
        {
            response.ContentType = type;
        }

        response.Headers["Accept-Ranges"] = "bytes";
        response.ContentLength64 = end - start + 1;

        if (ctx.Request.HttpMethod != "HEAD")
        {
            response.OutputStream.Write(content, (int)start, (int)(end - start + 1));
        }
    }

    [GeneratedRegex(@"^bytes=(\d+)-(\d+)?$")]
    private static partial Regex RangePattern();
}
