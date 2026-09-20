using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Chaosflix.Api.Models;
using Jellyfin.Plugin.Chaosflix.Channel;
using Xunit;
using static Jellyfin.Plugin.Chaosflix.Tests.Fakes.TestData;

namespace Jellyfin.Plugin.Chaosflix.Tests;

/// <summary>
/// The conference allowlist (#86). Slugs are taken verbatim from media.ccc.de, because
/// their shape is the whole reason the match is written the way it is.
/// </summary>
public class ConferenceFilterTests
{
    private static readonly DateTimeOffset Released = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly List<CccConference> All =
    [
        Conference("38c3", Released, "congress/2024"),
        Conference("39c3", Released, "congress/2025"),
        Conference("gpn22", Released, "conferences/gpn/gpn22"),
        Conference("fossgis2023", Released, "conferences/geo/fossgis2023"),
        Conference("oscal19", Released, "conferences/oscal19"),
    ];

    private static string[] Selected(string? filter) =>
        ChaosflixChannel.SelectConferences(All, filter).Select(c => c.Acronym).ToArray();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingConfiguredKeepsEveryConference(string? filter)
    {
        Assert.Equal(All.Count, Selected(filter).Length);
    }

    [Fact]
    public void AnAcronymKeepsExactlyThatConference()
    {
        Assert.Equal(["38c3"], Selected("38c3"));
    }

    [Fact]
    public void ASeriesKeepsEveryEditionOfIt()
    {
        Assert.Equal(["38c3", "39c3"], Selected("congress"));
    }

    [Fact]
    public void ASeriesIsFoundWhereverTheSlugPutsIt()
    {
        Assert.Equal(["gpn22"], Selected("gpn"));
        Assert.Equal(["fossgis2023"], Selected("geo"));
    }

    [Fact]
    public void TheSlugPrefixEveryConferenceSharesIsNotASeries()
    {
        Assert.Empty(Selected("conferences"));
    }

    [Fact]
    public void EntriesAreSeparatedAndTrimmedAndCaseDoesNotMatter()
    {
        Assert.Equal(["38c3", "39c3", "oscal19"], Selected("  CONGRESS ,\n OSCAL19 ;; "));
    }

    [Fact]
    public void AnEntryThatMatchesNothingKeepsNothing()
    {
        Assert.Empty(Selected("divoc"));
    }
}
