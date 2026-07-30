using System.Globalization;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace GraveyardAnalytics.Tests.Support;

/// <summary>
/// A throwaway Playback Reporting database in a temp directory, plus the
/// <see cref="IApplicationPaths"/> the <see cref="JellyfinGraveyardAnalytics.Database.Repository"/>
/// needs to find it.
/// </summary>
/// <remarks>
/// The DDL is a replica of Playback Reporting's, because that plugin is not installed
/// here. The declared column types are the part that matters and they are copied
/// deliberately: <c>DateCreated DATETIME</c> holds a naive UTC *string*, and
/// <c>PlayDuration INT</c> holds seconds. Both of those are assumptions the repository's
/// typed row DTOs are built on, so a test that seeds `DateTime` objects or milliseconds
/// would pass against a repository that could not read a real database.
/// </remarks>
public sealed class PlaybackDatabase : IDisposable
{
    private readonly string _root;

    public PlaybackDatabase()
    {
        _root = Path.Combine(Path.GetTempPath(), "graveyard-tests-" + Guid.NewGuid().ToString("N"));

        // The repository looks under <DataPath>/plugins/... — mirror the layout rather than
        // pointing straight at a file, so the path construction is exercised too.
        DataPath = Path.Combine(_root, "data");
        Directory.CreateDirectory(DataPath);

        Paths = new TestApplicationPaths(DataPath);
        DatabasePath = ResolveDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
    }

    public string DataPath { get; }

    public string DatabasePath { get; }

    public IApplicationPaths Paths { get; }

    /// <summary>
    /// Where the repository expects the database, asked of the repository rather than
    /// rebuilt here, so the fixture cannot drift from the path the shipped code uses.
    /// </summary>
    private string ResolveDatabasePath() => Repository().PlaybackDbPath;

    public JellyfinGraveyardAnalytics.Database.Repository Repository()
        => new(Paths);

    /// <summary>Creates the table but inserts nothing — a fresh Playback Reporting install.</summary>
    public void CreateEmpty()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();

        using var create = connection.CreateCommand();
        create.CommandText = @"
            CREATE TABLE IF NOT EXISTS PlaybackActivity (
                DateCreated DATETIME NOT NULL,
                UserId TEXT,
                ItemId TEXT,
                ItemType VARCHAR(50),
                ItemName TEXT,
                PlaybackMethod VARCHAR(50),
                ClientName TEXT,
                DeviceName TEXT,
                PlayDuration INT,
                PRIMARY KEY (DateCreated, UserId, ItemId)
            )";
        create.ExecuteNonQuery();

        connection.Close();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// A real database file that carries no <c>PlaybackActivity</c> table — Playback Reporting
    /// having created its database before its schema. The file test alone cannot tell this
    /// apart from a working install.
    /// </summary>
    public void CreateWithoutPlaybackTable()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();

        // Something has to be created, or SQLite leaves a zero-byte file that is not yet a
        // database — a different failure, and not the one under test.
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE IF NOT EXISTS SomeOtherTable (Id INTEGER PRIMARY KEY)";
        create.ExecuteNonQuery();

        connection.Close();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// One session. <paramref name="playDurationSeconds"/> is seconds, matching the column.
    /// </summary>
    public void AddSession(
        DateTime utc,
        string userId,
        string itemId,
        long playDurationSeconds,
        string itemName = "A Film",
        string itemType = "Movie",
        string playbackMethod = "DirectPlay")
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();

        using var insert = connection.CreateCommand();
        insert.CommandText = @"
            INSERT INTO PlaybackActivity
                (DateCreated, UserId, ItemId, ItemType, ItemName, PlaybackMethod, ClientName, DeviceName, PlayDuration)
            VALUES ($date, $user, $item, $type, $name, $method, 'Jellyfin Web', 'Living Room TV', $duration)";

        // The stored format: naive UTC, no zone marker. Writing an ISO string with a Z here
        // would make the repository's parse look correct when it is not.
        insert.Parameters.AddWithValue("$date", utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$user", userId);
        insert.Parameters.AddWithValue("$item", itemId);
        insert.Parameters.AddWithValue("$type", itemType);
        insert.Parameters.AddWithValue("$name", itemName);
        insert.Parameters.AddWithValue("$method", playbackMethod);
        insert.Parameters.AddWithValue("$duration", playDurationSeconds);
        insert.ExecuteNonQuery();

        connection.Close();
        SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives one test run is not worth failing a test over.
        }
    }
}

/// <summary>
/// Only <see cref="IApplicationPaths.DataPath"/> is read by the repository; the rest of the
/// interface exists so the type can be constructed.
/// </summary>
internal sealed class TestApplicationPaths(string dataPath) : IApplicationPaths
{
    public string DataPath { get; } = dataPath;

    public string ProgramDataPath => DataPath;

    public string WebPath => DataPath;

    public string ProgramSystemPath => DataPath;

    public string ImageCachePath => DataPath;

    public string PluginsPath => DataPath;

    public string PluginConfigurationsPath => DataPath;

    public string LogDirectoryPath => DataPath;

    public string ConfigurationDirectoryPath => DataPath;

    public string SystemConfigurationFilePath => Path.Combine(DataPath, "system.xml");

    public string CachePath => DataPath;

    public string TempDirectory => DataPath;

    public string VirtualDataPath => DataPath;

    public string TrickplayPath => DataPath;

    public string BackupPath => DataPath;

    public void MakeSanityCheckOrThrow()
    {
    }

    public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
    {
    }
}
