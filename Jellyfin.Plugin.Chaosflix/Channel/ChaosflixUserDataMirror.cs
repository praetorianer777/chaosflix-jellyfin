using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaosflix.Channel;

/// <summary>
/// Keeps the watch state of one talk in sync across the copies the channel
/// hands out.
/// </summary>
/// <remarks>
/// Jellyfin stores a channel item as one library item with exactly one parent,
/// so a talk listed in several folders needs one id per folder (#15) and ends up
/// as several library items — each with its own user data. Marking a talk
/// unwatched or clearing its position then only affects the copy in front of the
/// user (#53). This mirrors played state and resume position from the copy that
/// was saved to its siblings.
/// </remarks>
public sealed class ChaosflixUserDataMirror : IHostedService
{
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<ChaosflixUserDataMirror> _logger;

    // Jellyfin raises UserDataSaved from inside SaveUserData, so a mirrored save
    // comes back as another event; the copies it is about are in here while it runs.
    private readonly ConcurrentDictionary<(Guid User, Guid Item), byte> _mirroring = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ChaosflixUserDataMirror"/> class.
    /// </summary>
    /// <param name="userDataManager">Source of the save events and sink for the mirrored state.</param>
    /// <param name="libraryManager">Used to find the other copies of a talk.</param>
    /// <param name="userManager">Resolves the user a save belongs to.</param>
    /// <param name="logger">Logger.</param>
    public ChaosflixUserDataMirror(
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<ChaosflixUserDataMirror> logger)
    {
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    internal void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            Mirror(e);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not mirror the watch state of {Item}", e.Item?.Name);
        }
    }

    internal void Mirror(UserDataSaveEventArgs e)
    {
        var item = e.Item;
        var source = e.UserData;
        if (item is null || source is null || !IsWatchState(e.SaveReason))
        {
            return;
        }

        var eventGuid = ChaosflixChannel.EventGuidOf(item.ExternalId);
        if (eventGuid is null || _mirroring.ContainsKey((e.UserId, item.Id)))
        {
            return;
        }

        var user = _userManager.GetUserById(e.UserId);
        if (user is null)
        {
            return;
        }

        foreach (var sibling in Siblings(item, eventGuid))
        {
            var target = _userDataManager.GetUserData(user, sibling);
            if (target is null || !Differs(source, target))
            {
                continue;
            }

            target.Played = source.Played;
            target.PlaybackPositionTicks = source.PlaybackPositionTicks;
            target.PlayCount = source.PlayCount;
            target.LastPlayedDate = source.LastPlayedDate;

            if (!_mirroring.TryAdd((e.UserId, sibling.Id), 0))
            {
                continue;
            }

            try
            {
                _userDataManager.SaveUserData(user, sibling, target, UserDataSaveReason.UpdateUserData, CancellationToken.None);
            }
            finally
            {
                _mirroring.TryRemove((e.UserId, sibling.Id), out _);
            }
        }
    }

    // Import is a bulk reason and carries no user action; the rest is what a
    // client does when it plays a talk or toggles its watched state.
    private static bool IsWatchState(UserDataSaveReason reason) => reason is
        UserDataSaveReason.PlaybackStart or
        UserDataSaveReason.PlaybackProgress or
        UserDataSaveReason.PlaybackFinished or
        UserDataSaveReason.TogglePlayed or
        UserDataSaveReason.UpdateUserData;

    // A save whose state already matches produces no save of its own, which is
    // what stops a mirrored save from travelling back and forth.
    private static bool Differs(UserItemData source, UserItemData target) =>
        source.Played != target.Played ||
        source.PlaybackPositionTicks != target.PlaybackPositionTicks ||
        source.PlayCount != target.PlayCount ||
        source.LastPlayedDate != target.LastPlayedDate;

    private IEnumerable<BaseItem> Siblings(BaseItem item, string eventGuid)
    {
        // Looked up by name within the channel rather than by scanning every
        // channel item: the scopes a talk can appear under are open-ended
        // (related-<guid> exists per talk), so its other ids cannot be enumerated.
        var query = new InternalItemsQuery
        {
            Name = item.Name,
            ChannelIds = item.ChannelId == Guid.Empty ? Array.Empty<Guid>() : new[] { item.ChannelId }
        };

        IReadOnlyList<BaseItem> candidates;
        try
        {
            candidates = _libraryManager.GetItemList(query);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not look up the other copies of {Item}", item.Name);
            return Array.Empty<BaseItem>();
        }

        return candidates
            .Where(candidate =>
                !candidate.Id.Equals(item.Id) &&
                string.Equals(ChaosflixChannel.EventGuidOf(candidate.ExternalId), eventGuid, StringComparison.Ordinal))
            // Through the library cache, not as the query materialised them: user
            // data is read off the BaseItem instance the cache holds, so saving
            // against a fresh instance reaches the database but leaves what the
            // server serves stale until a restart.
            .Select(candidate => _libraryManager.GetItemById(candidate.Id) ?? candidate);
    }
}
