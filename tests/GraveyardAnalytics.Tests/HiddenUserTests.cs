using System.Reflection;
using GraveyardAnalytics.Tests.Support;
using JellyfinGraveyardAnalytics.Configuration;
using JellyfinGraveyardAnalytics.Services;
using MediaBrowser.Controller.Library;

namespace GraveyardAnalytics.Tests;

/// <summary>
/// Issue #1: hidden users are left out of what the dashboard *shows*, and only that.
///
/// Two halves, and the second is the one that matters. The repository has to drop a hidden
/// user's sessions in SQL — every aggregate, and the Guestbook before its row cap. And the
/// Morgue has to keep counting them: hiding someone who watches one series on repeat must
/// not make that series read as unwatched and offer it up for deletion.
/// </summary>
public class HiddenUserTests : IDisposable
{
    private readonly PlaybackDatabase _db = new();

    private const string ItemId = "aaaaaaaabbbbccccddddeeeeeeeeeeee";

    private const string Hidden = "0123456789abcdef0123456789abcdef";
    private const string Visible = "fedcba9876543210fedcba9876543210";

    public HiddenUserTests() => _db.CreateEmpty();

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static readonly string[] HiddenOnly = { Hidden };

    /// <summary>
    /// The hidden user watched three times, stored dashed and uppercase — the comparison
    /// has to normalize the stored id, not just the configured one. A session with no user
    /// at all must survive: <c>NULL NOT IN (…)</c> is not true, so a naive filter drops it.
    /// </summary>
    [Fact]
    public void EveryAggregateDropsTheHiddenUserAndKeepsAnonymousSessions()
    {
        var dashedUpper = Guid.ParseExact(Hidden, "N").ToString("D").ToUpperInvariant();

        _db.AddSession(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), dashedUpper, ItemId, 1000);
        _db.AddSession(new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc), Hidden, ItemId, 1000);
        _db.AddSession(new DateTime(2026, 3, 9, 10, 0, 0, DateTimeKind.Utc), Hidden, ItemId, 1000);
        _db.AddSession(new DateTime(2026, 3, 3, 10, 0, 0, DateTimeKind.Utc), Visible, ItemId, 300);
        _db.AddSession(new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc), null, ItemId, 200);

        var repository = _db.Repository();

        // Unfiltered: everything, so the filter below is what changes the numbers.
        Assert.Equal(5, repository.GetItemPlayCounts(120)[ItemId]);
        Assert.Equal(5, repository.GetItemPlayCounts(120, Array.Empty<string>())[ItemId]);

        Assert.Equal(2, repository.GetItemPlayCounts(120, HiddenOnly)[ItemId]);
        Assert.Equal(500, repository.GetItemPlayDurations(120, HiddenOnly)[ItemId]);
        Assert.Equal(new[] { Visible }, repository.GetItemViewers(120, HiddenOnly)[ItemId]);
        Assert.Equal(
            new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc),
            repository.GetItemLastPlayedDates(120, HiddenOnly)[ItemId]);
    }

    /// <summary>
    /// The cap counts only rows the Guestbook will show. Filtered after the read, the hidden
    /// user's newer sessions would fill a one-row cap and the visible one would never appear.
    /// </summary>
    [Fact]
    public void TheGuestbookCapIsNotUsedUpByAHiddenUser()
    {
        _db.AddSession(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), Visible, ItemId, 300);
        _db.AddSession(new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc), Hidden, ItemId, 300);
        _db.AddSession(new DateTime(2026, 3, 3, 10, 0, 0, DateTimeKind.Utc), Hidden, ItemId, 300);

        var (rows, truncated) = _db.Repository().GetRawPlaybackActivity(
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            rowLimit: 1,
            HiddenOnly);

        Assert.Equal(Visible, Assert.Single(rows).UserId);
        Assert.False(truncated);
    }

    /// <summary>
    /// The safety half. One old film, watched only by the hidden user. The Morgue must not
    /// offer it — it was watched — while the Sanctuary, which only displays, drops it.
    /// </summary>
    [Fact]
    public void TheMorgueStillCountsAHiddenUsersPlaysWhileTheSanctuaryDoesNot()
    {
        var film = TestLibrary.MovieAdded(DateTime.UtcNow.AddDays(-400), "Watched On Repeat", id: ItemId);
        var config = new TestLibrary.Configuration();
        var service = new AnalyticsService(
            _db.Repository(),
            TestLibrary.Containing(film),
            DispatchProxy.Create<IUserManager, UnusedUserManager>(),
            config);

        var stats = new PlaybackStats
        {
            PlayCounts = new() { [ItemId] = 40 },
            ItemViewers = new() { [ItemId] = new HashSet<string> { Hidden } },
            LastPlayedDates = new() { [ItemId] = DateTime.UtcNow.AddDays(-1) },
            PlayDurations = new() { [ItemId] = 40_000 },
            HistoryFloorUtc = DateTime.UtcNow.AddDays(-800),
            ExcludingHidden = new PlaybackStats { HistoryFloorUtc = DateTime.UtcNow.AddDays(-800) }
        };

        // Not in the Morgue even with barely-touched rows included: forty plays is watched.
        Assert.Empty(service.GetLeastWatchedItems("All", null, 50, stats, includeBarelyTouched: true).Items);

        Assert.Empty(service.GetLivingItems("All", null, 50, stats).Items);

        // And without anyone hidden the Sanctuary shows it, so the line above is the filter.
        var unfiltered = new PlaybackStats
        {
            PlayCounts = stats.PlayCounts,
            ItemViewers = stats.ItemViewers,
            LastPlayedDates = stats.LastPlayedDates,
            PlayDurations = stats.PlayDurations
        };
        Assert.Same(unfiltered, unfiltered.Displayed);
        Assert.Equal(40, Assert.Single(service.GetLivingItems("All", null, 50, unfiltered).Items).PlayCount);
    }

    /// <summary>
    /// The UI posts whatever the client hands it and the XML is hand-editable; every consumer
    /// compares against the dashless lowercase form, so that is what has to be stored.
    /// </summary>
    [Fact]
    public void HiddenUserIdsAreNormalizedOnWrite()
    {
        var config = new PluginConfiguration
        {
            HiddenUserIds = new[]
            {
                Guid.ParseExact(Hidden, "N").ToString("D").ToUpperInvariant(),
                Hidden,
                "not a guid",
                string.Empty
            }
        };

        Assert.Equal(HiddenOnly, config.HiddenUserIds);

        config.HiddenUserIds = null!;
        Assert.Empty(config.HiddenUserIds);

        Assert.Empty(new PluginConfiguration().HiddenUserIds);
    }
}
