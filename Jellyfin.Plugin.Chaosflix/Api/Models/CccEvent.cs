using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Chaosflix.Api.Models;

/// <summary>
/// CCC API event (a single talk/recording).
/// </summary>
public class CccEvent
{
    /// <summary>Gets or sets the GUID.</summary>
    [JsonPropertyName("guid")]
    public string Guid { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the subtitle.</summary>
    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    /// <summary>Gets or sets the slug.</summary>
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Gets or sets the original language.</summary>
    [JsonPropertyName("original_language")]
    public string? OriginalLanguage { get; set; }

    /// <summary>Gets or sets the speakers.</summary>
    [JsonPropertyName("persons")]
    public List<string> Persons { get; set; } = new();

    /// <summary>Gets or sets the tags.</summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    /// <summary>Gets or sets the view count.</summary>
    [JsonPropertyName("view_count")]
    public int ViewCount { get; set; }

    /// <summary>Gets or sets the event date.</summary>
    [JsonPropertyName("date")]
    public DateTimeOffset? Date { get; set; }

    /// <summary>Gets or sets the release date.</summary>
    [JsonPropertyName("release_date")]
    public DateTimeOffset? ReleaseDate { get; set; }

    /// <summary>Gets or sets the duration in seconds.</summary>
    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    /// <summary>Gets or sets the thumbnail URL.</summary>
    [JsonPropertyName("thumb_url")]
    public string? ThumbUrl { get; set; }

    /// <summary>Gets or sets the poster URL.</summary>
    [JsonPropertyName("poster_url")]
    public string? PosterUrl { get; set; }

    /// <summary>Gets or sets the frontend link.</summary>
    [JsonPropertyName("frontend_link")]
    public string? FrontendLink { get; set; }

    /// <summary>Gets or sets the API URL.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the conference title.</summary>
    [JsonPropertyName("conference_title")]
    public string? ConferenceTitle { get; set; }

    /// <summary>Gets or sets the conference API URL.</summary>
    [JsonPropertyName("conference_url")]
    public string? ConferenceUrl { get; set; }

    /// <summary>Gets or sets the recordings (only in detail response).</summary>
    [JsonPropertyName("recordings")]
    public List<CccRecording>? Recordings { get; set; }

    /// <summary>Gets or sets the related events.</summary>
    [JsonPropertyName("related")]
    public List<CccRelatedEvent>? Related { get; set; }
}

/// <summary>
/// Wrapper for event search response.
/// </summary>
public class CccEventsResponse
{
    /// <summary>Gets or sets the events.</summary>
    [JsonPropertyName("events")]
    public List<CccEvent> Events { get; set; } = new();
}
