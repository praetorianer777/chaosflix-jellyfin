using Jellyfin.Plugin.Chaosflix.Api.Models;

namespace Jellyfin.Plugin.Chaosflix.Tests.Fakes;

public static class TestData
{
    public static CccConference Conference(string acronym, DateTimeOffset? lastReleased, params CccEvent[] events) => new()
    {
        Acronym = acronym,
        Title = acronym.ToUpperInvariant(),
        Slug = acronym,
        Description = $"{acronym} description",
        LogoUrl = $"https://static.media.ccc.de/{acronym}.png",
        EventLastReleasedAt = lastReleased,
        Events = events.Length > 0 ? events.ToList() : null
    };

    public static CccEvent Event(
        string guid,
        int views = 0,
        DateTimeOffset? date = null,
        DateTimeOffset? releaseDate = null,
        List<CccRecording>? recordings = null,
        List<CccRelatedEvent>? related = null) => new()
    {
        Guid = guid,
        Title = $"Talk {guid}",
        Slug = guid,
        ViewCount = views,
        Date = date,
        ReleaseDate = releaseDate,
        Duration = 3600,
        Recordings = recordings,
        Related = related
    };

    public static CccRecording Recording(
        string folder,
        string mimeType = "video/mp4",
        bool highQuality = true,
        string language = "eng",
        int width = 1920,
        string? url = null) => new()
    {
        Folder = folder,
        MimeType = mimeType,
        HighQuality = highQuality,
        Language = language,
        Width = width,
        Height = width * 9 / 16,
        Size = 100,
        Length = 60,
        RecordingUrl = url ?? $"https://cdn.media.ccc.de/{folder}/{language}.bin"
    };

    /// <summary>
    /// A subtitle recording the way the API publishes one: no size, length or
    /// dimensions, an empty folder and a language of its own.
    /// </summary>
    public static CccRecording Subtitle(
        string filename,
        string mimeType = "application/x-subrip",
        string language = "eng",
        string state = "complete",
        string? url = null) => new()
    {
        Folder = string.Empty,
        MimeType = mimeType,
        HighQuality = true,
        Language = language,
        State = state,
        Filename = filename,
        RecordingUrl = url ?? $"https://cdn.media.ccc.de/{filename}"
    };

    public static DateTimeOffset Day(int year, int month = 1, int day = 1) => new(year, month, day, 12, 0, 0, TimeSpan.Zero);
}
