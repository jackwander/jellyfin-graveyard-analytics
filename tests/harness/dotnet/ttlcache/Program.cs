// Drives TtlCache<T> (Phase 4 item 15) from the BUILT plugin assembly.
//
// This is the harness behind Phase 4's done-when: "a debounced keystroke issues no new SQL
// inside the TTL window". The cache is what makes that true, so it is exercised directly --
// with an injected clock, so the TTL can be crossed without sleeping -- and the factory
// counts its own invocations. A factory call is one full set of aggregate queries.
//
// PlaybackStatsProvider itself cannot be driven here: it reads Plugin.Instance, which needs
// a running Jellyfin. The caching behaviour was split out of it precisely so this much could
// be checked without one.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

class P
{
    static int failures;

    static void Check(string label, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label}{(ok || detail.Length == 0 ? "" : $"  -> {detail}")}");
        if (!ok)
        {
            failures++;
        }
    }

    static string FindPluginDll()
    {
        var explicitPath = Environment.GetEnvironmentVariable("GRAVEYARD_DLL");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "JellyfinGraveyardAnalytics", "bin", "Release", "net9.0",
                "JellyfinGraveyardAnalyticsPlugin.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Build the plugin first (dotnet build -c Release), or set GRAVEYARD_DLL.");
    }

    // A stand-in for the aggregate load, counting how often it actually runs.
    sealed class Loader
    {
        private readonly Func<Task>? _gate;
        public int Calls;

        public Loader(Func<Task>? gate = null) => _gate = gate;

        public Func<CancellationToken, Task<string>> Factory(string value)
            => async _ =>
            {
                Interlocked.Increment(ref Calls);
                if (_gate is not null)
                {
                    await _gate().ConfigureAwait(false);
                }

                return value;
            };
    }

    static void Main()
    {
        var asm = Assembly.LoadFrom(FindPluginDll());
        var open = asm.GetType("JellyfinGraveyardAnalytics.Services.TtlCache`1")!;
        var cacheType = open.MakeGenericType(typeof(string));

        var now = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);
        Func<DateTime> clock = () => now;

        object NewCache(TimeSpan lifetime)
            => Activator.CreateInstance(cacheType, new object[] { lifetime, clock })!;

        var getAsync = cacheType.GetMethod("GetAsync")!;
        var invalidate = cacheType.GetMethod("Invalidate")!;
        var loadCount = cacheType.GetProperty("LoadCount")!;

        string Get(object cache, string signature, Func<CancellationToken, Task<string>> factory)
        {
            var task = (Task<string>)getAsync.Invoke(
                cache, new object[] { signature, factory, CancellationToken.None })!;
            return task.GetAwaiter().GetResult();
        }

        // --- 1. repeated reads inside the window load once ---
        var ttl = TimeSpan.FromSeconds(60);
        var cache = NewCache(ttl);
        var loader = new Loader();

        var first = Get(cache, "sig", loader.Factory("A"));
        for (var i = 0; i < 9; i++)
        {
            Get(cache, "sig", loader.Factory("A"));
        }

        Check("ten reads inside the TTL run the aggregates once", loader.Calls == 1, $"calls={loader.Calls}");
        Check("the cached value is returned, not a fresh one", first == "A", first);
        Check("LoadCount agrees", (int)loadCount.GetValue(cache)! == 1);

        // --- 2. the window actually expires ---
        now = now.AddSeconds(59);
        Get(cache, "sig", loader.Factory("A"));
        Check("a read at 59s is still cached", loader.Calls == 1, $"calls={loader.Calls}");

        now = now.AddSeconds(2); // 61s total
        var refreshed = Get(cache, "sig", loader.Factory("B"));
        Check("a read past the TTL reloads", loader.Calls == 2, $"calls={loader.Calls}");
        Check("the reloaded value replaces the old one", refreshed == "B", refreshed);

        // --- 3. changing the inputs is a miss even inside the window ---
        var sigCache = NewCache(ttl);
        var sigLoader = new Loader();
        Get(sigCache, "local|120", sigLoader.Factory("local"));
        var switched = Get(sigCache, "tracearr|120", sigLoader.Factory("tracearr"));
        Check("switching engine mid-window is a miss, not stale data",
            sigLoader.Calls == 2 && switched == "tracearr", $"calls={sigLoader.Calls} value={switched}");

        var thresholdCache = NewCache(ttl);
        var thresholdLoader = new Loader();
        Get(thresholdCache, "local|120", thresholdLoader.Factory("120"));
        Get(thresholdCache, "local|300", thresholdLoader.Factory("300"));
        Check("editing the play threshold mid-window is a miss",
            thresholdLoader.Calls == 2, $"calls={thresholdLoader.Calls}");

        // --- 4. Invalidate: what Condemn / Pardon / LastRites call ---
        var invalidated = NewCache(ttl);
        var invalidateLoader = new Loader();
        Get(invalidated, "sig", invalidateLoader.Factory("before"));
        Get(invalidated, "sig", invalidateLoader.Factory("before"));
        Check("still one load before invalidating", invalidateLoader.Calls == 1, $"calls={invalidateLoader.Calls}");

        invalidate.Invoke(invalidated, null);
        var after = Get(invalidated, "sig", invalidateLoader.Factory("after"));
        Check("Invalidate forces the next read to reload, inside the TTL",
            invalidateLoader.Calls == 2 && after == "after", $"calls={invalidateLoader.Calls} value={after}");

        // --- 5. concurrent misses collapse into one load ---
        // A debounced keystroke fires several requests at once. Without the gate they would
        // all miss together and all hit the database.
        var release = new TaskCompletionSource();
        var stampedeCache = NewCache(ttl);
        var stampedeLoader = new Loader(() => release.Task);
        var factory = stampedeLoader.Factory("shared");

        var racers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var task = (Task<string>)getAsync.Invoke(
                stampedeCache, new object[] { "sig", factory, CancellationToken.None })!;
            return task.GetAwaiter().GetResult();
        })).ToArray();

        // Let them pile up on the gate before letting the single loader finish.
        Thread.Sleep(150);
        release.SetResult();
        Task.WaitAll(racers.Cast<Task>().ToArray(), TimeSpan.FromSeconds(10));

        Check("eight concurrent readers run the aggregates once",
            stampedeLoader.Calls == 1, $"calls={stampedeLoader.Calls}");
        Check("every concurrent reader gets the same value",
            racers.All(t => t.Result == "shared"));

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
