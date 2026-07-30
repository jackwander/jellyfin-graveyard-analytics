// Drives the real Repository out of the built plugin assembly against a real SQLite file.
//
// Two Phase 5 claims need runtime evidence and cannot be read off the source:
//
//   * Item 19 replaced Dapper's `dynamic` with typed row DTOs. Mapping is decided at
//     runtime from SQLite's storage classes, so "it compiles" says nothing: a column whose
//     declared type does not match what is stored is exactly where a typed mapper fails and
//     `dynamic` did not.
//   * Finding 3's read-only fix. `Mode=ReadOnly` was set and then overwritten, so every read
//     held a writable handle and a missing database was silently created. The fix has its own
//     risk — a read-only connection to a WAL database cannot create the -shm file — which is
//     the last check here.
//
// The table DDL below is a *replica* of Playback Reporting's, not the real thing (that plugin
// is not installed here), so it can drift. Column declarations are the part that matters:
// DateCreated is declared DATETIME (NUMERIC affinity) while holding a naive UTC string, which
// is the specific mismatch the string-typed DTOs exist for.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

var results = new List<(string Name, bool Ok, string Detail)>();
void Check(string name, bool ok, string detail = "") => results.Add((name, ok, detail));

// ---------------------------------------------------------------- load the built assembly
var dllEnv = Environment.GetEnvironmentVariable("GRAVEYARD_DLL");
var dllPath = dllEnv ?? Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory,
    "../../../../../../../JellyfinGraveyardAnalytics/bin/Release/net9.0/JellyfinGraveyardAnalyticsPlugin.dll"));

if (!File.Exists(dllPath))
{
    Console.Error.WriteLine($"Plugin assembly not found at {dllPath}. Build it first (dotnet publish -c Release) or set GRAVEYARD_DLL.");
    return 2;
}

var pluginAsm = Assembly.LoadFrom(dllPath);
var repoType = pluginAsm.GetType("JellyfinGraveyardAnalytics.Database.Repository")
    ?? throw new InvalidOperationException("Repository type not found in the plugin assembly.");

var root = Path.Combine(Path.GetTempPath(), "graveyard-repo-harness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    // ------------------------------------------------------- A. missing database, untouched
    // Finding 3's other half: a writable open *creates* the file, which reads back as
    // "Playback Reporting is installed and has no activity". Read-only must not.
    var emptyDataPath = Path.Combine(root, "no-db");
    Directory.CreateDirectory(emptyDataPath);
    var absent = New(emptyDataPath);
    var absentDbPath = (string)Get(absent, "PlaybackDbPath")!;

    Check("A1 missing database reports as missing", !(bool)Get(absent, "PlaybackDatabaseExists")!);

    var threw = false;
    try { Call(absent, "GetItemPlayCounts", 120); }
    catch (TargetInvocationException ex) when (ex.InnerException is SqliteException) { threw = true; }
    Check("A2 querying a missing database throws instead of inventing one", threw);
    Check("A3 no database file was created by the attempt", !File.Exists(absentDbPath),
        File.Exists(absentDbPath) ? $"created {new FileInfo(absentDbPath).Length} bytes" : "");

    // ------------------------------------------------------------- the populated database
    var dataPath = Path.Combine(root, "data");
    Directory.CreateDirectory(dataPath);
    var dbPath = Path.Combine(dataPath, "playback_reporting.db");

    // Two items, one of them dash-formatted, plus a session under the play threshold and one
    // by a second user, so every aggregate has something to get wrong.
    const string ItemA = "11111111222233334444555555555555";          // stored without dashes
    const string ItemBDashed = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"; // stored with dashes
    const string ItemBFlat = "aaaaaaaabbbbccccddddeeeeeeeeeeee";
    const string UserOne = "user-one";
    const string UserTwo = "user-two";

    Seed(dbPath, journalMode: "delete", rows: new[]
    {
        // DateCreated,           UserId,  ItemId,      ItemName,  PlayDuration
        ("2026-01-02 10:00:00", UserOne, ItemA, "Cold Open", 600L),
        ("2026-03-04 11:30:00", UserTwo, ItemA, "Cold Open", 400L),
        ("2026-02-01 09:00:00", UserOne, ItemA, "Cold Open", 30L),      // under the threshold
        ("2026-05-06 20:15:00", UserOne, ItemBDashed, "The Thing", 900L),
    });

    var repo = New(dataPath);
    Check("B0 database is found", (bool)Get(repo, "PlaybackDatabaseExists")!);

    // ------------------------------------------------- B. typed DTOs map the four aggregates
    var counts = (Dictionary<string, int>)Call(repo, "GetItemPlayCounts", 120)!;
    Check("B1 play counts key on the dash-stripped id",
        counts.Count == 2 && counts[ItemA] == 2 && counts[ItemBFlat] == 1,
        Dump(counts));

    var viewers = (Dictionary<string, HashSet<string>>)Call(repo, "GetItemViewers", 120)!;
    Check("B2 viewers are distinct users, and the sub-threshold session adds none",
        viewers[ItemA].SetEquals(new[] { UserOne, UserTwo }) && viewers[ItemBFlat].Count == 1,
        Dump(viewers.ToDictionary(kv => kv.Key, kv => string.Join(",", kv.Value.OrderBy(v => v)))));

    var lastPlayed = (Dictionary<string, DateTime>)Call(repo, "GetItemLastPlayedDates", 120)!;
    Check("B3 last-played is the newest qualifying session, parsed from the DATETIME column",
        lastPlayed[ItemA] == new DateTime(2026, 3, 4, 11, 30, 0),
        Dump(lastPlayed));

    var durations = (Dictionary<string, long>)Call(repo, "GetItemPlayDurations", 120)!;
    Check("B4 durations sum only qualifying sessions (600+400, not +30)",
        durations[ItemA] == 1000 && durations[ItemBFlat] == 900,
        Dump(durations));

    // D2's floor is a query parameter, not a constant: the 30s session appears only below it.
    var countsNoFloor = (Dictionary<string, int>)Call(repo, "GetItemPlayCounts", 1)!;
    Check("B5 the play threshold is what excluded that session (3 plays at a 1s floor)",
        countsNoFloor[ItemA] == 3, Dump(countsNoFloor));

    var floor = (DateTime?)Call(repo, "GetHistoryFloorDate");
    Check("B6 history floor is the oldest row of all, threshold or not, as UTC",
        floor == new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc) && floor.Value.Kind == DateTimeKind.Utc,
        floor?.ToString("o") ?? "null");

    // ------------------------------------------- C. the Guestbook row DTO and its truncation
    var window = Call(repo, "GetRawPlaybackActivity",
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc),
        5000)!;

    var rows = ((IEnumerable)window.GetType().GetField("Item1")!.GetValue(window)!).Cast<object>().ToList();
    var truncated = (bool)window.GetType().GetField("Item2")!.GetValue(window)!;

    Check("C1 all four rows come back, newest first, untruncated",
        rows.Count == 4 && !truncated
        && (string?)Get(rows[0], "DateCreated") == "2026-05-06 20:15:00",
        $"{rows.Count} rows, truncated={truncated}, first={Get(rows[0], "DateCreated")}");

    Check("C2 text columns map to strings and PlayDuration to a long",
        (string?)Get(rows[0], "ItemName") == "The Thing"
        && (string?)Get(rows[0], "UserId") == UserOne
        && (string?)Get(rows[0], "ClientName") == "Jellyfin Web"
        && (string?)Get(rows[0], "DeviceName") == "Living Room TV"
        && (string?)Get(rows[0], "ItemType") == "Movie"
        && (string?)Get(rows[0], "PlaybackMethod") == "DirectPlay"
        && (long?)Get(rows[0], "PlayDuration") == 900L,
        string.Join(" | ", new[] { "ItemName", "UserId", "ClientName", "DeviceName", "ItemType", "PlaybackMethod", "PlayDuration" }
            .Select(p => $"{p}={Get(rows[0], p)}")));

    // The cap fetches one row past itself to detect that there was more.
    var capped = Call(repo, "GetRawPlaybackActivity",
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc),
        2)!;
    var cappedRows = ((IEnumerable)capped.GetType().GetField("Item1")!.GetValue(capped)!).Cast<object>().ToList();
    Check("C3 a cap of 2 returns exactly 2 rows and reports truncation",
        cappedRows.Count == 2 && (bool)capped.GetType().GetField("Item2")!.GetValue(capped)!,
        $"{cappedRows.Count} rows");

    // The window is applied in UTC against naive-UTC storage.
    var narrow = Call(repo, "GetRawPlaybackActivity",
        new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 3, 31, 23, 59, 59, DateTimeKind.Utc),
        5000)!;
    var narrowRows = ((IEnumerable)narrow.GetType().GetField("Item1")!.GetValue(narrow)!).Cast<object>().ToList();
    Check("C4 the date window bounds the result", narrowRows.Count == 1, $"{narrowRows.Count} rows");

    // ------------------------------------------------------- D. the handle really is read-only
    var connString = (string)repoType
        .GetField("_playbackDbConn", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(repo)!;

    Check("D1 the connection string the repository actually uses says Mode=ReadOnly",
        connString.Contains("Mode=ReadOnly", StringComparison.OrdinalIgnoreCase), connString);

    var writeRefused = false;
    try
    {
        using var conn = new SqliteConnection(connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO PlaybackActivity (DateCreated, UserId, ItemId, ItemName, PlayDuration) VALUES ('2026-06-01 00:00:00','x','y','z',10)";
        cmd.ExecuteNonQuery();
    }
    catch (SqliteException)
    {
        writeRefused = true;
    }
    Check("D2 that string cannot write to another plugin's database", writeRefused);

    // ------------------------------------------------------------------ E. the WAL exposure
    // The risk the fix introduces: SQLite cannot create the -shm file for a read-only
    // connection, so a WAL database whose writer has gone away could be unreadable. Checked
    // rather than assumed, because Playback Reporting chooses the journal mode, not us.
    var walDataPath = Path.Combine(root, "wal");
    Directory.CreateDirectory(walDataPath);
    var walDbPath = Path.Combine(walDataPath, "playback_reporting.db");
    Seed(walDbPath, journalMode: "wal", rows: new[]
    {
        ("2026-04-01 12:00:00", UserOne, ItemA, "Cold Open", 500L),
    });
    SqliteConnection.ClearAllPools();

    var walRepo = New(walDataPath);
    var walSidecars = Directory.GetFiles(walDataPath).Select(Path.GetFileName).ToList();
    try
    {
        var walCounts = (Dictionary<string, int>)Call(walRepo, "GetItemPlayCounts", 120)!;
        Check("E1 a WAL database is still readable read-only after its writer closed",
            walCounts.Count == 1 && walCounts[ItemA] == 1, Dump(walCounts));
    }
    catch (TargetInvocationException ex)
    {
        Check("E1 a WAL database is still readable read-only after its writer closed", false,
            $"{ex.InnerException?.GetType().Name}: {ex.InnerException?.Message} (files: {string.Join(",", walSidecars)})");
    }

    // E2 is the case E1 cannot produce: a clean close checkpoints the WAL and deletes the
    // sidecars, so E1 read a plain database. Copying the files out from under a live writer
    // leaves an *un-checkpointed* -wal with no owning process — what a crashed or killed
    // server leaves behind. A read-only connection may not create the -shm it needs to read
    // that, so the newest sessions can be invisible or the open can fail outright.
    var crashDataPath = Path.Combine(root, "wal-crash");
    Directory.CreateDirectory(crashDataPath);
    SeedLeavingWal(Path.Combine(crashDataPath, "playback_reporting.db"), ItemA);

    var crashRepo = New(crashDataPath);
    var sidecars = Directory.GetFiles(crashDataPath).Select(Path.GetFileName).ToList();
    try
    {
        var crashCounts = (Dictionary<string, int>)Call(crashRepo, "GetItemPlayCounts", 120)!;
        Check("E2 a stale WAL left by a killed writer is still readable (2 sessions)",
            crashCounts.TryGetValue(ItemA, out var seen) && seen == 2,
            $"{Dump(crashCounts)} (files: {string.Join(",", sidecars)})");
    }
    catch (TargetInvocationException ex)
    {
        Check("E2 a stale WAL left by a killed writer is still readable (2 sessions)", false,
            $"{ex.InnerException?.GetType().Name}: {ex.InnerException?.Message} (files: {string.Join(",", sidecars)})");
    }

    // E3 narrows E2: a crash leaves both sidecars, so E2 never had to *create* the -shm. This
    // deletes it — and the interesting part is not that the read succeeds but *how*: the file
    // list is captured before and after, because a read-only connection that recreates the -shm
    // is writing into another plugin's directory.
    var shmless = Path.Combine(root, "wal-no-shm");
    Directory.CreateDirectory(shmless);
    var shmlessDb = Path.Combine(shmless, "playback_reporting.db");
    SeedLeavingWal(shmlessDb, ItemA);
    File.Delete(shmlessDb + "-shm");
    SqliteConnection.ClearAllPools();

    var before = Directory.GetFiles(shmless).Select(Path.GetFileName).OrderBy(f => f).ToList();
    try
    {
        var noShmCounts = (Dictionary<string, int>)Call(New(shmless), "GetItemPlayCounts", 120)!;
        var after = Directory.GetFiles(shmless).Select(Path.GetFileName).OrderBy(f => f).ToList();

        Check("E3 a stale WAL with no -shm reads, by creating the -shm the read needs",
            noShmCounts.GetValueOrDefault(ItemA) == 2
            && !before.Any(f => f!.EndsWith("-shm", StringComparison.Ordinal))
            && after.Any(f => f!.EndsWith("-shm", StringComparison.Ordinal)),
            $"{Dump(noShmCounts)} | before=[{string.Join(",", before)}] after=[{string.Join(",", after)}]");
    }
    catch (TargetInvocationException ex)
    {
        Check("E3 a stale WAL with no -shm reads, by creating the -shm the read needs", false,
            $"{ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
    }

    // E4 is the arrangement E3 was originally credited with covering, and the only one that
    // fails: the -shm is missing *and* cannot be created. A read-only connection has no way to
    // build the shared-memory index a WAL read needs, so the stale WAL is unreadable. Jellyfin
    // writes to its data path constantly, so this is not a state a real server is in — recorded
    // so the read-only claim is not stronger than the evidence.
    if (!OperatingSystem.IsWindows())
    {
        var locked = Path.Combine(root, "wal-locked-dir");
        Directory.CreateDirectory(locked);
        var lockedDb = Path.Combine(locked, "playback_reporting.db");
        SeedLeavingWal(lockedDb, ItemA);
        File.Delete(lockedDb + "-shm");
        SqliteConnection.ClearAllPools();

        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Call(New(locked), "GetItemPlayCounts", 120);
            Check("E4 with the -shm missing AND the directory read-only, the read fails", false,
                "expected SqliteException, got a successful read");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is SqliteException sqlite)
        {
            Check("E4 with the -shm missing AND the directory read-only, the read fails",
                true, sqlite.Message);
        }
        finally
        {
            // Restored or the temp cleanup cannot remove it.
            File.SetUnixFileMode(locked,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ------------------------------------------------ F. the state every fresh install is in
    // Playback Reporting installed but with nothing recorded yet. The most common state there
    // is, and the aggregates have to return empty rather than throw — an exception here would
    // read to the admin as "the plugin is broken" on a server that is merely new.
    var emptyTable = Path.Combine(root, "empty-table");
    Directory.CreateDirectory(emptyTable);
    Seed(Path.Combine(emptyTable, "playback_reporting.db"), journalMode: "delete",
        rows: Array.Empty<(string, string, string, string, long)>());

    var emptyRepo = New(emptyTable);
    var emptyWindow = Call(emptyRepo, "GetRawPlaybackActivity",
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc), 5000)!;

    Check("F1 an empty PlaybackActivity table yields empty aggregates, not an exception",
        ((Dictionary<string, int>)Call(emptyRepo, "GetItemPlayCounts", 120)!).Count == 0
        && ((Dictionary<string, HashSet<string>>)Call(emptyRepo, "GetItemViewers", 120)!).Count == 0
        && ((Dictionary<string, DateTime>)Call(emptyRepo, "GetItemLastPlayedDates", 120)!).Count == 0
        && ((Dictionary<string, long>)Call(emptyRepo, "GetItemPlayDurations", 120)!).Count == 0
        && ((IEnumerable)emptyWindow.GetType().GetField("Item1")!.GetValue(emptyWindow)!).Cast<object>().Count() == 0);

    Check("F2 an empty table has no history floor, which is what the Morgue gates on",
        Call(emptyRepo, "GetHistoryFloorDate") is null);

    // Dapper builds one deserializer per query from the *first* row's storage classes, so a
    // NULL-heavy first row followed by populated ones is where a typed mapper would break and
    // `dynamic` would not. Playback Reporting leaves these columns null often enough.
    var nulls = Path.Combine(root, "null-first-row");
    Directory.CreateDirectory(nulls);
    var nullsDb = Path.Combine(nulls, "playback_reporting.db");
    Seed(nullsDb, journalMode: "delete", rows: new[]
    {
        ("2026-01-01 10:00:00", UserOne, ItemA, "Cold Open", 800L),
    });
    SeedNullRow(nullsDb, "2026-02-01 10:00:00", UserOne, ItemA);   // newest, so it maps first

    var nullWindow = Call(New(nulls), "GetRawPlaybackActivity",
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc), 5000)!;
    var nullRows = ((IEnumerable)nullWindow.GetType().GetField("Item1")!.GetValue(nullWindow)!).Cast<object>().ToList();

    Check("F3 a NULL-heavy newest row maps, and does not poison the rows after it",
        nullRows.Count == 2
        && Get(nullRows[0], "ItemName") is null
        && Get(nullRows[0], "PlayDuration") is null
        && (string?)Get(nullRows[1], "ItemName") == "Cold Open"
        && (long?)Get(nullRows[1], "PlayDuration") == 800L,
        string.Join(" ;; ", nullRows.Select(r => $"name={Get(r, "ItemName") ?? "<null>"} dur={Get(r, "PlayDuration")?.ToString() ?? "<null>"}")));
}
finally
{
    SqliteConnection.ClearAllPools();
    try { Directory.Delete(root, true); } catch (IOException) { /* temp dir, best effort */ }
}

var failed = results.Count(r => !r.Ok);
foreach (var r in results)
{
    Console.WriteLine($"{(r.Ok ? "PASS" : "FAIL")}  {r.Name}{(r.Ok || r.Detail.Length == 0 ? "" : "   <-- " + r.Detail)}");
}

Console.WriteLine($"\n{results.Count - failed}/{results.Count} passed");
return failed == 0 ? 0 : 1;

// ------------------------------------------------------------------------------- plumbing

object New(string dataPath) => Activator.CreateInstance(repoType, new HarnessPaths(dataPath))!;

object? Call(object instance, string method, params object?[] args)
    => repoType.GetMethod(method)!.Invoke(instance, args);

object? Get(object instance, string property)
    => instance.GetType().GetProperty(property)!.GetValue(instance);

string Dump<TKey, TValue>(Dictionary<TKey, TValue> dict) where TKey : notnull
    => string.Join(", ", dict.Select(kv => $"{kv.Key}={kv.Value}"));

// Replica of Playback Reporting's table. Declared types are the point: DateCreated is
// DATETIME (NUMERIC affinity) but holds a naive UTC string, and PlayDuration is INT.
void Seed(string dbPath, string journalMode, (string Date, string User, string Item, string Name, long Duration)[] rows)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();

    using (var pragma = conn.CreateCommand())
    {
        pragma.CommandText = $"PRAGMA journal_mode={journalMode};";
        pragma.ExecuteScalar();
    }

    using (var create = conn.CreateCommand())
    {
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
    }

    foreach (var row in rows)
    {
        using var insert = conn.CreateCommand();
        insert.CommandText = @"
            INSERT INTO PlaybackActivity
                (DateCreated, UserId, ItemId, ItemType, ItemName, PlaybackMethod, ClientName, DeviceName, PlayDuration)
            VALUES ($date, $user, $item, 'Movie', $name, 'DirectPlay', 'Jellyfin Web', 'Living Room TV', $duration)";
        insert.Parameters.AddWithValue("$date", row.Date);
        insert.Parameters.AddWithValue("$user", row.User);
        insert.Parameters.AddWithValue("$item", row.Item);
        insert.Parameters.AddWithValue("$name", row.Name);
        insert.Parameters.AddWithValue("$duration", row.Duration);
        insert.ExecuteNonQuery();
    }

    conn.Close();
    SqliteConnection.ClearAllPools();
}

// One row with every optional column left NULL. Playback Reporting does record sessions with
// no client, device or duration.
void SeedNullRow(string dbPath, string date, string user, string item)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();

    using var insert = conn.CreateCommand();
    insert.CommandText = @"
        INSERT INTO PlaybackActivity
            (DateCreated, UserId, ItemId, ItemType, ItemName, PlaybackMethod, ClientName, DeviceName, PlayDuration)
        VALUES ($date, $user, $item, NULL, NULL, NULL, NULL, NULL, NULL)";
    insert.Parameters.AddWithValue("$date", date);
    insert.Parameters.AddWithValue("$user", user);
    insert.Parameters.AddWithValue("$item", item);
    insert.ExecuteNonQuery();

    conn.Close();
    SqliteConnection.ClearAllPools();
}

// Builds a WAL database and copies it out while the writer still holds it, so the copy keeps
// an un-checkpointed -wal and a -shm belonging to a process that is gone. A clean close would
// checkpoint and delete both, which is why this cannot be done by closing first.
void SeedLeavingWal(string dbPath, string itemId)
{
    var stagingDir = Path.Combine(Path.GetDirectoryName(dbPath)!, "staging");
    Directory.CreateDirectory(stagingDir);
    var stagingDb = Path.Combine(stagingDir, "playback_reporting.db");

    using (var conn = new SqliteConnection($"Data Source={stagingDb}"))
    {
        conn.Open();

        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=wal;";
            pragma.ExecuteScalar();
        }

        using (var create = conn.CreateCommand())
        {
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
        }

        foreach (var date in new[] { "2026-04-01 12:00:00", "2026-04-02 12:00:00" })
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = @"
                INSERT INTO PlaybackActivity
                    (DateCreated, UserId, ItemId, ItemType, ItemName, PlaybackMethod, ClientName, DeviceName, PlayDuration)
                VALUES ($date, 'user-one', $item, 'Movie', 'Cold Open', 'DirectPlay', 'Jellyfin Web', 'Living Room TV', 500)";
            insert.Parameters.AddWithValue("$date", date);
            insert.Parameters.AddWithValue("$item", itemId);
            insert.ExecuteNonQuery();
        }

        // Copied with the connection still open, so the -wal has not been folded in yet.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(stagingDb + suffix))
            {
                File.Copy(stagingDb + suffix, dbPath + suffix, overwrite: true);
            }
        }
    }

    SqliteConnection.ClearAllPools();
    Directory.Delete(stagingDir, true);
}

/// <summary>
/// Only <see cref="IApplicationPaths.DataPath"/> is read by the repository; the rest of the
/// interface has to exist to construct it.
/// </summary>
internal sealed class HarnessPaths(string dataPath) : IApplicationPaths
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
