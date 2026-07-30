using System.Reflection;
using GraveyardAnalytics.Tests.Support;
using JellyfinGraveyardAnalytics.Services;
using MediaBrowser.Controller.Library;

namespace GraveyardAnalytics.Tests;

/// <summary>
/// D1, as resolved on 2026-07-30: the grace clamp is gone and a floor gate replaces it.
///
/// <code>
/// item is in the Morgue  &lt;=&gt;  PlayCount == 0
///                         AND DateCreated &lt;= UtcNow - MorgueGraceDays
///                         AND DateCreated &gt;= historyFloor      // unless opted in
/// </code>
///
/// This list feeds Condemn and then Exorcise, which deletes files off the disk. A false
/// positive risks someone's favourite film; a false negative costs disk space. That
/// asymmetry is why the gate defaults to withholding, and why it is worth a test that
/// states each clause separately — the original clamp was self-defeating in a way that no
/// amount of reading the code revealed, and only worked out on a table of values.
/// </summary>
public class MorgueFloorGateTests : IDisposable
{
    private readonly PlaybackDatabase _db = new();
    private readonly TestLibrary.Configuration _config = new();

    private static readonly DateTime Now = DateTime.UtcNow;

    public MorgueFloorGateTests() => _db.CreateEmpty();

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private AnalyticsService ServiceFor(ILibraryManager library)
        => new(
            _db.Repository(),
            library,
            DispatchProxy.Create<IUserManager, UnusedUserManager>(),
            _config);

    /// <summary>
    /// Stats are handed in rather than read, which is what Phase 4 and 5 made possible:
    /// the Morgue's rule can now be exercised without a database behind it.
    /// </summary>
    private static PlaybackStats StatsWithFloor(DateTime? historyFloorUtc, params (string Id, int Plays)[] plays)
        => new()
        {
            PlayCounts = plays.ToDictionary(p => p.Id, p => p.Plays),
            ItemViewers = new Dictionary<string, HashSet<string>>(),
            LastPlayedDates = new Dictionary<string, DateTime>(),
            PlayDurations = new Dictionary<string, long>(),
            HistoryFloorUtc = historyFloorUtc
        };

    // ---- the age clause -------------------------------------------------------------

    /// <summary>
    /// Grace is applied as configured now. An item younger than it is not a Morgue
    /// candidate whatever the history says — "added last week and unwatched" is not neglect.
    /// </summary>
    [Fact]
    public void AnItemInsideTheGracePeriodIsNotOffered()
    {
        var recent = TestLibrary.MovieAdded(Now.AddDays(-10), "Added Last Week");
        var service = ServiceFor(TestLibrary.Containing(recent));

        var response = service.GetLeastWatchedItems(
            "Movie", null, 50, StatsWithFloor(Now.AddDays(-365)));

        Assert.Empty(response.Items);
    }

    [Fact]
    public void AnItemPastTheGracePeriodIsOffered()
    {
        var old = TestLibrary.MovieAdded(Now.AddDays(-200), "Long Forgotten");
        var service = ServiceFor(TestLibrary.Containing(old));

        var response = service.GetLeastWatchedItems(
            "Movie", null, 50, StatsWithFloor(Now.AddDays(-365)));

        Assert.Equal("Long Forgotten", Assert.Single(response.Items).Name);
    }

    /// <summary>
    /// The configured grace is honoured rather than clamped. Under the old rule a short
    /// history shrank this number, which *loosened* the age test — the specific inversion
    /// that got D1 reopened.
    /// </summary>
    [Fact]
    public void TheConfiguredGraceIsWhatApplies()
    {
        _config.Current.MorgueGraceDays = 365;

        var item = TestLibrary.MovieAdded(Now.AddDays(-200), "Two Hundred Days Old");
        var service = ServiceFor(TestLibrary.Containing(item));

        // Coverage is only 30 days. The old clamp would have cut grace to 30 and admitted
        // this item; the floor gate leaves grace at 365, so 200 days is still too young.
        var response = service.GetLeastWatchedItems(
            "Movie", null, 50, StatsWithFloor(Now.AddDays(-30)));

        Assert.Empty(response.Items);
        Assert.Equal(365, response.GraceDays);
    }

    // ---- the floor clause -----------------------------------------------------------

    /// <summary>
    /// The gate itself. An item added before history begins reads as zero-play whether it
    /// was loved or ignored, so it is withheld by default — and counted, so the banner can
    /// say what it is not showing.
    /// </summary>
    [Fact]
    public void AnItemPredatingHistoryIsWithheldButCounted()
    {
        var predatesHistory = TestLibrary.MovieAdded(Now.AddDays(-400), "Predates History");
        var service = ServiceFor(TestLibrary.Containing(predatesHistory));

        var response = service.GetLeastWatchedItems(
            "Movie", null, 50, StatsWithFloor(Now.AddDays(-100)));

        Assert.Empty(response.Items);
        Assert.Equal(1, response.UnverifiableCandidateCount);
        Assert.False(response.IncludingUnverifiable);
    }

    [Fact]
    public void TheOptInShowsTheWithheldItems()
    {
        var predatesHistory = TestLibrary.MovieAdded(Now.AddDays(-400), "Predates History");
        var service = ServiceFor(TestLibrary.Containing(predatesHistory));

        var response = service.GetLeastWatchedItems(
            "Movie", null, 50, StatsWithFloor(Now.AddDays(-100)), includeUnverifiable: true);

        Assert.Equal("Predates History", Assert.Single(response.Items).Name);
        Assert.True(response.IncludingUnverifiable);
        Assert.Equal(1, response.UnverifiableCandidateCount);
    }

    /// <summary>
    /// Both clauses at once, which is the arrangement that matters: old enough to be
    /// neglected, and young enough that the history could have seen it played.
    /// </summary>
    [Fact]
    public void OnlyTheItemSatisfyingBothClausesSurvives()
    {
        var tooYoung = TestLibrary.MovieAdded(Now.AddDays(-10), "Too Young");
        var justRight = TestLibrary.MovieAdded(Now.AddDays(-200), "Verifiably Dead");
        var predatesHistory = TestLibrary.MovieAdded(Now.AddDays(-500), "Unverifiable");

        var service = ServiceFor(TestLibrary.Containing(tooYoung, justRight, predatesHistory));

        var response = service.GetLeastWatchedItems(
            "Movie", null, 50, StatsWithFloor(Now.AddDays(-365)));

        Assert.Equal("Verifiably Dead", Assert.Single(response.Items).Name);
        Assert.Equal(1, response.UnverifiableCandidateCount);
    }

    // ---- the play clause ------------------------------------------------------------

    /// <summary>
    /// A played item is not in the Morgue however old it is. The play count arrives already
    /// filtered by D2's threshold, which is what <see cref="PlayThresholdTests"/> covers.
    /// </summary>
    [Fact]
    public void APlayedItemIsNotOfferedHoweverOld()
    {
        var id = Guid.NewGuid();
        var played = TestLibrary.MovieAdded(Now.AddDays(-300), "Watched Once", id: id.ToString());
        var service = ServiceFor(TestLibrary.Containing(played));

        var response = service.GetLeastWatchedItems(
            "Movie", null, 50, StatsWithFloor(Now.AddDays(-365), (id.ToString("N"), 1)));

        Assert.Empty(response.Items);
    }

    /// <summary>
    /// "Barely touched" widens the play test only. The age test and the floor gate still
    /// apply — the toggle is about how little watching counts as watched, not about
    /// suspending the rules that make the verdict trustworthy.
    /// </summary>
    [Fact]
    public void BarelyTouchedWidensThePlayTestAndNothingElse()
    {
        var barelyId = Guid.NewGuid();
        var barely = TestLibrary.MovieAdded(Now.AddDays(-300), "Barely Touched", id: barelyId.ToString());

        var youngId = Guid.NewGuid();
        var young = TestLibrary.MovieAdded(Now.AddDays(-5), "Young And Barely Touched", id: youngId.ToString());

        var service = ServiceFor(TestLibrary.Containing(barely, young));
        var stats = StatsWithFloor(
            Now.AddDays(-365),
            (barelyId.ToString("N"), AnalyticsService.BarelyTouchedPlayCeiling),
            (youngId.ToString("N"), AnalyticsService.BarelyTouchedPlayCeiling));

        var without = service.GetLeastWatchedItems("Movie", null, 50, stats);
        Assert.Empty(without.Items);

        var with = ServiceFor(TestLibrary.Containing(barely, young))
            .GetLeastWatchedItems("Movie", null, 50, stats, includeBarelyTouched: true);

        // The old one crosses the widened play test; the young one is still inside grace.
        Assert.Equal("Barely Touched", Assert.Single(with.Items).Name);
    }

    // ---- no history at all ----------------------------------------------------------

    /// <summary>
    /// With no history nothing is verifiable, so the default Morgue is empty rather than the
    /// entire library. Returning every title on a server that has just installed Playback
    /// Reporting, next to a button that deletes files, is the outcome D1 exists to prevent.
    /// </summary>
    [Fact]
    public void WithNoHistoryTheDefaultMorgueIsEmpty()
    {
        var old = TestLibrary.MovieAdded(Now.AddDays(-400), "Ancient");
        var service = ServiceFor(TestLibrary.Containing(old));

        var response = service.GetLeastWatchedItems("Movie", null, 50, StatsWithFloor(null));

        Assert.Empty(response.Items);
        Assert.Equal(0, response.CoverageDays);
        Assert.Null(response.HistoryFloorUtc);
    }

    /// <summary>
    /// And it says nothing about reclaimable space, rather than claiming zero. "0 B" reads
    /// as a measurement; null is the absence of one, and the two mean opposite things to
    /// someone deciding whether to delete.
    /// </summary>
    [Fact]
    public void WithNoHistoryThereIsNoReclaimableClaim()
    {
        var old = TestLibrary.MovieAdded(Now.AddDays(-400), "Ancient");
        var service = ServiceFor(TestLibrary.Containing(old));

        var response = service.GetLeastWatchedItems("Movie", null, 50, StatsWithFloor(null));

        Assert.Null(response.TotalWasted);
        Assert.Equal("0 B", response.TotalSize);
    }

    /// <summary>
    /// The opt-in is the only way to see anything with no history, and those rows carry
    /// their <c>DateAdded</c> so the dashboard can mark them individually rather than
    /// leaving the banner to qualify a table that looks authoritative.
    /// </summary>
    [Fact]
    public void WithNoHistoryTheOptInStillShowsTheLibrary()
    {
        var old = TestLibrary.MovieAdded(Now.AddDays(-400), "Ancient");
        var service = ServiceFor(TestLibrary.Containing(old));

        var response = service.GetLeastWatchedItems(
            "Movie", null, 50, StatsWithFloor(null), includeUnverifiable: true);

        var row = Assert.Single(response.Items);
        Assert.Equal("Ancient", row.Name);
        Assert.NotNull(row.DateAdded);
        Assert.True(response.IncludingUnverifiable);

        // Still no reclaimable claim: every row shown is one history cannot speak to.
        Assert.Null(response.TotalWasted);
    }

    // ---- what the banner is told ----------------------------------------------------

    /// <summary>
    /// Coverage is reported in whole days from the floor, so the banner can state how far
    /// back the verdict reaches instead of leaving the reader to assume it is complete.
    /// </summary>
    [Fact]
    public void CoverageIsReportedInDaysFromTheFloor()
    {
        var service = ServiceFor(TestLibrary.Containing(TestLibrary.MovieAdded(Now.AddDays(-200))));

        var response = service.GetLeastWatchedItems(
            "Movie", null, 50, StatsWithFloor(Now.AddDays(-90)));

        Assert.InRange(response.CoverageDays ?? -1, 89, 90);
    }

    /// <summary>
    /// Reclaimable space counts only rows that can be shown to be unwatched. Without the
    /// floor test this claimed "never played" about exactly the items D1 refuses to call
    /// unwatched — and it does so on the Chapel too, where the row action is Exorcise and
    /// there is no coverage banner to qualify it.
    /// </summary>
    [Fact]
    public void ReclaimableSpaceExcludesTheUnverifiableRows()
    {
        var verifiable = TestLibrary.MovieAdded(Now.AddDays(-200), "Verifiable", size: 1024);
        var unverifiable = TestLibrary.MovieAdded(Now.AddDays(-500), "Unverifiable", size: 1024L * 1024 * 1024);

        var service = ServiceFor(TestLibrary.Containing(verifiable, unverifiable));

        var response = service.GetLeastWatchedItems(
            "Movie", null, 50, StatsWithFloor(Now.AddDays(-365)), includeUnverifiable: true);

        // Both rows are listed, and the header size covers both...
        Assert.Equal(2, response.Items.Count);
        Assert.Equal("1 GB", response.TotalSize);

        // ...but only the verifiable kilobyte is claimed as reclaimable.
        Assert.Equal("1 KB", response.TotalWasted);
    }
}

/// <summary>
/// The Morgue never touches <see cref="IUserManager"/>; this exists so the service can be
/// constructed, and throws rather than returning defaults if that ever stops being true.
/// </summary>
internal class UnusedUserManager : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => throw new NotSupportedException(
            $"IUserManager.{targetMethod?.Name} was called; the Morgue tests assume it is unused.");
}
