using System.Text.RegularExpressions;
using Jellyfin.Plugin.Chaosflix.Tests.Fakes;

namespace Jellyfin.Plugin.Chaosflix.Tests;

[Collection(PluginCollection.Name)]
public class ConfigPageTests
{
    private static (Plugin Plugin, string Html) LoadConfigPage()
    {
        var plugin = TestPlugin.Configure();
        var resource = Assert.Single(plugin.GetPages()).EmbeddedResourcePath;

        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resource);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return (plugin, reader.ReadToEnd());
    }

    [Fact]
    public void ConfigPageUsesThePluginId()
    {
        var (plugin, html) = LoadConfigPage();

        var ids = Regex
            .Matches(html, "[0-9a-zA-Z]{8}-[0-9a-zA-Z]{4}-[0-9a-zA-Z]{4}-[0-9a-zA-Z]{4}-[0-9a-zA-Z]{12}")
            .Select(match => match.Value)
            .Distinct()
            .ToList();

        Assert.Equal(new[] { plugin.Id.ToString() }, ids);
    }

    /// <summary>
    /// jellyfin-web keeps only the <c>div[data-role="page"]</c> of a plugin
    /// configuration page; a script outside that element is dropped silently.
    /// </summary>
    [Fact]
    public void ConfigPageScriptLivesInsideThePageElement()
    {
        var (_, html) = LoadConfigPage();

        var pageStart = html.IndexOf("data-role=\"page\"", StringComparison.Ordinal);
        var pageEnd = html.LastIndexOf("</div>", StringComparison.Ordinal);
        var scriptStart = html.IndexOf("<script", StringComparison.Ordinal);
        var scriptEnd = html.IndexOf("</script>", StringComparison.Ordinal);

        Assert.InRange(scriptStart, pageStart, pageEnd);
        Assert.InRange(scriptEnd, scriptStart, pageEnd);
    }
}
