using System.Net;
using System.Net.Http.Json;
using Jellyfin.Plugin.Chaosflix.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Chaosflix.Tests.Fakes;

/// <summary>
/// In-process stand-in for api.media.ccc.de, routed by path and query.
/// Unknown routes return 404 like the real API.
/// </summary>
public sealed class FakeCccApi : HttpMessageHandler, IHttpClientFactory
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
    private HttpClient? _redirectClient;

    public List<string> Requests { get; } = new();

    public List<string> RequestUris { get; } = new();

    public FakeCccApi Json(string pathAndQuery, object body)
    {
        _routes[pathAndQuery] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        return this;
    }

    public FakeCccApi Status(string pathAndQuery, HttpStatusCode status)
    {
        _routes[pathAndQuery] = () => new HttpResponseMessage(status);
        return this;
    }

    public int CountRequests(string pathAndQuery) => Requests.Count(r => r == pathAndQuery);

    public CccApiClient CreateApiClient() => new(this, NullLogger<CccApiClient>.Instance);

    // The client must not dispose the shared handler, otherwise a second
    // CreateClient call (or a disposed CccApiClient) breaks later requests.
    // Redirect resolution must reach the real loopback CDN instead of this fake,
    // and must see the 302 rather than following it.
    public HttpClient CreateClient(string name) => name == CccApiClient.RedirectClientName
        ? _redirectClient ??= new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        : new HttpClient(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.RequestUri!.PathAndQuery;
        lock (Requests)
        {
            Requests.Add(key);
            RequestUris.Add(request.RequestUri.ToString());
        }

        return Task.FromResult(_routes.TryGetValue(key, out var route)
            ? route()
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
