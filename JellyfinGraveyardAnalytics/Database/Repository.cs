using Dapper;
using Microsoft.Data.Sqlite;
using MediaBrowser.Common.Configuration;
using JellyfinAnalyticsPlugin.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;

namespace JellyfinAnalyticsPlugin.Database
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

        public System.Collections.Generic.Dictionary<string, int> GetItemPlayCounts()
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_playbackDbConn);

            var results = Dapper.SqlMapper.Query(connection, @"
                SELECT ItemId, COUNT(*) as PlayCount
                FROM PlaybackActivity
                WHERE ItemId IS NOT NULL
                AND PlayDuration >= 120
                GROUP BY ItemId
            ");

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

        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> GetItemViewers()
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_playbackDbConn);

            var results = Dapper.SqlMapper.Query(connection, @"
                SELECT ItemId, UserId
                FROM PlaybackActivity
                WHERE ItemId IS NOT NULL AND UserId IS NOT NULL AND PlayDuration > 300
            ");

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

        public System.Collections.Generic.Dictionary<string, System.DateTime> GetItemLastPlayedDates()
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_playbackDbConn);

            var results = Dapper.SqlMapper.Query(connection, @"
                SELECT ItemId, MAX(DateCreated) as LastPlayedDate
                FROM PlaybackActivity
                WHERE ItemId IS NOT NULL
                GROUP BY ItemId
            ");

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

        public System.Collections.Generic.Dictionary<string, long> GetItemPlayDurations()
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_playbackDbConn);

            var results = Dapper.SqlMapper.Query(connection, @"
                SELECT ItemId, SUM(PlayDuration) as TotalDuration
                FROM PlaybackActivity
                WHERE ItemId IS NOT NULL
                GROUP BY ItemId
            ");

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

        public IEnumerable<dynamic> GetRawPlaybackActivity(System.DateTime startDate, System.DateTime endDate)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_playbackDbConn);

            return Dapper.SqlMapper.Query(connection, @"
                SELECT
                    DateCreated,
                    UserId,
                    ItemName,
                    ItemType,
                    ClientName,
                    DeviceName,
                    PlaybackMethod, -- FIXED THIS COLUMN NAME
                    PlayDuration
                FROM PlaybackActivity
                WHERE DateCreated >= @Start AND DateCreated <= @End
                ORDER BY DateCreated DESC",
                new { Start = startDate.ToString("yyyy-MM-dd HH:mm:ss"), End = endDate.ToString("yyyy-MM-dd HH:mm:ss") });
        }
    }
}
