using JellyfinGraveyardAnalytics.Services;

namespace GraveyardAnalytics.Tests;

/// <summary>
/// The aggregate cache (Phase 4). Every read of a media tab needs four aggregate queries
/// over the whole Playback Reporting database, and the search box is debounced rather than
/// gated — so a typed word used to issue a fresh set per keystroke, concurrently.
///
/// The clock is injected, so the TTL is crossed without sleeping and the tests are
/// deterministic. A test that slept would be slow *and* flaky, and the seam exists in the
/// production type precisely so it does not have to.
/// </summary>
public class TtlCacheTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <summary>A clock the test advances by hand.</summary>
    private sealed class TestClock
    {
        private DateTime _now = new(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc);

        public DateTime Now() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class Counter
    {
        public int Loads { get; private set; }

        public Task<string> LoadAsync(CancellationToken cancellationToken)
        {
            Loads++;
            return Task.FromResult($"load-{Loads}");
        }
    }

    [Fact]
    public async Task TheFirstReadLoadsAndTheRestInsideTheWindowDoNot()
    {
        var clock = new TestClock();
        var counter = new Counter();
        using var cache = new TtlCache<string>(Lifetime, clock.Now);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal("load-1", await cache.GetAsync("sig", counter.LoadAsync, CancellationToken.None));
        }

        Assert.Equal(1, counter.Loads);
        Assert.Equal(1, cache.LoadCount);
    }

    /// <summary>
    /// The boundary in both directions. One second short of the lifetime is still a hit; one
    /// second past it is a miss.
    /// </summary>
    [Fact]
    public async Task TheValueExpiresWhenTheLifetimeElapses()
    {
        var clock = new TestClock();
        var counter = new Counter();
        using var cache = new TtlCache<string>(Lifetime, clock.Now);

        await cache.GetAsync("sig", counter.LoadAsync, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal("load-1", await cache.GetAsync("sig", counter.LoadAsync, CancellationToken.None));
        Assert.Equal(1, counter.Loads);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("load-2", await cache.GetAsync("sig", counter.LoadAsync, CancellationToken.None));
        Assert.Equal(2, counter.Loads);
    }

    /// <summary>
    /// The signature is a stamp of the inputs the value depends on — the active engine and
    /// the play threshold. A change to either has to be a miss, not stale data: serving the
    /// old aggregates after an admin lowers the threshold would make the setting look
    /// broken for up to a full TTL.
    /// </summary>
    [Fact]
    public async Task ASignatureChangeIsAMissAndNotStaleData()
    {
        var clock = new TestClock();
        var counter = new Counter();
        using var cache = new TtlCache<string>(Lifetime, clock.Now);

        Assert.Equal("load-1", await cache.GetAsync("local:120", counter.LoadAsync, CancellationToken.None));
        Assert.Equal("load-2", await cache.GetAsync("local:30", counter.LoadAsync, CancellationToken.None));
        Assert.Equal("load-3", await cache.GetAsync("tracearr:30", counter.LoadAsync, CancellationToken.None));

        Assert.Equal(3, counter.Loads);
    }

    /// <summary>
    /// Condemning, pardoning or exorcising an item changes what the aggregates would say, so
    /// the next read has to be fresh rather than up to a TTL stale — the admin has just
    /// acted and expects the table to reflect it.
    /// </summary>
    [Fact]
    public async Task InvalidateForcesAReloadInsideTheWindow()
    {
        var clock = new TestClock();
        var counter = new Counter();
        using var cache = new TtlCache<string>(Lifetime, clock.Now);

        await cache.GetAsync("sig", counter.LoadAsync, CancellationToken.None);
        await cache.GetAsync("sig", counter.LoadAsync, CancellationToken.None);
        Assert.Equal(1, counter.Loads);

        cache.Invalidate();

        Assert.Equal("load-2", await cache.GetAsync("sig", counter.LoadAsync, CancellationToken.None));
        Assert.Equal(2, counter.Loads);
    }

    /// <summary>
    /// The debounced-keystroke case, which is what the cache is for. Eight readers arriving
    /// at once on a cold cache must collapse into one load — without the gate they all miss
    /// and all query, which is worse than no cache at all.
    /// </summary>
    [Fact]
    public async Task ConcurrentReadersCollapseIntoASingleLoad()
    {
        var clock = new TestClock();
        var loads = 0;
        var release = new TaskCompletionSource();
        using var cache = new TtlCache<string>(Lifetime, clock.Now);

        async Task<string> SlowLoad(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref loads);
            await release.Task.ConfigureAwait(false);
            return "the one value";
        }

        var readers = Enumerable.Range(0, 8)
            .Select(_ => cache.GetAsync("sig", SlowLoad, CancellationToken.None))
            .ToList();

        // Let the first reader reach the factory and the other seven queue behind the gate.
        release.SetResult();

        var values = await Task.WhenAll(readers);

        Assert.Equal(1, loads);
        Assert.Equal(1, cache.LoadCount);
        Assert.All(values, v => Assert.Equal("the one value", v));
    }

    /// <summary>
    /// A failed load must not be cached. The aggregates fail when Playback Reporting's
    /// database is missing, and caching that failure would keep the tab broken for a full
    /// TTL after the admin installed the plugin it asked for.
    /// </summary>
    [Fact]
    public async Task AFailedLoadIsNotRememberedAsAValue()
    {
        var clock = new TestClock();
        var attempts = 0;
        using var cache = new TtlCache<string>(Lifetime, clock.Now);

        Task<string> Failing(CancellationToken cancellationToken)
        {
            attempts++;
            throw new InvalidOperationException("database missing");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetAsync("sig", Failing, CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetAsync("sig", Failing, CancellationToken.None));

        Assert.Equal(2, attempts);
        Assert.Equal(0, cache.LoadCount);
    }
}
