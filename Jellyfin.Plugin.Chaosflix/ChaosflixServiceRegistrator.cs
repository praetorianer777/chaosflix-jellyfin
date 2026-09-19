using System.Net.Http;
using Jellyfin.Plugin.Chaosflix.Api;
using Jellyfin.Plugin.Chaosflix.Channel;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Chaosflix;

/// <summary>
/// Registers plugin services in the DI container.
/// </summary>
public class ChaosflixServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Redirect resolution needs the 302 itself, not the mirror behind it.
        serviceCollection
            .AddHttpClient(CccApiClient.RedirectClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        serviceCollection.AddSingleton<CccApiClient>();

        // The status endpoint reports this channel's caches, so it has to reach the very
        // instance Jellyfin serves from — registering the class twice would give it a
        // second one with empty caches.
        serviceCollection.AddSingleton<ChaosflixChannel>();
        serviceCollection.AddSingleton<IChannel>(sp => sp.GetRequiredService<ChaosflixChannel>());
        serviceCollection.AddHostedService<ChaosflixUserDataMirror>();
    }
}
