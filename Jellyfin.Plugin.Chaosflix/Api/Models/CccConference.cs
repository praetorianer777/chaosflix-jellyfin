using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Chaosflix.Api.Models;

/// <summary>
/// CCC API conference (e.g. 38C3, Camp 2019).
/// </summary>
public class CccConference
{
    /// <summary>Gets or sets the acronym (e.g. "38c3").</summary>
    [JsonPropertyName("acronym")]
    public string Acronym { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Gets or sets the slug.</summary>
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    /// <summary>Gets or sets the logo URL.</summary>
    [JsonPropertyName("logo_url")]
    public string? LogoUrl { get; set; }

    /// <summary>Gets or sets the aspect ratio.</summary>
    [JsonPropertyName("aspect_ratio")]
    public string? AspectRatio { get; set; }

    /// <summary>Gets or sets the API URL.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the last released event date.</summary>
    [JsonPropertyName("event_last_released_at")]
    public DateTimeOffset? EventLastReleasedAt { get; set; }

    /// <summary>Gets or sets the events (only present in detail response).</summary>
    [JsonPropertyName("events")]
    public List<CccEvent>? Events { get; set; }
}

/// <summary>
/// Wrapper for conference list response.
/// </summary>
public class CccConferencesResponse
{
    /// <summary>Gets or sets the conferences.</summary>
    [JsonPropertyName("conferences")]
    public List<CccConference> Conferences { get; set; } = new();
}
