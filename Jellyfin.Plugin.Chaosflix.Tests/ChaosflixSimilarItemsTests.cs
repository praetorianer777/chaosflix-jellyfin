using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Chaosflix.Api.Models;
using Jellyfin.Plugin.Chaosflix.Channel;
using Jellyfin.Plugin.Chaosflix.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static Jellyfin.Plugin.Chaosflix.Tests.Fakes.TestData;

namespace Jellyfin.Plugin.Chaosflix.Tests;

/// <summary>
/// The "More like this" row (#72). A provider can only answer with library items, so
/// every case here is about which stored copy of a talk a recommendation resolves to.
/// </summary>
public class ChaosflixSimilarItemsTests
{
    private static readonly Guid ChannelId = Guid.NewGuid();

    private readonly FakeCccApi _api = new();
    private readonly List<BaseItem> _library = new();
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();

    public ChaosflixSimilarItemsTests() =>
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(_ => _library);

    [Theory]
    [InlineData(typeof(Video), true)]
    [InlineData(typeof(Folder), false)]
    public void OnlyVideosHaveRelatedTalks(Type itemType, bool supported) =>
        Assert.Equal(supported, Provider().Supports(itemType));

    [Fact]
    public async Task ATalkThatIsNotOursHasNoRelatedTalks()
    {
        var stranger = Item("some-other-plugin:1");

        Assert.Empty(await Similar(stranger));
    }

    [Fact]
    public async Task RecommendationsComeBackHeaviestFirst()
    {
        Source("e1", ("light", 1), ("heavy", 9), ("middling", 5));
        var heavy = Stored("heavy");
        var middling = Stored("middling");
        var light = Stored("light");

        var similar = await Similar(Stored("e1"));

        Assert.Equal([heavy.Id, middling.Id, light.Id], similar.Select(i => i.Id));
    }

    [Fact]
    public async Task ATalkNobodyHasBrowsedToIsLeftOut()
    {
        Source("e1", ("stored", 9), ("never-browsed", 8));
        var stored = Stored("stored");

        var similar = await Similar(Stored("e1"));

        Assert.Equal([stored.Id], similar.Select(i => i.Id));
    }

    [Fact]
    public async Task TheConferenceCopyIsTheOneOffered()
    {
        Source("e1", ("elsewhere", 9));
        var popular = Item("event:popular:elsewhere");
        var conference = Item("event:conf-38c3:elsewhere");
        _library.Add(popular);
        _library.Add(conference);

        var similar = await Similar(Stored("e1"));

        Assert.Equal([conference.Id], similar.Select(i => i.Id));
    }

    [Fact]
    public async Task TheTalkItselfAndWhatTheCallerExcludesAreLeftOut()
    {
        Source("e1", ("e1", 9), ("excluded", 8), ("kept", 7));
        var source = Stored("e1");
        var excluded = Stored("excluded");
        var kept = Stored("kept");

        var similar = await Similar(source, new SimilarItemsQuery { ExcludeItemIds = [excluded.Id] });

        Assert.Equal([kept.Id], similar.Select(i => i.Id));
    }

    [Fact]
    public async Task TheCallersLimitIsHonoured()
    {
        Source("e1", ("a", 3), ("b", 2), ("c", 1));
        Stored("a");
        Stored("b");
        Stored("c");

        var similar = await Similar(Stored("e1"), new SimilarItemsQuery { Limit = 2 });

        Assert.Equal(2, similar.Count);
    }

    private void Source(string guid, params (string Guid, int Weight)[] related)
    {
        var ev = Event(guid);
        ev.Related = related.Select(r => new CccRelatedEvent { EventGuid = r.Guid, Weight = r.Weight }).ToList();
        _api.Json($"/public/events/{guid}", ev);
    }

    private BaseItem Stored(string guid)
    {
        var item = Item(ChaosflixChannel.ConferenceItemId(guid));
        _library.Add(item);
        return item;
    }

    private static BaseItem Item(string externalId) =>
        new Video { Id = Guid.NewGuid(), Name = externalId, ExternalId = externalId, ChannelId = ChannelId };

    private ChaosflixSimilarItems Provider() =>
        new(_api.CreateApiClient(), _libraryManager, NullLogger<ChaosflixSimilarItems>.Instance);

    private Task<IReadOnlyList<BaseItem>> Similar(BaseItem item, SimilarItemsQuery? query = null) =>
        Provider().GetSimilarItemsAsync(item, query ?? new SimilarItemsQuery(), CancellationToken.None);
}
