using System;
using Jellyfin.Plugin.Chaosflix.Api;
using Xunit;

namespace Jellyfin.Plugin.Chaosflix.Tests.Contract;

/// <summary>
/// Checks that the real media.ccc.de API still delivers what the plugin reads out of it.
///
/// Every other test in this repo runs against fixtures we wrote ourselves, so a renamed or dropped
/// field upstream stays green everywhere and only breaks for users. These tests are the one place
/// that would notice. They assert shape, never values: talks, conferences and view counts change.
///
/// Opt-in via <c>CCC_CONTRACT=1</c>; see <see cref="ContractFactAttribute"/>.
/// </summary>
[Trait("Category", "Contract")]
public sealed class CccApiContractTests : IClassFixture<CccApiLiveFixture>
{
    private readonly CccApiLiveFixture _api;

    /// <summary>
    /// Initializes a new instance of the <see cref="CccApiContractTests"/> class.
    /// </summary>
    public CccApiContractTests(CccApiLiveFixture api) => _api = api;

    [ContractFact]
    public void ConferenceList_MatchesContract()
    {
        _api.EnsureFetched();
        CccContract.AssertConferenceList(_api.Conferences);
    }

    [ContractFact]
    public void ConferenceDetail_MatchesContract()
    {
        _api.EnsureFetched();
        Assert.True(
            _api.ConferenceDetail != null,
            $"GET /public/conferences/{_api.ConferenceAcronym} returned nothing.");
        CccContract.AssertConferenceDetail(_api.ConferenceDetail!, _api.ConferenceAcronym);
    }

    [ContractFact]
    public void EventDetail_MatchesContract()
    {
        _api.EnsureFetched();
        Assert.True(
            _api.EventDetail != null,
            "No event detail could be sampled from the newest conference.");
        CccContract.AssertEventDetail(_api.EventDetail!);
    }

    [ContractFact]
    public void Search_MatchesContract()
    {
        _api.EnsureFetched();
        CccContract.AssertSearchResults(_api.SearchResults, CccApiLiveFixture.SearchQuery);
    }

    /// <summary>
    /// The plugin hands players a signed proxy URL and resolves the CDN redirect itself
    /// (<see cref="CccApiClient.ResolveRedirectAsync"/>) because some clients choke on a
    /// cross-domain redirect. That only helps as long as the CDN keeps redirecting.
    /// </summary>
    [ContractFact]
    public void Cdn_StillRedirectsToAMirror()
    {
        _api.EnsureFetched();
        Assert.True(_api.Cdn != null, "No cdn.media.ccc.de MP4 was available to probe.");

        var cdn = _api.Cdn!;
        Assert.True(
            cdn.HeadStatus is >= 301 and <= 308,
            $"HEAD {cdn.RecordingUrl} answered {cdn.HeadStatus} instead of a redirect.");
        Assert.True(
            cdn.Redirected,
            $"HEAD {cdn.RecordingUrl} answered {cdn.HeadStatus} without a Location header.");
        Assert.True(
            Uri.TryCreate(cdn.MirrorUrl, UriKind.Absolute, out _),
            $"The CDN redirected {cdn.RecordingUrl} to a non-absolute location: '{cdn.MirrorUrl}'.");
    }

    /// <summary>
    /// Jellyfin seeks by asking for byte ranges. A mirror that ignores Range would play from the
    /// start on every seek, so range support is part of the contract, not a nicety.
    ///
    /// What is asserted is the answer to a ranged GET, not an <c>Accept-Ranges</c> advertisement:
    /// <see cref="ChaosflixStreamController"/> forwards the Range header to whatever mirror the CDN
    /// named and never reads Accept-Ranges, and it sets <c>Accept-Ranges: bytes</c> on its own
    /// response itself. nginx (which every ccc mirror runs) omits the header from a 206 because it
    /// is redundant there, so requiring it made the suite fail on mirrors that seek perfectly well.
    /// </summary>
    [ContractFact]
    public void Mirror_SupportsRangeRequests()
    {
        _api.EnsureFetched();
        Assert.True(_api.Cdn != null, "No cdn.media.ccc.de MP4 was available to probe.");

        var cdn = _api.Cdn!;
        Assert.True(
            cdn.RangeStatus == 206,
            $"A ranged GET on {cdn.MirrorUrl} answered {cdn.RangeStatus} instead of 206; "
            + "seeking would restart the stream.");
        Assert.True(
            cdn.ContentRange != null,
            $"{cdn.MirrorUrl} answered 206 without a Content-Range header.");
        Assert.True(
            cdn.BytesReturned == 1024,
            $"A request for the first 1024 bytes of {cdn.MirrorUrl} returned {cdn.BytesReturned}.");
    }
}
