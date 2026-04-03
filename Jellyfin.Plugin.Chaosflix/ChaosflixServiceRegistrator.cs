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
        serviceCollection.AddSingleton<CccApiClient>();
        serviceCollection.AddSingleton<IChannel, ChaosflixChannel>();
    }
}
