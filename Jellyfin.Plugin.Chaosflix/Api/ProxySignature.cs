using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Chaosflix.Api;

/// <summary>
/// Signs the proxy URLs the channel hands out, so the endpoint only serves
/// recordings this plugin selected. Without a signature anyone who can reach
/// the server could stream arbitrary CCC recordings through it.
/// </summary>
public static class ProxySignature
{
    /// <summary>Query parameter carrying the signature.</summary>
    public const string QueryParameter = "t";

    /// <summary>
    /// Secret for this server process. It is deliberately not persisted: media
    /// sources are handed out per playback, so a restart simply means the next
    /// request gets a freshly signed url, and nothing has to be stored or
    /// migrated.
    /// </summary>
    public static string Secret { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Signs one recording. The signature covers exactly the parameters the
    /// controller acts on, so none of them can be swapped afterwards.
    /// </summary>
    public static string Create(string eventGuid, string? recordingFolder, string? language)
    {
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{eventGuid}\n{recordingFolder ?? string.Empty}\n{language ?? string.Empty}");

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Verifies a signature in constant time.
    /// </summary>
    public static bool Verify(string eventGuid, string? recordingFolder, string? language, string? signature)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }

        var expected = Create(eventGuid, recordingFolder, language);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature));
    }
}
