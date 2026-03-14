using Microsoft.Data.Sqlite;
using Dapper;

namespace JellyfinAnalyticsPlugin.Database
{
    public class DatabaseInitializer
    {
        public static void Initialize(string dbPath)
        {
            // Simplified connection string
            var connectionString = $"Data Source={dbPath};";
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            // Manually enable WAL mode
            connection.Execute("PRAGMA journal_mode=WAL;");

            var sql = @"
                CREATE TABLE IF NOT EXISTS playback_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id TEXT NOT NULL,
                    media_id TEXT NOT NULL,
                    media_title TEXT,
                    media_type TEXT,
                    start_time DATETIME NOT NULL,
                    end_time DATETIME,
                    play_duration_seconds INTEGER,
                    completion_percentage REAL,
                    device_name TEXT,
                    session_id TEXT
                );

                CREATE TABLE IF NOT EXISTS recommendations (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id TEXT NOT NULL,
                    recommended_media_id TEXT NOT NULL,
                    score REAL,
                    reason TEXT,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                );";

            connection.Execute(sql);
        }
    }
}
