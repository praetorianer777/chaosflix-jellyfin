using Jellyfin.Plugin.Chaosflix.Api;

namespace Jellyfin.Plugin.Chaosflix.Tests;

public class MemoryCacheTests
{
    private readonly MemoryCache _cache = new();

    [Fact]
    public async Task ReturnsCachedValueWithinTtl()
    {
        var calls = 0;
        Task<int> Factory(CancellationToken _) => Task.FromResult(++calls);

        var first = await _cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(1), Factory, CancellationToken.None);
        var second = await _cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(1), Factory, CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RecreatesValueAfterTtlExpires()
    {
        var calls = 0;
        Task<int> Factory(CancellationToken _) => Task.FromResult(++calls);

        await _cache.GetOrCreateAsync("k", TimeSpan.FromMilliseconds(20), Factory, CancellationToken.None);
        await Task.Delay(60);
        var value = await _cache.GetOrCreateAsync("k", TimeSpan.FromMilliseconds(20), Factory, CancellationToken.None);

        Assert.Equal(2, value);
    }

    [Fact]
    public async Task KeysAreIndependent()
    {
        var a = await _cache.GetOrCreateAsync("a", TimeSpan.FromMinutes(1), _ => Task.FromResult("A"), CancellationToken.None);
        var b = await _cache.GetOrCreateAsync("b", TimeSpan.FromMinutes(1), _ => Task.FromResult("B"), CancellationToken.None);

        Assert.Equal("A", a);
        Assert.Equal("B", b);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneFactoryRun()
    {
        var calls = 0;
        var gate = new TaskCompletionSource();

        async Task<int> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return 42;
        }

        var callers = Enumerable.Range(0, 10)
            .Select(_ => _cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(1), Factory, CancellationToken.None))
            .ToList();

        await Task.Delay(50);
        gate.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.All(results, r => Assert.Equal(42, r));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailedFactoryIsNotCached()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _cache.GetOrCreateAsync<int>("k", TimeSpan.FromMinutes(1), _ => throw new InvalidOperationException(), CancellationToken.None));

        var value = await _cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(1), _ => Task.FromResult(7), CancellationToken.None);

        Assert.Equal(7, value);
    }

    [Fact]
    public async Task ClearForcesRecreation()
    {
        var calls = 0;
        Task<int> Factory(CancellationToken _) => Task.FromResult(++calls);

        await _cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(1), Factory, CancellationToken.None);
        _cache.Clear();
        var value = await _cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(1), Factory, CancellationToken.None);

        Assert.Equal(2, value);
    }

    [Fact]
    public async Task ExpiredEntriesAreEvictedOnWrite()
    {
        var time = new FakeTime();
        var cache = new MemoryCache(timeProvider: time);

        await cache.GetOrCreateAsync("a", TimeSpan.FromMinutes(1), _ => Task.FromResult(1), CancellationToken.None);
        await cache.GetOrCreateAsync("b", TimeSpan.FromMinutes(1), _ => Task.FromResult(2), CancellationToken.None);
        Assert.Equal(2, cache.Count);

        time.Advance(TimeSpan.FromMinutes(2));
        await cache.GetOrCreateAsync("c", TimeSpan.FromMinutes(1), _ => Task.FromResult(3), CancellationToken.None);

        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task CapacityIsBounded()
    {
        var cache = new MemoryCache(capacity: 4);

        for (var i = 0; i < 200; i++)
        {
            var value = i;
            await cache.GetOrCreateAsync($"k{i}", TimeSpan.FromMinutes(1), _ => Task.FromResult(value), CancellationToken.None);
        }

        Assert.True(cache.Count <= 4, $"cache kept {cache.Count} entries");
    }

    [Fact]
    public async Task LockDictionaryDoesNotGrowWithKeys()
    {
        for (var i = 0; i < 200; i++)
        {
            var value = i;
            await _cache.GetOrCreateAsync($"k{i}", TimeSpan.FromMinutes(1), _ => Task.FromResult(value), CancellationToken.None);
        }

        Assert.Equal(0, _cache.LockCount);
    }

    [Fact]
    public async Task ConcurrentCallersLeaveNoLockBehind()
    {
        var gate = new TaskCompletionSource();

        async Task<int> Factory(CancellationToken _)
        {
            await gate.Task;
            return 42;
        }

        var callers = Enumerable.Range(0, 10)
            .Select(_ => _cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(1), Factory, CancellationToken.None))
            .ToList();

        await Task.Delay(50);
        gate.SetResult();
        await Task.WhenAll(callers);

        Assert.Equal(0, _cache.LockCount);
    }

    [Fact]
    public async Task ClearReleasesEntriesAndLocks()
    {
        await _cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(1), _ => Task.FromResult(1), CancellationToken.None);

        _cache.Clear();

        Assert.Equal(0, _cache.Count);
        Assert.Equal(0, _cache.LockCount);
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
