using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using JellyfinGraveyardAnalytics.Configuration;

namespace JellyfinGraveyardAnalytics.Services
{
    /// <summary>
    /// Every playback aggregate the media tabs need, from whichever engine is enabled.
    /// Assembled together so a single row can never mix engines — before this existed the
    /// Chapel and Sanctuary read plays from Tracearr and durations from the local database
    /// in the same row.
    /// </summary>
    public sealed class PlaybackStats
    {
        public Dictionary<string, int> PlayCounts { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, HashSet<string>> ItemViewers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, DateTime> LastPlayedDates { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, long> PlayDurations { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Oldest activity the active engine can see, or null when there is none. Drives the
        /// Morgue's floor gate (D1).
        /// </summary>
        public DateTime? HistoryFloorUtc { get; init; }
    }

    /// <summary>
    /// A single-slot cache with a time-to-live, guarding an expensive load.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="PlaybackStatsProvider"/> because the *cache* is what has to
    /// be the singleton while the provider is scoped, and separately because the caching
    /// behaviour is then testable on its own — it depends on nothing but a clock.
    /// </remarks>
    public sealed class TtlCache<T> : IDisposable
        where T : class
    {
        private readonly TimeSpan _lifetime;
        private readonly Func<DateTime> _clock;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private T? _value;
        private DateTime _loadedAt;
        private string _signature = string.Empty;

        public TtlCache(TimeSpan lifetime, Func<DateTime>? clock = null)
        {
            _lifetime = lifetime;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>Number of times the factory actually ran. Diagnostics and tests.</summary>
        public int LoadCount { get; private set; }

        /// <summary>
        /// Returns the cached value, or loads one. <paramref name="signature"/> is a stamp of
        /// the inputs the value depends on — a change to it is a miss even inside the TTL,
        /// so flipping engines or editing the play threshold cannot be served stale data.
        /// </summary>
        public async Task<T> GetAsync(
            string signature,
            Func<CancellationToken, Task<T>> factory,
            CancellationToken cancellationToken)
        {
            if (TryRead(signature, out var early))
            {
                return early;
            }

            // One loader at a time. A debounced keystroke fires several requests at once and
            // they would otherwise all miss and all hit the database.
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-check: another caller may have loaded it while this one queued.
                if (TryRead(signature, out var loaded))
                {
                    return loaded;
                }

                var value = await factory(cancellationToken).ConfigureAwait(false);

                _value = value;
                _loadedAt = _clock();
                _signature = signature;
                LoadCount++;

                return value;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Drops the cached value. Called when the plugin itself changes what the aggregates
        /// would say — condemning, pardoning or deleting an item — so the next read is fresh
        /// rather than up to a TTL stale.
        /// </summary>
        public void Invalidate()
        {
            _value = null;
            _signature = string.Empty;
        }

        private bool TryRead(string signature, out T value)
        {
            var cached = _value;

            if (cached is not null
                && _signature == signature
                && _clock() - _loadedAt < _lifetime)
            {
                value = cached;
                return true;
            }

            value = default!;
            return false;
        }

        /// <summary>
        /// Releases the loader gate. Registered as a singleton, so in practice this runs once
        /// when Jellyfin tears the container down; it exists because the type owns a
        /// <see cref="SemaphoreSlim"/> and a type that owns one and cannot be disposed is a
        /// handle leak waiting for a second registration site.
        /// </summary>
        public void Dispose() => _gate.Dispose();
    }

    /// <summary>
    /// The one place the media tabs get playback aggregates from, with a short TTL cache in
    /// front of it.
    /// </summary>
    /// <remarks>
    /// Each media tab used to run the aggregates itself, so a debounced keystroke re-ran four
    /// full-table SQL passes — or, on Tracearr, re-walked the history — per request, three
    /// tabs over. The TTL is deliberately short: this is a dashboard, and the cost being
    /// avoided is repeating identical work inside one burst of typing, not serving an old
    /// library.
    /// </remarks>
    public sealed class PlaybackStatsProvider
    {
        /// <summary>
        /// How long an aggregate set is reused. Long enough to absorb a burst of keystrokes
        /// and a tab switch, short enough that a play registered elsewhere shows up promptly.
        /// </summary>
        public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How far back the Tracearr history aggregate reaches. Doubles as the history floor
        /// on that engine, since the aggregate cannot see past the window we request.
        /// </summary>
        public const int TracearrHistoryWeeks = 52;

        private readonly TracearrService _tracearrService;
        private readonly Database.Repository _repository;
        private readonly IPluginConfigurationSource _configSource;
        private readonly ILogger<PlaybackStatsProvider> _logger;
        private readonly TtlCache<PlaybackStats> _cache;

        /// <summary>
        /// The cache is injected rather than owned: it is the singleton, this type is not.
        /// <see cref="TracearrService"/> is registered through <c>AddHttpClient</c> and so is
        /// transient, and holding a transient inside a singleton would pin one
        /// <see cref="System.Net.Http.HttpClient"/> forever and defeat the factory's handler
        /// rotation.
        /// </summary>
        public PlaybackStatsProvider(
            TracearrService tracearrService,
            Database.Repository repository,
            IPluginConfigurationSource configSource,
            TtlCache<PlaybackStats> cache,
            ILogger<PlaybackStatsProvider> logger)
        {
            _tracearrService = tracearrService;
            _repository = repository;
            _configSource = configSource;
            _cache = cache;
            _logger = logger;
        }

        public Task<PlaybackStats> GetAsync(CancellationToken cancellationToken)
        {
            var config = _configSource.Current;

            // Everything the aggregates depend on. Changing any of it must not be served from
            // cache, so it is part of the key rather than something Invalidate has to catch.
            var signature = string.Join(
                '|',
                config.EnableTracearr ? "tracearr" : "local",
                config.MinPlayDurationSeconds,
                config.EnableTracearr ? config.TracearrUrl : string.Empty);

            return _cache.GetAsync(signature, LoadAsync, cancellationToken);
        }

        /// <summary>
        /// Drops the cached aggregates after the plugin changes the library itself.
        /// </summary>
        public void Invalidate() => _cache.Invalidate();

        private async Task<PlaybackStats> LoadAsync(CancellationToken cancellationToken)
        {
            var config = _configSource.Current;

            if (config.EnableTracearr)
            {
                var stats = await _tracearrService
                    .GetTracearrPlaybackStatsAsync(TracearrHistoryWeeks, cancellationToken)
                    .ConfigureAwait(false);

                return new PlaybackStats
                {
                    PlayCounts = stats.playCounts,
                    ItemViewers = stats.itemViewers,
                    LastPlayedDates = stats.lastPlayedDates,
                    PlayDurations = stats.playDurations,

                    // The aggregate is bounded by the window we ask for, so that window is
                    // the floor — there is nothing older for it to have seen.
                    HistoryFloorUtc = DateTime.UtcNow.AddDays(-7 * TracearrHistoryWeeks)
                };
            }

            if (!_repository.PlaybackDatabaseExists)
            {
                throw new PlaybackDataUnavailableException(
                    "The Playback Reporting plugin database was not found.");
            }

            var threshold = config.MinPlayDurationSeconds;

            return new PlaybackStats
            {
                PlayCounts = _repository.GetItemPlayCounts(threshold),
                ItemViewers = _repository.GetItemViewers(threshold),
                LastPlayedDates = _repository.GetItemLastPlayedDates(threshold),
                PlayDurations = _repository.GetItemPlayDurations(threshold),
                HistoryFloorUtc = ReadHistoryFloor()
            };
        }

        private DateTime? ReadHistoryFloor()
        {
            try
            {
                return _repository.GetHistoryFloorDate();
            }
            catch (Exception ex)
            {
                // Treated as "no history", which yields an empty Morgue and a banner rather
                // than a library-wide list of unverifiable claims.
                _logger.LogWarning(ex, "Could not read the playback history floor.");
                return null;
            }
        }
    }
}
