using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Chaosflix.Api.Models;

namespace Jellyfin.Plugin.Chaosflix.Tests.Contract;

/// <summary>
/// The shape of the media.ccc.de responses the plugin depends on, expressed as assertions over
/// the plugin's own models. Values change with every congress, so nothing here asserts a value —
/// only that the fields the plugin reads survived deserialisation with a usable value.
///
/// Kept separate from the tests that fetch so the very same assertions can run offline against the
/// recorded fixtures in <c>Contract/Fixtures</c>, which is what proves the assertions themselves work.
/// </summary>
public static class CccContract
{
    /// <summary>
    /// Asserts the contract of <c>GET /public/conferences</c>.
    /// </summary>
    public static void AssertConferenceList(IReadOnlyList<CccConference> conferences)
    {
        Assert.True(conferences.Count > 0, "GET /public/conferences returned no conferences at all.");

        foreach (var conference in conferences)
        {
            // Every browsing path builds /conferences/{acronym} from this, so a blank or
            // path-unsafe acronym makes the conference unreachable rather than merely ugly.
            Assert.True(
                !string.IsNullOrWhiteSpace(conference.Acronym),
                Missing("acronym", Describe(conference)));
            Assert.True(
                conference.Acronym.IndexOfAny(new[] { '/', '?', '#', ' ' }) < 0,
                $"Conference acronym '{conference.Acronym}' is not usable in a URL path any more.");
            Assert.True(
                !string.IsNullOrWhiteSpace(conference.Title),
                Missing("title", Describe(conference)));
            Assert.True(
                IsAbsoluteHttpUrl(conference.Url),
                $"Conference '{conference.Acronym}' has a non-absolute url: '{conference.Url}'.");
        }

        // ChaosflixChannel drops conferences without event_last_released_at and orders every
        // other view by it, so the whole channel empties out if the field disappears. A handful
        // of conferences legitimately lack it; a majority lacking it is a contract break.
        var dated = conferences.Count(c => c.EventLastReleasedAt != null);
        Assert.True(
            dated * 2 > conferences.Count,
            $"Only {dated} of {conferences.Count} conferences carry event_last_released_at; "
            + "the channel filters and sorts on it.");
    }

    /// <summary>
    /// Asserts the contract of <c>GET /public/conferences/{acronym}</c>.
    /// </summary>
    public static void AssertConferenceDetail(CccConference conference, string requestedAcronym)
    {
        Assert.True(
            string.Equals(conference.Acronym, requestedAcronym, StringComparison.OrdinalIgnoreCase),
            $"GET /public/conferences/{requestedAcronym} answered with acronym '{conference.Acronym}'.");
        Assert.True(
            conference.Events != null,
            $"Conference '{requestedAcronym}' has no 'events' property in the detail response.");
        Assert.True(
            conference.Events!.Count > 0,
            $"Conference '{requestedAcronym}' lists no events.");

        foreach (var cccEvent in conference.Events)
        {
            AssertEventIdentity(cccEvent);
        }

        // These are per-event optional but must exist somewhere, or the field was renamed:
        // the channel needs date (year folders, trending age), persons (cast), tags (genres)
        // and view_count (rating and the trending list).
        AssertAtLeastOne(conference.Events, e => e.Date != null, "date", requestedAcronym);
        AssertAtLeastOne(conference.Events, e => e.Persons.Count > 0, "persons", requestedAcronym);
        AssertAtLeastOne(conference.Events, e => e.Tags.Count > 0, "tags", requestedAcronym);
        AssertAtLeastOne(conference.Events, e => e.ViewCount > 0, "view_count", requestedAcronym);
    }

    /// <summary>
    /// Asserts the contract of <c>GET /public/events/{guid}</c>.
    /// </summary>
    public static void AssertEventDetail(CccEvent cccEvent)
    {
        AssertEventIdentity(cccEvent);

        Assert.True(
            cccEvent.Recordings != null,
            $"Event {cccEvent.Guid} has no 'recordings' property; nothing would be playable.");
        Assert.True(
            cccEvent.Recordings!.Count > 0,
            $"Event {cccEvent.Guid} lists no recordings.");

        foreach (var recording in cccEvent.Recordings)
        {
            AssertRecording(recording, cccEvent.Guid);
        }

        var videos = cccEvent.Recordings
            .Where(r => r.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var video in videos)
        {
            AssertPlayableRecording(video, cccEvent.Guid);
        }

        Assert.True(
            videos.Count > 0,
            $"Event {cccEvent.Guid} has no recording with a video/* mime_type; "
            + "the channel and the stream proxy both filter on that prefix.");
        Assert.True(
            videos.Any(r => r.MimeType.Contains("mp4", StringComparison.OrdinalIgnoreCase)),
            $"Event {cccEvent.Guid} offers no MP4; the stream proxy prefers mp4 over webm.");
        Assert.True(
            videos.Any(r => r.Width > 0 && r.Height > 0),
            $"Event {cccEvent.Guid} has no video recording with width/height; "
            + "the channel sorts candidates by width.");
        Assert.True(
            videos.Any(r => r.HighQuality),
            $"Event {cccEvent.Guid} has no high_quality video recording.");
        Assert.True(
            videos.Any(r => !r.HighQuality),
            $"Event {cccEvent.Guid} has no standard-quality video recording; "
            + "PreferredQuality=Standard would have nothing to pick.");

        AssertRelated(cccEvent);
    }

    /// <summary>
    /// Asserts the contract of <c>GET /public/events/search?q=</c>.
    /// </summary>
    public static void AssertSearchResults(IReadOnlyList<CccEvent> events, string query)
    {
        Assert.True(events.Count > 0, $"Searching for '{query}' returned no events.");

        foreach (var cccEvent in events)
        {
            AssertEventIdentity(cccEvent);
        }
    }

    /// <summary>
    /// Asserts the contract of the <c>related</c> list, which backs the "related talks" folder.
    /// The list is legitimately empty for many talks, so only its presence and the shape of the
    /// entries it does carry are contractual.
    /// </summary>
    public static void AssertRelated(CccEvent cccEvent)
    {
        Assert.True(
            cccEvent.Related != null,
            $"Event {cccEvent.Guid} has no 'related' property; the related-talks folder reads it.");

        foreach (var related in cccEvent.Related!)
        {
            Assert.True(
                !string.IsNullOrWhiteSpace(related.EventGuid),
                $"A related entry of event {cccEvent.Guid} has no event_guid.");
            Assert.True(
                Guid.TryParse(related.EventGuid, out _),
                $"Related event_guid '{related.EventGuid}' of event {cccEvent.Guid} is not a GUID.");
            Assert.True(
                related.Weight > 0,
                $"Related entry {related.EventGuid} of event {cccEvent.Guid} has weight {related.Weight}; "
                + "the channel orders the folder by it.");
        }
    }

    private static void AssertEventIdentity(CccEvent cccEvent)
    {
        Assert.True(
            !string.IsNullOrWhiteSpace(cccEvent.Guid),
            Missing("guid", $"event '{cccEvent.Title}'"));
        Assert.True(
            Guid.TryParse(cccEvent.Guid, out _),
            $"Event guid '{cccEvent.Guid}' is not a GUID; the channel builds item ids from it.");
        Assert.True(
            !string.IsNullOrWhiteSpace(cccEvent.Title),
            Missing("title", $"event {cccEvent.Guid}"));
        Assert.True(
            IsAbsoluteHttpUrl(cccEvent.Url),
            $"Event {cccEvent.Guid} has a non-absolute url: '{cccEvent.Url}'.");
    }

    /// <summary>
    /// Holds for every recording, playable or not — an event carries subtitle and chapter
    /// recordings too, and those only have to deserialise and identify themselves.
    /// </summary>
    private static void AssertRecording(CccRecording recording, string eventGuid)
    {
        var where = $"recording '{recording.Filename}' of event {eventGuid}";

        Assert.True(!string.IsNullOrWhiteSpace(recording.MimeType), Missing("mime_type", where));
        Assert.True(
            recording.MimeType.Contains('/', StringComparison.Ordinal),
            $"mime_type '{recording.MimeType}' of {where} is not a media type.");
        Assert.True(
            IsAbsoluteHttpUrl(recording.RecordingUrl),
            $"recording_url of {where} is not an absolute http(s) URL: '{recording.RecordingUrl}'.");
    }

    /// <summary>
    /// Holds for the video recordings, the only ones the channel and the stream proxy offer.
    /// </summary>
    private static void AssertPlayableRecording(CccRecording recording, string eventGuid)
    {
        var where = $"video recording '{recording.Filename}' of event {eventGuid}";

        Assert.True(!string.IsNullOrWhiteSpace(recording.Language), Missing("language", where));

        // The stream proxy re-finds a recording by folder+language, so a blank folder makes the
        // signed /Chaosflix/Stream URL unresolvable even though the item still appears.
        Assert.True(!string.IsNullOrWhiteSpace(recording.Folder), Missing("folder", where));

        // RunTimeTicks comes from length, and the bitrate Jellyfin uses to pick a stream is
        // size/length — a zero in either turns into a zero runtime or a missing bitrate, and a
        // null (which the API sends for recordings that are not media) into neither. Both are
        // false here, because a lifted comparison against null is false.
        Assert.True(recording.Length > 0, $"length of {where} is {Number(recording.Length)}.");
        Assert.True(recording.Size > 0, $"size of {where} is {Number(recording.Size)}.");
    }

    private static void AssertAtLeastOne(
        IReadOnlyList<CccEvent> events,
        Func<CccEvent, bool> predicate,
        string field,
        string acronym)
    {
        Assert.True(
            events.Any(predicate),
            $"Not one of the {events.Count} events of conference '{acronym}' has a usable '{field}'.");
    }

    private static bool IsAbsoluteHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    private static string Describe(CccConference conference) =>
        string.IsNullOrWhiteSpace(conference.Acronym)
            ? $"conference '{conference.Title}'"
            : $"conference '{conference.Acronym}'";

    private static string Missing(string field, string where) =>
        $"The CCC API no longer delivers a usable '{field}' for {where}.";

    private static string Number(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "null";
}
