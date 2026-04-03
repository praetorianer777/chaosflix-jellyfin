using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Chaosflix.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the preferred video quality.
    /// </summary>
    public VideoQuality PreferredQuality { get; set; } = VideoQuality.High;

    /// <summary>
    /// Gets or sets the preferred language (ISO 639 code, e.g. "deu", "eng").
    /// Empty means original language.
    /// </summary>
    public string PreferredLanguage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the preferred video format.
    /// </summary>
    public VideoFormat PreferredFormat { get; set; } = VideoFormat.Mp4;
}

/// <summary>
/// Video quality preference.
/// </summary>
public enum VideoQuality
{
    /// <summary>High quality (1080p).</summary>
    High,

    /// <summary>Standard quality (576p).</summary>
    Standard
}

/// <summary>
/// Video format preference.
/// </summary>
public enum VideoFormat
{
    /// <summary>H.264 MP4.</summary>
    Mp4,

    /// <summary>WebM/VP9.</summary>
    WebM
}
