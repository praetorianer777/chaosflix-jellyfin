using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Chaosflix.Api;

/// <summary>
/// Simple in-memory cache with TTL expiration, a capacity bound and stampede protection.
/// </summary>
public class MemoryCache
{
    /// <summary>
    /// Default number of entries kept before the soonest-expiring ones are evicted.
    /// </summary>
    internal const int DefaultCapacity = 256;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, Gate> _locks = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;
    private long _hits;
    private long _misses;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryCache"/> class.
    /// </summary>
    /// <param name="capacity">Maximum number of entries to keep.</param>
    /// <param name="timeProvider">Clock used for expiry, for tests.</param>
    public MemoryCache(int capacity = DefaultCapacity, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal int Count => _cache.Count;

    internal int LockCount => _locks.Count;

    internal int Capacity => _capacity;

    internal long Hits => Interlocked.Read(ref _hits);

    internal long Misses => Interlocked.Read(ref _misses);

    /// <summary>
    /// Counts the entries that would still be served. Expired entries are only dropped
    /// when something is written, so <see cref="Count"/> can outrun what is usable.
    /// </summary>
    internal int CountLive(Func<string, bool>? predicate = null)
    {
        var now = _timeProvider.GetUtcNow();
        return _cache.Count(entry =>
            entry.Value.ExpiresAt > now && (predicate is null || predicate(entry.Key)));
    }

    /// <summary>
    /// Gets or creates a cached value.
    /// </summary>
    public async Task<T> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken)
    {
        if (TryGet<T>(key, out var hit))
        {
            Interlocked.Increment(ref _hits);
            return hit;
        }

        var gate = RentGate(key);
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (TryGet<T>(key, out hit))
                {
                    Interlocked.Increment(ref _hits);
                    return hit;
                }

                Interlocked.Increment(ref _misses);
                var value = await factory(cancellationToken).ConfigureAwait(false);
                _cache[key] = new CacheEntry(value!, _timeProvider.GetUtcNow().Add(ttl));
                Evict();
                return value;
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            ReturnGate(key, gate);
        }
    }

    /// <summary>
    /// Invalidates a single cached entry.
    /// </summary>
    public void Remove(string key) => _cache.TryRemove(key, out _);

    /// <summary>
    /// Invalidates all cached entries and releases the per-key locks that are idle.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        foreach (var pair in _locks)
        {
            TryDropGate(pair.Key, pair.Value);
        }
    }

    private bool TryGet<T>(string key, out T value)
    {
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > _timeProvider.GetUtcNow())
        {
            value = (T)entry.Value;
            return true;
        }

        value = default!;
        return false;
    }

    private void Evict()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var entry in _cache)
        {
            if (entry.Value.ExpiresAt <= now)
            {
                _cache.TryRemove(entry.Key, out _);
            }
        }

        while (_cache.Count > _capacity)
        {
            var oldest = _cache.OrderBy(e => e.Value.ExpiresAt).Select(e => e.Key).FirstOrDefault();
            if (oldest is null || !_cache.TryRemove(oldest, out _))
            {
                break;
            }
        }
    }

    // A gate lives only as long as someone is using it, so the lock dictionary stays
    // bounded by the number of in-flight calls instead of the number of keys ever seen.
    private Gate RentGate(string key)
    {
        while (true)
        {
            var gate = _locks.GetOrAdd(key, _ => new Gate());
            lock (gate)
            {
                if (!gate.Dropped)
                {
                    gate.Users++;
                    return gate;
                }
            }
        }
    }

    private void ReturnGate(string key, Gate gate)
    {
        lock (gate)
        {
            gate.Users--;
            DropIfIdle(key, gate);
        }
    }

    private void TryDropGate(string key, Gate gate)
    {
        lock (gate)
        {
            DropIfIdle(key, gate);
        }
    }

    private void DropIfIdle(string key, Gate gate)
    {
        if (gate.Users > 0 || gate.Dropped)
        {
            return;
        }

        gate.Dropped = true;
        _locks.TryRemove(new KeyValuePair<string, Gate>(key, gate));
        gate.Semaphore.Dispose();
    }

    private sealed record CacheEntry(object Value, DateTimeOffset ExpiresAt);

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }

        public bool Dropped { get; set; }
    }
}
