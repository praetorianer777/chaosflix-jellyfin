using Jellyfin.Plugin.Chaosflix.Api;
using Jellyfin.Plugin.Chaosflix.Api.Models;
using Jellyfin.Plugin.Chaosflix.Tests.Fakes;

namespace Jellyfin.Plugin.Chaosflix.Tests;

[Collection(PluginCollection.Name)]
public class ApiBaseUrlTests
{
    private readonly FakeCccApi _api = new();

    [Fact]
    public async Task UsesPublicApiByDefault()
    {
        TestPlugin.Configure();
        _api.Json("/public/conferences", new CccConferencesResponse());

        await _api.CreateApiClient().GetConferencesAsync(CancellationToken.None);

        Assert.Equal("https://api.media.ccc.de/public/conferences", Assert.Single(_api.RequestUris));
    }

    [Fact]
    public async Task UsesConfiguredEndpointForEveryRoute()
    {
        TestPlugin.Configure(c => c.ApiBaseUrl = "http://fake-ccc:3000/public/");
        _api.Json("/public/conferences", new CccConferencesResponse());
        _api.Json("/public/conferences/e2e", TestData.Conference("e2e", null));
        _api.Json("/public/events/e1", TestData.Event("e1"));
        _api.Json("/public/events/search?q=x", new CccEventsResponse());
        var client = _api.CreateApiClient();

        await client.GetConferencesAsync(CancellationToken.None);
        await client.GetConferenceAsync("e2e", CancellationToken.None);
        await client.GetEventAsync("e1", CancellationToken.None);
        await client.SearchEventsAsync("x", CancellationToken.None);

        Assert.Equal(
            new[]
            {
                "http://fake-ccc:3000/public/conferences",
                "http://fake-ccc:3000/public/conferences/e2e",
                "http://fake-ccc:3000/public/events/e1",
                "http://fake-ccc:3000/public/events/search?q=x"
            },
            _api.RequestUris);
    }
}
