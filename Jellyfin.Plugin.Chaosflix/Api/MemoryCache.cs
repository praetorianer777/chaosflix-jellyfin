using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Chaosflix.Api;

/// <summary>
/// Simple in-memory cache with TTL expiration.
/// </summary>
public class MemoryCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    /// <summary>
    /// Gets or creates a cached value.
    /// </summary>
    public async Task<T> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return (T)entry.Value;
        }

        var value = await factory(cancellationToken).ConfigureAwait(false);
        _cache[key] = new CacheEntry(value!, DateTimeOffset.UtcNow.Add(ttl));
        return value;
    }

    /// <summary>
    /// Invalidates all cached entries.
    /// </summary>
    public void Clear() => _cache.Clear();

    private sealed record CacheEntry(object Value, DateTimeOffset ExpiresAt);
}
