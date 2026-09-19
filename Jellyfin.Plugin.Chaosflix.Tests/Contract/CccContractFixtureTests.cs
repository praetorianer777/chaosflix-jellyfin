using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Chaosflix.Api.Models;
using Xunit;

namespace Jellyfin.Plugin.Chaosflix.Tests.Contract;

/// <summary>
/// Runs the contract assertions against responses recorded from the real API (see
/// <c>Contract/Fixtures/README.md</c>). This is hermetic and part of every push: it cannot tell
/// whether media.ccc.de still agrees — only <see cref="CccApiContractTests"/> can — but it keeps
/// the assertions themselves honest, and it pins the awkward shapes the real API delivers.
/// </summary>
public sealed class CccContractFixtureTests
{
    /// <summary>Matches the options <c>HttpClient.GetFromJsonAsync</c> uses inside the plugin.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "Contract", "Fixtures");

    [Fact]
    public void RecordedConferenceList_SatisfiesTheContract()
    {
        var response = Load<CccConferencesResponse>("conferences.json");
        CccContract.AssertConferenceList(response.Conferences);
    }

    [Fact]
    public void RecordedConferenceDetail_SatisfiesTheContract()
    {
        var conference = Load<CccConference>("conference-38c3.json");
        CccContract.AssertConferenceDetail(conference, "38c3");
    }

    [Fact]
    public void RecordedEventDetail_SatisfiesTheContract()
    {
        var cccEvent = Load<CccEvent>("event-38c3-rekordbox.json");
        CccContract.AssertEventDetail(cccEvent);
    }

    [Fact]
    public void RecordedSearchResults_SatisfyTheContract()
    {
        var response = Load<CccEventsResponse>("search-chaos.json");
        CccContract.AssertSearchResults(response.Events, "chaos");
    }

    /// <summary>
    /// media.ccc.de sends <c>"length": null</c>, <c>"size": null</c>, <c>"width": null</c> and
    /// <c>"height": null</c> for recordings that are not media — an unfinished subtitle track, for
    /// instance. System.Text.Json refuses null for a non-nullable int, so without the converter on
    /// <see cref="CccRecording"/> the whole event fails to deserialise and the talk disappears from
    /// the channel. Recorded from /public/events/search?q=chaos.
    /// </summary>
    [Fact]
    public void RecordedSubtitleRecording_DeserialisesDespiteNullNumbers()
    {
        var response = Load<CccEventsResponse>("search-chaos.json");
        var subtitle = response.Events[0].Recordings!
            .Single(r => r.MimeType == "application/x-subrip");

        Assert.Equal(0, subtitle.Length);
        Assert.Equal(0, subtitle.Size);
        Assert.Equal(0, subtitle.Width);
        Assert.Equal(0, subtitle.Height);
    }

    private static T Load<T>(string fileName)
    {
        var path = Path.Combine(FixtureDir, fileName);
        Assert.True(File.Exists(path), $"Recorded fixture '{fileName}' is missing from {FixtureDir}.");

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)!;
    }
}
