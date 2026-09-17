using Jellyfin.Plugin.Chaosflix.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using NSubstitute;

namespace Jellyfin.Plugin.Chaosflix.Tests.Fakes;

/// <summary>
/// Tests touching <see cref="Plugin.Instance"/> share static state and must not run in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginCollection
{
    public const string Name = "Plugin singleton";
}

public static class TestPlugin
{
    public static Plugin Configure(Action<PluginConfiguration>? configure = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "chaosflix-tests");
        Directory.CreateDirectory(dir);

        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginsPath.Returns(dir);
        paths.PluginConfigurationsPath.Returns(dir);

        var plugin = new Plugin(paths, Substitute.For<IXmlSerializer>());
        var config = new PluginConfiguration();
        configure?.Invoke(config);
        plugin.UpdateConfiguration(config);
        return plugin;
    }
}
