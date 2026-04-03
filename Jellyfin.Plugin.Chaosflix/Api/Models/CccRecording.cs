using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Chaosflix.Api.Models;

/// <summary>
/// CCC API recording (a specific video/audio file for an event).
/// </summary>
public class CccRecording
{
    /// <summary>Gets or sets the file size in MB.</summary>
    [JsonPropertyName("size")]
    public int Size { get; set; }

    /// <summary>Gets or sets the length in seconds.</summary>
    [JsonPropertyName("length")]
    public int Length { get; set; }

    /// <summary>Gets or sets the MIME type.</summary>
    [JsonPropertyName("mime_type")]
    public string MimeType { get; set; } = string.Empty;

    /// <summary>Gets or sets the language.</summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets the filename.</summary>
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    /// <summary>Gets or sets the state.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets the folder (e.g. "h264-hd", "webm-sd").</summary>
    [JsonPropertyName("folder")]
    public string Folder { get; set; } = string.Empty;

    /// <summary>Gets or sets whether this is the high quality version.</summary>
    [JsonPropertyName("high_quality")]
    public bool HighQuality { get; set; }

    /// <summary>Gets or sets the width.</summary>
    [JsonPropertyName("width")]
    public int Width { get; set; }

    /// <summary>Gets or sets the height.</summary>
    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>Gets or sets the direct recording URL.</summary>
    [JsonPropertyName("recording_url")]
    public string RecordingUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the API URL.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the updated timestamp.</summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// Related event reference.
/// </summary>
public class CccRelatedEvent
{
    /// <summary>Gets or sets the related event GUID.</summary>
    [JsonPropertyName("event_guid")]
    public string EventGuid { get; set; } = string.Empty;

    /// <summary>Gets or sets the relationship weight.</summary>
    [JsonPropertyName("weight")]
    public int Weight { get; set; }
}
