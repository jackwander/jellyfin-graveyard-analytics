using Dapper;
using Microsoft.Data.Sqlite;
using MediaBrowser.Common.Configuration;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System;

namespace JellyfinGraveyardAnalytics.Database
{
    /// <summary>
    /// One Guestbook row as Playback Reporting stores it.
    /// </summary>
    /// <remarks>
    /// Every text column is typed as <see cref="string"/> rather than a parsed type, because
    /// SQLite column types are declarations rather than guarantees: <c>DateCreated</c> is
    /// declared <c>DATETIME</c> but stored as a naive UTC string, so parsing stays where the
    /// fallback lives instead of happening inside the mapper.
    /// <para>
    /// This buys tolerance for the *text* columns only. The numeric ones are typed, and Dapper
    /// builds one deserializer per query from the first row's storage classes — so a REAL or a
    /// BLOB where an INTEGER belongs fails the whole query rather than one row, where the old
    /// <c>dynamic</c> path silently truncated or stringified it. Playback Reporting cannot
    /// write those (it binds an int duration, and TEXT affinity keeps numbers out of the id
    /// columns), and a 500 beats silent garbage, so this is a deliberate trade.
    /// </para>
    /// </remarks>
    public sealed class PlaybackActivityRow
    {
        public string? DateCreated { get; set; }
        public string? UserId { get; set; }
        public string? ItemName { get; set; }
        public string? ItemType { get; set; }
        public string? ClientName { get; set; }
        public string? DeviceName { get; set; }
        public string? PlaybackMethod { get; set; }
        public long? PlayDuration { get; set; }
    }

    /// <summary>
    /// Read-only access to the Playback Reporting plugin's database. It belongs to another
    /// plugin, so nothing here ever modifies its contents.
    /// </summary>
    /// <remarks>
    /// "Read-only" is about the data, not the directory: opening a WAL database still creates
    /// the <c>-shm</c> shared-memory index beside it if it is missing, read-only connection or
    /// not. That is SQLite's, not ours, and it is why a stale WAL is readable at all — but it
    /// does mean this writes a sidecar into another plugin's directory, and that a data path
    /// mounted read-only cannot serve a stale WAL. Measured in
    /// <c>tests/harness/dotnet/repository</c>, probes E1-E4.
    /// </remarks>
    public class Repository
    {
        private readonly string _playbackDbConn;

        public string PlaybackDbPath { get; }

        public Repository(IApplicationPaths appPaths)
        {
            PlaybackDbPath = Path.Combine(appPaths.DataPath, "playback_reporting.db");

            // Mode=ReadOnly, and nothing may overwrite it afterwards (finding 3). The
            // connection string was built with this flag and then reassigned without it, so
            // every read held a writable handle on another plugin's database — and because a
            // writable open *creates* a missing file, an absent Playback Reporting installation
            // was silently manufactured as an empty database that reads as "installed, no
            // activity yet". Read-only fails instead, which is the truth.
            _playbackDbConn = $"Data Source={PlaybackDbPath};Mode=ReadOnly;";
        }

        /// <summary>
        /// Whether the Playback Reporting database is actually there. Checked before opening,
        /// because a read-only connection to a missing file throws SQLite error 14 and the
        /// admin needs "install Playback Reporting", not a database error.
        /// </summary>
        public bool PlaybackDatabaseExists => File.Exists(PlaybackDbPath);

        /// <summary>
        /// Why the Playback Reporting data cannot be read, or <see langword="null"/> when it
        /// can. Log-only text: the client-facing message is the controller's own constant.
        /// </summary>
        /// <remarks>
        /// <see cref="PlaybackDatabaseExists"/> is a file test, so a database that exists but
        /// carries no <c>PlaybackActivity</c> table passed the guard, reached the queries and
        /// surfaced as a 500 — where the admin needed the actionable 400. That is not a
        /// hypothetical shape: Playback Reporting creates its database before its schema, and
        /// this plugin opens it read-only, so it can neither create nor migrate the table it
        /// is missing.
        /// <para>
        /// Deliberately narrow. A file that is present, has the table, and then fails to open
        /// for some other reason — locked, or not a database at all — still throws, because
        /// reporting a transient <c>SQLITE_BUSY</c> as "Playback Reporting is not installed"
        /// would be a worse answer than an error.
        /// </para>
        /// </remarks>
        public string? PlaybackDataUnavailableReason()
        {
            if (!PlaybackDatabaseExists)
            {
                return "The Playback Reporting plugin database was not found at " + PlaybackDbPath;
            }

            using var connection = new SqliteConnection(_playbackDbConn);

            var table = connection.QuerySingleOrDefault<string?>(
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'PlaybackActivity'");

            return table is null
                ? "The Playback Reporting database exists but has no PlaybackActivity table."
                : null;
        }

        public Dictionary<string, int> GetItemPlayCounts(int minPlayDurationSeconds)
        {
            using var connection = new SqliteConnection(_playbackDbConn);

            var rows = connection.Query<PlayCountRow>(@"
                SELECT ItemId, COUNT(*) as PlayCount
                FROM PlaybackActivity
                WHERE ItemId IS NOT NULL
                AND PlayDuration >= @MinPlayDuration
                GROUP BY ItemId
            ", new { MinPlayDuration = minPlayDurationSeconds });

            var dict = new Dictionary<string, int>();

            foreach (var row in rows)
            {
                var itemId = NormalizeItemId(row.ItemId);
                if (itemId != null)
                {
                    dict[itemId] = row.PlayCount;
                }
            }

            return dict;
        }

        public Dictionary<string, HashSet<string>> GetItemViewers(int minPlayDurationSeconds)
        {
            using var connection = new SqliteConnection(_playbackDbConn);

            var rows = connection.Query<ItemViewerRow>(@"
                SELECT ItemId, UserId
                FROM PlaybackActivity
                WHERE ItemId IS NOT NULL AND UserId IS NOT NULL
                AND PlayDuration >= @MinPlayDuration
            ", new { MinPlayDuration = minPlayDurationSeconds });

            var viewerMap = new Dictionary<string, HashSet<string>>();

            foreach (var row in rows)
            {
                var itemId = NormalizeItemId(row.ItemId);

                if (itemId == null || string.IsNullOrEmpty(row.UserId))
                {
                    continue;
                }

                if (!viewerMap.TryGetValue(itemId, out var viewers))
                {
                    viewers = new HashSet<string>();
                    viewerMap[itemId] = viewers;
                }

                viewers.Add(row.UserId);
            }

            return viewerMap;
        }

        public Dictionary<string, DateTime> GetItemLastPlayedDates(int minPlayDurationSeconds)
        {
            using var connection = new SqliteConnection(_playbackDbConn);

            // Without this filter a 10-second check bumps "Last Breath" and shields the
            // item from every time-based rule.
            var rows = connection.Query<LastPlayedRow>(@"
                SELECT ItemId, MAX(DateCreated) as LastPlayedDate
                FROM PlaybackActivity
                WHERE ItemId IS NOT NULL
                AND PlayDuration >= @MinPlayDuration
                GROUP BY ItemId
            ", new { MinPlayDuration = minPlayDurationSeconds });

            var dict = new Dictionary<string, DateTime>();

            foreach (var row in rows)
            {
                var itemId = NormalizeItemId(row.ItemId);

                if (itemId != null && TryParseStoredUtc(row.LastPlayedDate, out var parsedDate))
                {
                    dict[itemId] = parsedDate;
                }
            }

            return dict;
        }

        public Dictionary<string, long> GetItemPlayDurations(int minPlayDurationSeconds)
        {
            using var connection = new SqliteConnection(_playbackDbConn);

            var rows = connection.Query<PlayDurationRow>(@"
                SELECT ItemId, SUM(PlayDuration) as TotalDuration
                FROM PlaybackActivity
                WHERE ItemId IS NOT NULL
                AND PlayDuration >= @MinPlayDuration
                GROUP BY ItemId
            ", new { MinPlayDuration = minPlayDurationSeconds });

            var dict = new Dictionary<string, long>();

            foreach (var row in rows)
            {
                var itemId = NormalizeItemId(row.ItemId);

                // SUM over a group of NULLs is NULL, not 0.
                if (itemId != null && row.TotalDuration.HasValue)
                {
                    dict[itemId] = row.TotalDuration.Value;
                }
            }

            return dict;
        }

        /// <summary>
        /// Oldest activity Playback Reporting knows about, or null on an empty database.
        /// Everything before this date is invisible to us: an item added earlier reads as
        /// zero-play whether it was watched or not, which is why the Morgue withholds items
        /// added before it (D1's floor gate).
        /// </summary>
        public DateTime? GetHistoryFloorDate()
        {
            using var connection = new SqliteConnection(_playbackDbConn);

            var floor = connection.QuerySingleOrDefault<string?>(
                "SELECT MIN(DateCreated) FROM PlaybackActivity");

            return TryParseStoredUtc(floor, out var parsed) ? parsed : null;
        }

        /// <summary>
        /// Parses a stored timestamp as the naive UTC it is, yielding a
        /// <see cref="DateTimeKind.Utc"/> value.
        /// </summary>
        /// <remarks>
        /// Shared because the two callers had drifted apart and only one was right (finding 30).
        /// Both parts matter. <see cref="CultureInfo.InvariantCulture"/>, because the format is
        /// SQLite's and not the server operator's. And <c>AssumeUniversal | AdjustToUniversal</c>,
        /// because without them the result is <see cref="DateTimeKind.Unspecified"/> — which
        /// serializes with no <c>Z</c>, so a browser reads the instant as its own local time and
        /// the column is a whole day out west of UTC.
        ///
        /// Public because <see cref="Services.AnalyticsService"/> reads the same column for the
        /// Guestbook and had its own bare <c>DateTime.TryParse</c> — a third site of the same
        /// drift, found by the analyzers in Phase 6.
        /// </remarks>
        public static bool TryParseStoredUtc(string? value, out DateTime utc)
            => DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out utc);

        /// <summary>
        /// Guestbook rows, newest first, capped at <paramref name="rowLimit"/>. Returns
        /// whether the cap truncated the result so the caller can say so rather than
        /// presenting a partial window as complete.
        /// </summary>
        public (List<PlaybackActivityRow> Rows, bool Truncated) GetRawPlaybackActivity(
            DateTime startDate, DateTime endDate, int rowLimit)
        {
            using var connection = new SqliteConnection(_playbackDbConn);

            // One row over the cap: if it comes back, there was more to show.
            var rows = connection.Query<PlaybackActivityRow>(@"
                SELECT
                    DateCreated,
                    UserId,
                    ItemName,
                    ItemType,
                    ClientName,
                    DeviceName,
                    PlaybackMethod,
                    PlayDuration
                FROM PlaybackActivity
                WHERE DateCreated >= @Start AND DateCreated <= @End
                ORDER BY DateCreated DESC
                LIMIT @Limit",
                new
                {
                    Start = FormatSqliteUtc(startDate),
                    End = FormatSqliteUtc(endDate),
                    Limit = rowLimit + 1
                }).ToList();

            if (rows.Count > rowLimit)
            {
                rows.RemoveRange(rowLimit, rows.Count - rowLimit);
                return (rows, true);
            }

            return (rows, false);
        }

        /// <summary>
        /// Strips dashes so the key matches the <c>Guid.ToString("N")</c> form every consumer
        /// of these dictionaries looks items up by. Returns null for a row carrying no id.
        /// </summary>
        private static string? NormalizeItemId(string? itemId)
            => string.IsNullOrEmpty(itemId) ? null : itemId.Replace("-", string.Empty);

        /// <summary>
        /// Playback Reporting stores naive UTC strings, so a local-time bound would shift
        /// the window by the server's offset.
        /// </summary>
        private static string FormatSqliteUtc(DateTime value)
        {
            var utc = value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };

            return utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        // Row shapes for the aggregates. Dapper's `dynamic` was doing the same mapping at
        // runtime with no compiler check on the column names, so a renamed alias failed as a
        // missing property at request time rather than at build time.

        private sealed class PlayCountRow
        {
            public string? ItemId { get; set; }

            /// <summary>COUNT(*), so never null.</summary>
            public int PlayCount { get; set; }
        }

        private sealed class ItemViewerRow
        {
            public string? ItemId { get; set; }
            public string? UserId { get; set; }
        }

        private sealed class LastPlayedRow
        {
            public string? ItemId { get; set; }

            /// <summary>Left as text: see <see cref="PlaybackActivityRow"/>.</summary>
            public string? LastPlayedDate { get; set; }
        }

        private sealed class PlayDurationRow
        {
            public string? ItemId { get; set; }
            public long? TotalDuration { get; set; }
        }
    }
}
