using GraveyardAnalytics.Tests.Support;

namespace GraveyardAnalytics.Tests;

/// <summary>
/// D2's play threshold, over a real SQLite file.
///
/// The threshold is the single rule that decides whether a session counts as a play, and
/// it has to be applied identically by all four aggregates — otherwise an item can be
/// zero-play by one measure and watched by another, and the Morgue and the Sanctuary
/// disagree about the same title. The aggregates are separate SQL statements, so "they all
/// apply it" is not something reading one of them can establish.
///
/// It also has to be a query *parameter* rather than a filter applied afterwards: the whole
/// point is that the database does not return the two-second false starts.
/// </summary>
public class PlayThresholdTests : IDisposable
{
    private readonly PlaybackDatabase _db = new();

    // One item, four sessions: two long, two below any sane floor. At a 1-second threshold
    // all four count; at 120 seconds only the two long ones do.
    private const string ItemId = "aaaaaaaabbbbccccddddeeeeeeeeeeee";

    public PlayThresholdTests()
    {
        _db.CreateEmpty();

        _db.AddSession(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), "user-one", ItemId, 3600);
        _db.AddSession(new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc), "user-two", ItemId, 600);

        // The false starts. Same item, and one of them is by a *third* user, so a threshold
        // that fails to apply inflates the unique-viewer count as well as the play count.
        _db.AddSession(new DateTime(2026, 3, 3, 10, 0, 0, DateTimeKind.Utc), "user-three", ItemId, 2);
        _db.AddSession(new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc), "user-one", ItemId, 45);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void PlayCountsCountOnlySessionsPastTheThreshold()
    {
        var repository = _db.Repository();

        Assert.Equal(4, repository.GetItemPlayCounts(1)[ItemId]);
        Assert.Equal(2, repository.GetItemPlayCounts(120)[ItemId]);
    }

    /// <summary>
    /// The false start by user-three is the reason this matters: without the threshold the
    /// Sanctuary reports three people reached by a title one person actually watched.
    /// </summary>
    [Fact]
    public void UniqueViewersCountOnlyViewersPastTheThreshold()
    {
        var repository = _db.Repository();

        Assert.Equal(3, repository.GetItemViewers(1)[ItemId].Count);
        Assert.Equal(2, repository.GetItemViewers(120)[ItemId].Count);
    }

    /// <summary>
    /// Last Breath is a verdict as much as a date — the dashboard colours it against a
    /// twelve-month cut. A 45-second false start four days after the real viewing must not
    /// be what revives the row.
    /// </summary>
    [Fact]
    public void LastPlayedIgnoresSessionsBelowTheThreshold()
    {
        var repository = _db.Repository();

        Assert.Equal(
            new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc),
            repository.GetItemLastPlayedDates(1)[ItemId]);

        Assert.Equal(
            new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc),
            repository.GetItemLastPlayedDates(120)[ItemId]);
    }

    /// <summary>
    /// Time Played sums the same filtered set. 3600 + 600 + 2 + 45 unfiltered; 4200 filtered.
    /// </summary>
    [Fact]
    public void PlayDurationsSumOnlySessionsPastTheThreshold()
    {
        var repository = _db.Repository();

        Assert.Equal(4247, repository.GetItemPlayDurations(1)[ItemId]);
        Assert.Equal(4200, repository.GetItemPlayDurations(120)[ItemId]);
    }

    /// <summary>
    /// The boundary. D2 says a session counts once it *reaches* the threshold, so a session
    /// of exactly the threshold length is a play. An off-by-one here is invisible in normal
    /// use and changes which items the Morgue offers up for deletion.
    /// </summary>
    [Fact]
    public void ASessionOfExactlyTheThresholdCounts()
    {
        const string exact = "ffffffffffffffffffffffffffffffff";
        _db.AddSession(new DateTime(2026, 3, 5, 10, 0, 0, DateTimeKind.Utc), "user-four", exact, 120);

        var repository = _db.Repository();

        Assert.Equal(1, repository.GetItemPlayCounts(120)[exact]);
        Assert.False(repository.GetItemPlayCounts(121).ContainsKey(exact));
    }

    /// <summary>
    /// An item whose every session is below the threshold must be *absent* from the
    /// aggregates, not present with a zero. The Morgue reads a missing key as zero plays,
    /// and a row that says "0" is the row it offers for deletion — so this is the shape the
    /// filter has to produce, not merely the count.
    /// </summary>
    [Fact]
    public void AnItemWithOnlyFalseStartsDropsOutEntirely()
    {
        const string neverReallyWatched = "11111111222233334444555555555555";
        _db.AddSession(new DateTime(2026, 3, 6, 10, 0, 0, DateTimeKind.Utc), "user-five", neverReallyWatched, 5);
        _db.AddSession(new DateTime(2026, 3, 7, 10, 0, 0, DateTimeKind.Utc), "user-six", neverReallyWatched, 9);

        var repository = _db.Repository();

        Assert.True(repository.GetItemPlayCounts(1).ContainsKey(neverReallyWatched));
        Assert.False(repository.GetItemPlayCounts(120).ContainsKey(neverReallyWatched));
        Assert.False(repository.GetItemViewers(120).ContainsKey(neverReallyWatched));
        Assert.False(repository.GetItemPlayDurations(120).ContainsKey(neverReallyWatched));
        Assert.False(repository.GetItemLastPlayedDates(120).ContainsKey(neverReallyWatched));
    }

    /// <summary>
    /// A fresh Playback Reporting install: the table exists and is empty. The most common
    /// state there is, and the one a typed row mapper is likeliest to throw on.
    /// </summary>
    [Fact]
    public void AnEmptyTableYieldsEmptyAggregatesRatherThanThrowing()
    {
        using var empty = new PlaybackDatabase();
        empty.CreateEmpty();

        var repository = empty.Repository();

        Assert.Empty(repository.GetItemPlayCounts(120));
        Assert.Empty(repository.GetItemViewers(120));
        Assert.Empty(repository.GetItemPlayDurations(120));
        Assert.Empty(repository.GetItemLastPlayedDates(120));
        Assert.Null(repository.GetHistoryFloorDate());
    }

    /// <summary>
    /// Finding 3. A read-only handle must not invent the file it cannot find — a writable
    /// open creates an empty database, which then reads as "Playback Reporting is installed
    /// and nobody has watched anything", the single most misleading state this plugin has.
    /// </summary>
    [Fact]
    public void AMissingDatabaseIsReportedMissingAndNotCreated()
    {
        using var missing = new PlaybackDatabase();

        var repository = missing.Repository();

        Assert.False(repository.PlaybackDatabaseExists);
        Assert.False(File.Exists(missing.DatabasePath));

        Assert.ThrowsAny<Exception>(() => repository.GetItemPlayCounts(120));

        // The point of the assertion: the failed read left no file behind.
        Assert.False(File.Exists(missing.DatabasePath));
    }
}
