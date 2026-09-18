using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Chaosflix.Channel;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.Chaosflix.Tests;

public class ChaosflixUserDataMirrorTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid ChannelId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly IUserDataManager _userData = Substitute.For<IUserDataManager>();
    private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
    private readonly IUserManager _users = Substitute.For<IUserManager>();
    private readonly User _user = new("chaosflix", "provider", "provider");
    private readonly List<BaseItem> _items = new();

    public ChaosflixUserDataMirrorTests()
    {
        _users.GetUserById(UserId).Returns(_user);
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(callInfo =>
        {
            var query = callInfo.Arg<InternalItemsQuery>();
            return _items.Where(i => i.Name == query.Name).ToList();
        });
        _library.GetItemById(Arg.Any<Guid>()).Returns(callInfo =>
            _items.FirstOrDefault(i => i.Id == callInfo.Arg<Guid>()));
    }

    [Fact]
    public void MirrorsPlayedStateToTheOtherCopiesOfTheSameTalk()
    {
        var source = Item("event:conf-e2e:guid-1");
        var sibling = Item("event:popular:guid-1");
        var sibi = UserData(sibling);

        Mirror().Mirror(Saved(source, played: true, positionTicks: 900));

        Assert.True(sibi.Played);
        Assert.Equal(900, sibi.PlaybackPositionTicks);
        _userData.Received(1).SaveUserData(_user, sibling, sibi, UserDataSaveReason.UpdateUserData, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void LeavesADifferentTalkOfTheSameNameAlone()
    {
        var source = Item("event:conf-e2e:guid-1");
        var other = Item("event:conf-other:guid-2");
        UserData(other);

        Mirror().Mirror(Saved(source, played: true, positionTicks: 900));

        _userData.DidNotReceive().SaveUserData(
            Arg.Any<User>(), other, Arg.Any<UserItemData>(), Arg.Any<UserDataSaveReason>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void IgnoresItemsThatAreNotChaosflixTalks()
    {
        var source = Item("some-other-channel-item");
        Item("event:popular:guid-1");

        Mirror().Mirror(Saved(source, played: true, positionTicks: 900));

        _library.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());
    }

    [Fact]
    public void IgnoresABulkImport()
    {
        var source = Item("event:conf-e2e:guid-1");
        Item("event:popular:guid-1");

        Mirror().Mirror(Saved(source, played: true, positionTicks: 900, reason: UserDataSaveReason.Import));

        _library.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());
    }

    // A mirrored save raises UserDataSaved again; without this the copies would
    // keep saving each other.
    [Fact]
    public void DoesNotSaveASiblingThatAlreadyMatches()
    {
        var source = Item("event:conf-e2e:guid-1");
        var sibling = Item("event:popular:guid-1");
        UserData(sibling, played: true, positionTicks: 900);

        Mirror().Mirror(Saved(source, played: true, positionTicks: 900));

        _userData.DidNotReceive().SaveUserData(
            Arg.Any<User>(), Arg.Any<BaseItem>(), Arg.Any<UserItemData>(), Arg.Any<UserDataSaveReason>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void AMirroredSaveDoesNotTravelBack()
    {
        var source = Item("event:conf-e2e:guid-1");
        var sibling = Item("event:popular:guid-1");
        var sourceData = UserData(source, played: true, positionTicks: 900);
        var siblingData = UserData(sibling);
        var mirror = Mirror();

        // What the server does: the save the mirror performs comes back as an
        // event of its own, from inside SaveUserData.
        _userData
            .When(m => m.SaveUserData(_user, sibling, Arg.Any<UserItemData>(), Arg.Any<UserDataSaveReason>(), Arg.Any<CancellationToken>()))
            .Do(_ => mirror.Mirror(Saved(sibling, siblingData, UserDataSaveReason.UpdateUserData)));

        mirror.Mirror(Saved(source, sourceData, UserDataSaveReason.PlaybackProgress));

        _userData.Received(1).SaveUserData(
            Arg.Any<User>(), Arg.Any<BaseItem>(), Arg.Any<UserItemData>(), Arg.Any<UserDataSaveReason>(), Arg.Any<CancellationToken>());
    }

    private ChaosflixUserDataMirror Mirror() =>
        new(_userData, _library, _users, NullLogger<ChaosflixUserDataMirror>.Instance);

    private BaseItem Item(string externalId)
    {
        var item = new Video
        {
            Id = Guid.NewGuid(),
            Name = "A talk",
            ExternalId = externalId,
            ChannelId = ChannelId
        };
        _items.Add(item);
        return item;
    }

    private UserItemData UserData(BaseItem item, bool played = false, long positionTicks = 0)
    {
        var data = new UserItemData { Key = item.Id.ToString("N"), Played = played, PlaybackPositionTicks = positionTicks };
        _userData.GetUserData(_user, item).Returns(data);
        return data;
    }

    private static UserDataSaveEventArgs Saved(
        BaseItem item,
        bool played,
        long positionTicks,
        UserDataSaveReason reason = UserDataSaveReason.PlaybackProgress) =>
        Saved(item, new UserItemData { Key = item.Id.ToString("N"), Played = played, PlaybackPositionTicks = positionTicks }, reason);

    private static UserDataSaveEventArgs Saved(BaseItem item, UserItemData data, UserDataSaveReason reason) =>
        new() { Item = item, UserData = data, UserId = UserId, SaveReason = reason };
}
