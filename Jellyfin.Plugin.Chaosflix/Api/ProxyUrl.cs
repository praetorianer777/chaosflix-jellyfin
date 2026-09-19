using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Chaosflix.Api;

/// <summary>
/// Reads back a proxy url the channel handed out, so a diagnostic can check it and show
/// it without handing the reader a url they could stream through.
/// </summary>
internal static class ProxyUrl
{
    private const string FolderParameter = "recordingFolder";
    private const string LanguageParameter = "language";

    /// <summary>
    /// Renders the url without its signature. The result is rebuilt from the parts that
    /// were understood rather than edited in place, so nothing of the signature can
    /// survive a url shaped differently than expected.
    /// </summary>
    internal static string Redact(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "<unparseable proxy url>";
        }

        var query = Parse(uri);
        var parts = new List<string>();
        if (query.TryGetValue(FolderParameter, out var folder))
        {
            parts.Add($"{FolderParameter}={folder}");
        }

        if (query.TryGetValue(LanguageParameter, out var language))
        {
            parts.Add($"{LanguageParameter}={language}");
        }

        parts.Add($"{ProxySignature.QueryParameter}=<redacted>");
        return $"{uri.GetLeftPart(UriPartial.Path)}?{string.Join('&', parts)}";
    }

    /// <summary>
    /// Whether the url carries a signature the proxy endpoint would accept for exactly
    /// the recording the url names.
    /// </summary>
    internal static bool IsSignedCorrectly(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var guid = uri.Segments.LastOrDefault()?.TrimEnd('/');
        if (string.IsNullOrEmpty(guid))
        {
            return false;
        }

        var query = Parse(uri);
        query.TryGetValue(ProxySignature.QueryParameter, out var signature);
        return ProxySignature.Verify(
            Uri.UnescapeDataString(guid),
            query.GetValueOrDefault(FolderParameter),
            query.GetValueOrDefault(LanguageParameter),
            signature);
    }

    private static Dictionary<string, string> Parse(Uri uri)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                query[pair[..separator]] = Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return query;
    }
}
