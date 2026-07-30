using System.Globalization;
using System.Text.Json;
using GraveyardAnalytics.Tests.Support;
using JellyfinGraveyardAnalytics.Database;
using JellyfinGraveyardAnalytics.Models;

namespace GraveyardAnalytics.Tests;

/// <summary>
/// Finding 30. Every timestamp crossing the wire has to be <see cref="DateTimeKind.Utc"/>.
///
/// The bug was invisible server-side: the stored strings are naive UTC, so every
/// *comparison* was tick-for-tick correct, and it survived to be found by reading rather
/// than by anyone noticing. It showed on the wire. An <see cref="DateTimeKind.Unspecified"/>
/// value serializes with no <c>Z</c>, so the browser reads a UTC instant as its own local
/// time; and the dashboard colours Last Breath against a twelve-month cut, so an
/// offset-sized error can move a row across that verdict and render a long-dead title as a
/// live one.
///
/// A bare <c>DateTime.TryParse</c> is the trap, and it fails in two directions: without
/// styles it yields <c>Unspecified</c> for a zoneless string, and it yields <c>Local</c> for
/// one carrying an offset. The plugin had one of each — the local engine and the Tracearr
/// engine — so the same response field arrived differently anchored depending on which
/// engine filled it.
/// </summary>
public class StoredTimestampTests
{
    /// <summary>
    /// The stored format: SQLite's, naive, no zone marker. Parsed as the UTC it is.
    /// </summary>
    [Fact]
    public void AStoredStringParsesAsUtc()
    {
        Assert.True(Repository.TryParseStoredUtc("2026-03-04 11:30:00", out var parsed));

        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal(new DateTime(2026, 3, 4, 11, 30, 0, DateTimeKind.Utc), parsed);
    }

    /// <summary>
    /// The <c>Kind</c> is the load-bearing part, and it is the part a bare parse gets wrong.
    /// Asserted on its own so a regression names the cause rather than a wrong date.
    /// </summary>
    [Fact]
    public void TheKindIsUtcAndNotMerelyTheRightInstant()
    {
        Assert.True(Repository.TryParseStoredUtc("2026-03-04 11:30:00", out var throughHelper));

        // What the code used to do. On a machine west of UTC these two agree on every field
        // and differ only in Kind — which is exactly why this survived so long.
        Assert.True(DateTime.TryParse("2026-03-04 11:30:00", out var bareParse));

        Assert.Equal(DateTimeKind.Utc, throughHelper.Kind);
        Assert.Equal(DateTimeKind.Unspecified, bareParse.Kind);
    }

    /// <summary>
    /// A string that does carry an offset is converted, not reinterpreted. This is the other
    /// direction of the same bug: default styles honour the <c>Z</c> by producing a
    /// <c>Local</c> value, which is a correct instant with a <c>Kind</c> that serializes
    /// with the server's offset instead of as UTC.
    /// </summary>
    [Fact]
    public void AnOffsetBearingStringIsAdjustedToUtcRatherThanToLocal()
    {
        Assert.True(Repository.TryParseStoredUtc("2026-03-04T11:30:00+02:00", out var parsed));

        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal(new DateTime(2026, 3, 4, 9, 30, 0, DateTimeKind.Utc), parsed);
    }

    /// <summary>
    /// Parsed under the invariant culture, because the format belongs to SQLite and not to
    /// the server operator. Under a culture that reads dates day-first, an ambient parse of
    /// "2026-03-04" is 4 March by one reading and 3 April by another.
    /// </summary>
    [Fact]
    public void ParsingDoesNotDependOnTheAmbientCulture()
    {
        var original = Thread.CurrentThread.CurrentCulture;

        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.True(Repository.TryParseStoredUtc("2026-03-04 11:30:00", out var german));

            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.True(Repository.TryParseStoredUtc("2026-03-04 11:30:00", out var invariant));

            Assert.Equal(invariant, german);
            Assert.Equal(3, german.Month);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    [InlineData("0000-00-00 00:00:00")]
    public void AnUnreadableValueIsReportedRatherThanGuessed(string? value)
    {
        Assert.False(Repository.TryParseStoredUtc(value, out var parsed));
        Assert.Equal(default, parsed);
    }

    /// <summary>
    /// The end of the round trip: the value the browser actually receives. A <c>Z</c> here
    /// is the whole fix, and it is a property of the serializer plus the <c>Kind</c>, not of
    /// the parse alone — so it is asserted on the serialized JSON rather than on the object.
    /// </summary>
    [Fact]
    public void TheSerializedFormCarriesItsZ()
    {
        Assert.True(Repository.TryParseStoredUtc("2026-03-04 11:30:00", out var parsed));

        var json = JsonSerializer.Serialize(new LeastWatchedItem
        {
            MediaId = Guid.Empty.ToString(),
            Name = "A Film",
            Type = "Movie",
            LastPlayed = parsed
        });

        Assert.Contains("\"LastPlayed\":\"2026-03-04T11:30:00Z\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the pre-fix shape, recorded for contrast: no <c>Z</c>, which is what
    /// <c>new Date(item.LastPlayed)</c> in the browser reads as local time.
    /// </summary>
    [Fact]
    public void TheUnspecifiedFormIsWhatTheBrowserMisread()
    {
        Assert.True(DateTime.TryParse("2026-03-04 11:30:00", out var unspecified));

        var json = JsonSerializer.Serialize(new LeastWatchedItem
        {
            MediaId = Guid.Empty.ToString(),
            Name = "A Film",
            Type = "Movie",
            LastPlayed = unspecified
        });

        Assert.DoesNotContain("Z\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The full path, through the real repository over a real file: a stored string comes
    /// back anchored to UTC, and the history floor read forty lines away in the same class
    /// is anchored the same way. Those two drifting apart is the finding.
    /// </summary>
    [Fact]
    public void TheRepositoryAnchorsBothLastPlayedAndTheHistoryFloorToUtc()
    {
        using var db = new PlaybackDatabase();
        db.CreateEmpty();

        const string itemId = "abcdef0123456789abcdef0123456789";
        db.AddSession(new DateTime(2026, 3, 4, 11, 30, 0, DateTimeKind.Utc), "user-one", itemId, 3600);

        var repository = db.Repository();

        var lastPlayed = repository.GetItemLastPlayedDates(120)[itemId];
        Assert.Equal(DateTimeKind.Utc, lastPlayed.Kind);
        Assert.Equal(new DateTime(2026, 3, 4, 11, 30, 0, DateTimeKind.Utc), lastPlayed);

        var floor = repository.GetHistoryFloorDate();
        Assert.NotNull(floor);
        Assert.Equal(DateTimeKind.Utc, floor.Value.Kind);
        Assert.Equal(lastPlayed, floor.Value);
    }
}
