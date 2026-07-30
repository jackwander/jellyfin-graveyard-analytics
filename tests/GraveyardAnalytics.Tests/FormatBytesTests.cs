using JellyfinGraveyardAnalytics.Services;

namespace GraveyardAnalytics.Tests;

/// <summary>
/// Finding 6. The pre-fix formatter divided the running <c>long</c> inside the loop, which
/// truncated on every step, and indexed its suffix array with a counter it had already
/// incremented — so the first value large enough to need "PB" walked off the end of the
/// array and threw <see cref="IndexOutOfRangeException"/> instead of rendering a number.
///
/// This is the whole reason a suite exists at all: the function is pure, takes one
/// argument, and the bug was reachable from any library with a petabyte in it. Nothing but
/// a test was ever going to find it, because the sizes that trigger it do not occur on a
/// developer's machine.
/// </summary>
public class FormatBytesTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1L, "1 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(1073741824L, "1 GB")]
    [InlineData(1099511627776L, "1 TB")]
    public void FormatsTheOrdinaryRange(long bytes, string expected)
        => Assert.Equal(expected, AnalyticsService.FormatBytes(bytes));

    /// <summary>
    /// The values that threw. The old suffix array stopped at TB, so 1 PB was the first
    /// size to index past its end.
    ///
    /// Checked non-vacuous: shrinking the array back to <c>{B, KB, MB, GB, TB}</c> fails
    /// these three and <see cref="HandlesLongMinValueWithoutOverflowing"/> and nothing else.
    /// </summary>
    [Theory]
    [InlineData(1125899906842624L, "1 PB")]
    [InlineData(1152921504606846976L, "1 EB")]
    [InlineData(long.MaxValue, "8 EB")]
    public void FormatsPastTheOldArrayBound(long bytes, string expected)
        => Assert.Equal(expected, AnalyticsService.FormatBytes(bytes));

    /// <summary>
    /// The loop's <c>order &lt; suffixes.Length - 1</c> guard, stated so a future edit to the
    /// suffix array cannot reintroduce the throw.
    /// </summary>
    /// <remarks>
    /// Weaker than it looks, and worth saying so. With seven suffixes the guard is
    /// unreachable for any <see cref="long"/> — <c>long.MaxValue</c> divides down to 8
    /// before the loop can reach index 7, so removing the guard entirely still passes every
    /// test here. What actually fixed finding 6 was extending the array; the guard is what
    /// keeps a *shortened* one from throwing rather than merely mislabelling. This is a
    /// regression net for that edit, not evidence the guard does anything today.
    /// </remarks>
    [Fact]
    public void NeverIndexesPastTheLastSuffix()
    {
        var exception = Record.Exception(() => AnalyticsService.FormatBytes(long.MaxValue));
        Assert.Null(exception);
    }

    /// <summary>
    /// Precision is what the mid-loop integer division cost. 1.5 KB came out as "1 KB"
    /// before, and the same truncation applied at every order, so a 1.9 TB library read as
    /// 1 TB on the header that the whole point of the plugin is to make you look at.
    /// </summary>
    [Fact]
    public void KeepsTheFractionalPart()
    {
        Assert.Equal("1.5 KB", AnalyticsService.FormatBytes(1536));
        Assert.Equal("1.5 GB", AnalyticsService.FormatBytes(1610612736));
        Assert.Equal("2.5 MB", AnalyticsService.FormatBytes(2621440));
    }

    /// <summary>
    /// Two decimal places, not more: the format is "0.##", and a size like 1.333 GB has to
    /// read as a size and not as a float dump.
    /// </summary>
    [Fact]
    public void RoundsToTwoDecimalPlaces()
        => Assert.Equal("1.33 GB", AnalyticsService.FormatBytes(1431655765));

    /// <summary>
    /// A negative size should never reach here — it would mean Jellyfin reported one — but
    /// the sign is preserved rather than swallowed, so if it ever does the number on screen
    /// is visibly wrong instead of quietly plausible.
    /// </summary>
    [Fact]
    public void PreservesTheSignOfANegativeSize()
    {
        Assert.Equal("-1 KB", AnalyticsService.FormatBytes(-1024));
        Assert.Equal("-1 B", AnalyticsService.FormatBytes(-1));
    }

    /// <summary>
    /// <c>long.MinValue</c> has no positive counterpart, so negating it in <c>long</c>
    /// overflows. The formatter negates in <c>double</c>, which is why this is a size and
    /// not an <see cref="OverflowException"/>.
    /// </summary>
    [Fact]
    public void HandlesLongMinValueWithoutOverflowing()
        => Assert.Equal("-8 EB", AnalyticsService.FormatBytes(long.MinValue));
}
