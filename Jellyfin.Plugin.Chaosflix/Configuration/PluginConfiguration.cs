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

    /// <summary>
    /// Gets or sets a value indicating whether a talk starts on a version every client
    /// can stream without a re-encode, leaving <see cref="PreferredFormat"/> to order
    /// the versions behind it.
    /// </summary>
    public bool CompatibleDefaultVersion { get; set; }

    /// <summary>
    /// Gets or sets the CCC API endpoint. Empty means the public API at media.ccc.de;
    /// set it to use a mirror or, in the e2e tests, a local stand-in.
    /// </summary>
    public string ApiBaseUrl { get; set; } = string.Empty;
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
