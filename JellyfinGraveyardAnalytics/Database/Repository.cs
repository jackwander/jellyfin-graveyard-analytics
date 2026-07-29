using Dapper;
using Microsoft.Data.Sqlite;
using MediaBrowser.Common.Configuration;
using JellyfinGraveyardAnalytics.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;

namespace JellyfinGraveyardAnalytics.Database
{
    public class Repository
    {
        private readonly string _playbackDbConn;
        private readonly string _analyticsDbConn;
        public string PlaybackDbPath { get; private set; }

        public Repository(IApplicationPaths appPaths)
        {
            var playbackDbPath = Path.Combine(appPaths.DataPath, "playback_reporting.db");
            _playbackDbConn = $"Data Source={playbackDbPath};Mode=ReadOnly;";
            var analyticsFolder = Path.Combine(appPaths.ProgramDataPath, "plugins", "configurations");
            Directory.CreateDirectory(analyticsFolder);
            var analyticsDbPath = Path.Combine(analyticsFolder, "AdvancedAnalytics.db");
            _analyticsDbConn = $"Data Source={analyticsDbPath};";
            DatabaseInitializer.Initialize(analyticsDbPath);
            PlaybackDbPath = System.IO.Path.Combine(appPaths.DataPath, "playback_reporting.db");
            _playbackDbConn = $"Data Source={PlaybackDbPath}";
        }

        public HashSet<string> GetWatchedMediaIds()
        {
            using var connection = new SqliteConnection(_playbackDbConn);
            var ids = connection.Query<string>("SELECT DISTINCT ItemId FROM PlaybackActivity");
            return new HashSet<string>(ids);
        }

        public dynamic GetOverallStats()
        {
            using var connection = new SqliteConnection(_playbackDbConn);
            var result = connection.QuerySingleOrDefault(@"
                SELECT
                    COUNT(*) as TotalPlays,
                    SUM(PlayDuration) / 3600 as TotalWatchTimeHours
                FROM PlaybackActivity
                WHERE PlayDuration > 0");

            return result ?? new { TotalPlays = 0, TotalWatchTimeHours = 0 };
        }

        public IEnumerable<dynamic> GetActivityTimeline()
        {
            using var connection = new SqliteConnection(_playbackDbConn);
            return connection.Query(@"
                SELECT date(DateCreated) as PlayDate, COUNT(*) as PlayCount
                FROM PlaybackActivity
                WHERE PlayDuration > 0
                GROUP BY date(DateCreated)
                ORDER BY PlayDate DESC
                LIMIT 7");
        }

        public IEnumerable<string> GetAllActiveUserIds()
        {
            using var connection = new SqliteConnection(_playbackDbConn);
            return connection.Query<string>("SELECT DISTINCT UserId FROM PlaybackActivity");
        }

        public HashSet<string> GetWatchedMediaIdsByUser(string userId)
        {
            using var connection = new SqliteConnection(_playbackDbConn);
            var formattedUserId = userId.Replace("-", "");
            var ids = connection.Query<string>(
                "SELECT DISTINCT ItemId FROM PlaybackActivity WHERE UserId = @userId",
                new { userId = formattedUserId });
            return new HashSet<string>(ids);
        }

        public System.Collections.Generic.Dictionary<string, int> GetItemPlayCounts(int minPlayDurationSeconds)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_playbackDbConn);

            var results = Dapper.SqlMapper.Query(connection, @"
                SELECT ItemId, COUNT(*) as PlayCount
                FROM PlaybackActivity
                WHERE ItemId IS NOT NULL
                AND PlayDuration >= @MinPlayDuration
                GROUP BY ItemId
            ", new { MinPlayDuration = minPlayDurationSeconds });

            var dict = new System.Collections.Generic.Dictionary<string, int>();

            foreach (dynamic row in results)
            {
                string itemId = row.ItemId?.ToString() ?? "";
                if (!string.IsNullOrEmpty(itemId) && row.PlayCount != null)
                {
                    dict[itemId.Replace("-", "")] = (int)row.PlayCount;
                }
            }

            return dict;
        }

        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> GetItemViewers(int minPlayDurationSeconds)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_playbackDbConn);

            var results = Dapper.SqlMapper.Query(connection, @"
                SELECT ItemId, UserId
                FROM PlaybackActivity
                WHERE ItemId IS NOT NULL AND UserId IS NOT NULL
                AND PlayDuration >= @MinPlayDuration
            ", new { MinPlayDuration = minPlayDurationSeconds });

            var viewerMap = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>();

            foreach (dynamic row in results)
            {
                string itemId = row.ItemId?.ToString() ?? "";
                string userId = row.UserId?.ToString() ?? "";

                if (!string.IsNullOrEmpty(itemId) && !string.IsNullOrEmpty(userId))
                {
                    string cleanItemId = itemId.Replace("-", "");

                    if (!viewerMap.ContainsKey(cleanItemId))
                    {
                        viewerMap[cleanItemId] = new System.Collections.Generic.HashSet<string>();
                    }

                    viewerMap[cleanItemId].Add(userId);
                }
            }

            return viewerMap;
        }

        public System.Collections.Generic.Dictionary<string, System.DateTime> GetItemLastPlayedDates(int minPlayDurationSeconds)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_playbackDbConn);

            // Without this filter a 10-second check bumps "Last Breath" and shields the
            // item from every time-based rule.
            var results = Dapper.SqlMapper.Query(connection, @"
                SELECT ItemId, MAX(DateCreated) as LastPlayedDate
                FROM PlaybackActivity
                WHERE ItemId IS NOT NULL
                AND PlayDuration >= @MinPlayDuration
                GROUP BY ItemId
            ", new { MinPlayDuration = minPlayDurationSeconds });

            var dict = new System.Collections.Generic.Dictionary<string, System.DateTime>();

            foreach (dynamic row in results)
            {
                string itemId = row.ItemId?.ToString() ?? "";
                if (!string.IsNullOrEmpty(itemId) && row.LastPlayedDate != null)
                {
                    if (System.DateTime.TryParse(row.LastPlayedDate.ToString(), out System.DateTime parsedDate))
                    {
                        dict[itemId.Replace("-", "")] = parsedDate;
                    }
                }
            }

            return dict;
        }

        public System.Collections.Generic.Dictionary<string, long> GetItemPlayDurations(int minPlayDurationSeconds)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_playbackDbConn);

            var results = Dapper.SqlMapper.Query(connection, @"
                SELECT ItemId, SUM(PlayDuration) as TotalDuration
                FROM PlaybackActivity
                WHERE ItemId IS NOT NULL
                AND PlayDuration >= @MinPlayDuration
                GROUP BY ItemId
            ", new { MinPlayDuration = minPlayDurationSeconds });

            var dict = new System.Collections.Generic.Dictionary<string, long>();

            foreach (dynamic row in results)
            {
                string itemId = row.ItemId?.ToString() ?? "";
                if (!string.IsNullOrEmpty(itemId) && row.TotalDuration != null)
                {
                    dict[itemId.Replace("-", "")] = (long)row.TotalDuration;
                }
            }

            return dict;
        }

        /// <summary>
        /// Oldest activity Playback Reporting knows about, or null on an empty database.
        /// Everything before this date is invisible to us: an item added earlier reads as
        /// zero-play whether it was watched or not, which is why the Morgue grace period is
        /// clamped to this coverage.
        /// </summary>
        public System.DateTime? GetHistoryFloorDate()
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_playbackDbConn);

            var floor = Dapper.SqlMapper.QuerySingleOrDefault<string?>(
                connection, "SELECT MIN(DateCreated) FROM PlaybackActivity");

            if (string.IsNullOrWhiteSpace(floor))
            {
                return null;
            }

            return System.DateTime.TryParse(
                floor,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : null;
        }

        /// <summary>
        /// Guestbook rows, newest first, capped at <paramref name="rowLimit"/>. Returns
        /// whether the cap truncated the result so the caller can say so rather than
        /// presenting a partial window as complete.
        /// </summary>
        public (List<dynamic> Rows, bool Truncated) GetRawPlaybackActivity(
            System.DateTime startDate, System.DateTime endDate, int rowLimit)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_playbackDbConn);

            // One row over the cap: if it comes back, there was more to show.
            var rows = Dapper.SqlMapper.Query(connection, @"
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
        /// Playback Reporting stores naive UTC strings, so a local-time bound would shift
        /// the window by the server's offset.
        /// </summary>
        private static string FormatSqliteUtc(System.DateTime value)
        {
            var utc = value.Kind switch
            {
                System.DateTimeKind.Utc => value,
                System.DateTimeKind.Local => value.ToUniversalTime(),
                _ => System.DateTime.SpecifyKind(value, System.DateTimeKind.Utc)
            };

            return utc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
