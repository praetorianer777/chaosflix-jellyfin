using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Chaosflix.Api.Models;

/// <summary>
/// A conference as streaming.media.ccc.de describes it while it is running. The shape
/// follows the generator at voc/streaming-website (view/streams-json-v2.php); outside a
/// congress the endpoint answers with an empty list.
/// </summary>
public class CccLiveConference
{
    /// <summary>Gets or sets the conference title.</summary>
    [JsonPropertyName("conference")]
    public string Conference { get; set; } = string.Empty;

    /// <summary>Gets or sets the acronym, e.g. "39c3".</summary>
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether anything is on air right now. The site sets
    /// it from the rooms' schedules, which is what decides whether the channel offers a
    /// live folder at all.
    /// </summary>
    [JsonPropertyName("isCurrentlyStreaming")]
    public bool IsCurrentlyStreaming { get; set; }

    /// <summary>Gets or sets the room groups.</summary>
    [JsonPropertyName("groups")]
    public List<CccLiveGroup> Groups { get; set; } = new();
}

/// <summary>A named set of rooms, e.g. "Live" or "Selfhosted".</summary>
public class CccLiveGroup
{
    /// <summary>Gets or sets the group name.</summary>
    [JsonPropertyName("group")]
    public string Group { get; set; } = string.Empty;

    /// <summary>Gets or sets the rooms.</summary>
    [JsonPropertyName("rooms")]
    public List<CccLiveRoom> Rooms { get; set; } = new();
}

/// <summary>One room with its stream variants and what is on in it.</summary>
public class CccLiveRoom
{
    /// <summary>Gets or sets the room slug, e.g. "halla".</summary>
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name, e.g. "Adams".</summary>
    [JsonPropertyName("display")]
    public string Display { get; set; } = string.Empty;

    /// <summary>Gets or sets the thumbnail url.</summary>
    [JsonPropertyName("thumb")]
    public string? Thumb { get; set; }

    /// <summary>Gets or sets the room's page on the streaming site.</summary>
    [JsonPropertyName("link")]
    public string? Link { get; set; }

    /// <summary>Gets or sets what is running now and next.</summary>
    [JsonPropertyName("talks")]
    public CccLiveTalks? Talks { get; set; }

    /// <summary>Gets or sets the stream variants.</summary>
    [JsonPropertyName("streams")]
    public List<CccLiveStreamVariant> Streams { get; set; } = new();
}

/// <summary>The talk on air and the one after it; both are null between talks.</summary>
public class CccLiveTalks
{
    /// <summary>Gets or sets the talk currently on air.</summary>
    [JsonPropertyName("current")]
    public CccLiveTalk? Current { get; set; }

    /// <summary>Gets or sets the next talk.</summary>
    [JsonPropertyName("next")]
    public CccLiveTalk? Next { get; set; }
}

/// <summary>A scheduled talk, as the streaming site reports it.</summary>
public class CccLiveTalk
{
    /// <summary>Gets or sets the title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the speakers, already joined by the site.</summary>
    [JsonPropertyName("speaker")]
    public string? Speaker { get; set; }
}

/// <summary>
/// One way to watch a room: a resolution, a language and a player type. "native" is the
/// floor language, "translated" an interpreter's channel.
/// </summary>
public class CccLiveStreamVariant
{
    /// <summary>Gets or sets the variant slug, e.g. "hd-native".</summary>
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    /// <summary>Gets or sets the human readable description.</summary>
    [JsonPropertyName("display")]
    public string Display { get; set; } = string.Empty;

    /// <summary>Gets or sets the player type: video, slides, audio, music or dash.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this is an interpreter's channel.</summary>
    [JsonPropertyName("isTranslated")]
    public bool IsTranslated { get; set; }

    /// <summary>Gets or sets width and height, absent for audio.</summary>
    [JsonPropertyName("videoSize")]
    public List<int>? VideoSize { get; set; }

    /// <summary>Gets or sets the urls by protocol ("hls", "webm", "dash", "mp3", "opus").</summary>
    [JsonPropertyName("urls")]
    public Dictionary<string, CccLiveStreamUrl> Urls { get; set; } = new();
}

/// <summary>One protocol's url for a stream variant.</summary>
public class CccLiveStreamUrl
{
    /// <summary>Gets or sets the description the site shows for it.</summary>
    [JsonPropertyName("display")]
    public string? Display { get; set; }

    /// <summary>Gets or sets the technical description, e.g. "hls".</summary>
    [JsonPropertyName("tech")]
    public string? Tech { get; set; }

    /// <summary>Gets or sets the stream url.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}
