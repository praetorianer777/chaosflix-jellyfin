using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Chaosflix.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaosflix.Channel;

/// <summary>
/// Turns the weighted recommendations media.ccc.de publishes for a talk into the
/// "More like this" row every Jellyfin client already shows.
///
/// The plugin has always fetched them and could already list them behind a folder id
/// of <c>related-&lt;guid&gt;</c>, but nothing ever handed out such a folder, so the
/// feature was unreachable (#72). Answering here instead of emitting folders is what
/// keeps it from making the duplicate problem worse: a channel item is stored under
/// exactly one parent, so every folder of related talks would add another library
/// item for a talk that already exists (#54, #15). This adds none.
/// </summary>
public class ChaosflixSimilarItems : ILocalSimilarItemsProvider
{
    /// <summary>How many related talks are offered when the caller names no limit.</summary>
    internal const int DefaultLimit = 12;

    private readonly CccApiClient _apiClient;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ChaosflixSimilarItems> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChaosflixSimilarItems"/> class.
    /// </summary>
    public ChaosflixSimilarItems(
        CccApiClient apiClient, ILibraryManager libraryManager, ILogger<ChaosflixSimilarItems> logger)
    {
        _apiClient = apiClient;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ChaosflixChannel.ChannelName;

    /// <inheritdoc />
    public MetadataPluginType Type => MetadataPluginType.LocalSimilarityProvider;

    /// <summary>
    /// Gets how long Jellyfin may reuse an answer. The recommendations change when
    /// media.ccc.de recomputes them, which is far slower than people browse.
    /// </summary>
    public TimeSpan? CacheDuration => TimeSpan.FromHours(6);

    /// <inheritdoc />
    public bool Supports(Type itemType) => typeof(Video).IsAssignableFrom(itemType);

    /// <inheritdoc />
    public async Task<IReadOnlyList<BaseItem>> GetSimilarItemsAsync(
        BaseItem item, SimilarItemsQuery query, CancellationToken cancellationToken)
    {
        var eventGuid = ChaosflixChannel.EventGuidOf(item.ExternalId);
        if (eventGuid == null)
        {
            return Array.Empty<BaseItem>();
        }

        var cccEvent = await _apiClient.GetEventAsync(eventGuid, cancellationToken).ConfigureAwait(false);
        if (cccEvent?.Related == null || cccEvent.Related.Count == 0)
        {
            return Array.Empty<BaseItem>();
        }

        var stored = StoredTalks(item.ChannelId);
        var excluded = query.ExcludeItemIds?.ToHashSet() ?? new HashSet<Guid>();

        var similar = cccEvent.Related
            .OrderByDescending(r => r.Weight)
            .Select(r => stored.GetValueOrDefault(r.EventGuid))
            .OfType<BaseItem>()
            .Where(i => i.Id != item.Id && !excluded.Contains(i.Id))
            .Take(query.Limit ?? DefaultLimit)
            .ToList();

        _logger.LogDebug(
            "Chaosflix: {Stored} of {Related} related talks of {Title} are in the library",
            similar.Count,
            cccEvent.Related.Count,
            item.Name);

        return similar;
    }

    /// <summary>
    /// The talks of this channel that Jellyfin has stored, by CCC event guid. Only a
    /// talk somebody has browsed to — or that the sync walked in (#8) — is in here at
    /// all; a recommendation pointing anywhere else is simply left out, because a
    /// similar-items provider can only answer with items that exist.
    /// </summary>
    private Dictionary<string, BaseItem> StoredTalks(Guid channelId)
    {
        var talks = new Dictionary<string, BaseItem>(StringComparer.Ordinal);

        foreach (var stored in _libraryManager.GetItemList(new InternalItemsQuery { ChannelIds = [channelId] }))
        {
            var guid = ChaosflixChannel.EventGuidOf(stored.ExternalId);
            if (guid == null)
            {
                continue;
            }

            // A talk is its own library item in every folder that lists it, so the same
            // guid turns up several times. The conference copy is the canonical one.
            if (!talks.ContainsKey(guid) || ChaosflixChannel.IsConferenceCopy(stored.ExternalId))
            {
                talks[guid] = stored;
            }
        }

        return talks;
    }
}
