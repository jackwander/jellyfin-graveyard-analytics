using JellyfinGraveyardAnalytics.Configuration;

namespace GraveyardAnalytics.Tests;

/// <summary>
/// The three numeric settings, clamped on write.
///
/// Two things reach these setters that nobody reviews: the admin UI, which posts whatever
/// is in the box, and the plugin's XML configuration file, which is hand-editable. A value
/// from either has to be safe by the time a query sees it — a <c>MinPlayDurationSeconds</c>
/// of -1 or a <c>GuestbookRowLimit</c> of 5,000,000 is a query nobody intended to run.
///
/// The subtle case is deserialization. A configuration written before one of these keys
/// existed has no element for it, which arrives as <c>0</c>, not as "unset". Honouring that
/// zero as "no floor" would silently restore the unfiltered aggregates the setting exists
/// to fix, on exactly the servers that upgraded rather than installed fresh — so zero falls
/// back to the default rather than being clamped up to the minimum.
/// </summary>
public class ConfigurationTests
{
    [Fact]
    public void DefaultsMatchTheDocumentedConfigSurface()
    {
        var config = new PluginConfiguration();

        Assert.Equal(120, config.MinPlayDurationSeconds);
        Assert.Equal(180, config.MorgueGraceDays);
        Assert.Equal(5000, config.GuestbookRowLimit);

        // Tracearr is opt-in, and the webhook fails closed on a fresh install because of it.
        Assert.False(config.EnableTracearr);
        Assert.Equal(string.Empty, config.TracearrUrl);
        Assert.Equal(string.Empty, config.TracearrApiKey);
    }

    /// <summary>
    /// Zero is "the element was missing", not "no floor". This is the upgrade path.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void AMissingOrNegativeValueFallsBackToTheDefault(int written)
    {
        var config = new PluginConfiguration
        {
            MinPlayDurationSeconds = written,
            MorgueGraceDays = written,
            GuestbookRowLimit = written
        };

        Assert.Equal(PluginConfiguration.DefaultMinPlayDurationSeconds, config.MinPlayDurationSeconds);
        Assert.Equal(PluginConfiguration.DefaultMorgueGraceDays, config.MorgueGraceDays);
        Assert.Equal(PluginConfiguration.DefaultGuestbookRowLimit, config.GuestbookRowLimit);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(120, 120)]
    [InlineData(3600, 3600)]
    [InlineData(3601, 3600)]
    [InlineData(int.MaxValue, 3600)]
    public void MinPlayDurationIsHeldInsideItsRange(int written, int expected)
        => Assert.Equal(expected, new PluginConfiguration { MinPlayDurationSeconds = written }.MinPlayDurationSeconds);

    [Theory]
    [InlineData(1, 30)]
    [InlineData(29, 30)]
    [InlineData(30, 30)]
    [InlineData(180, 180)]
    [InlineData(365, 365)]
    [InlineData(366, 365)]
    [InlineData(int.MaxValue, 365)]
    public void MorgueGraceDaysIsHeldInsideItsRange(int written, int expected)
        => Assert.Equal(expected, new PluginConfiguration { MorgueGraceDays = written }.MorgueGraceDays);

    [Theory]
    [InlineData(1, 100)]
    [InlineData(99, 100)]
    [InlineData(100, 100)]
    [InlineData(5000, 5000)]
    [InlineData(50000, 50000)]
    [InlineData(50001, 50000)]
    [InlineData(int.MaxValue, 50000)]
    public void GuestbookRowLimitIsHeldInsideItsRange(int written, int expected)
        => Assert.Equal(expected, new PluginConfiguration { GuestbookRowLimit = written }.GuestbookRowLimit);

    /// <summary>
    /// The clamp is on the setter, so reading back what was written is the only way a
    /// caller can find out the value was rejected — and every caller reads through the
    /// property. A backing field that skipped the clamp on read would defeat all of the
    /// above, so this asserts the round trip rather than the setter in isolation.
    /// </summary>
    [Fact]
    public void TheClampedValueIsWhatIsReadBack()
    {
        var config = new PluginConfiguration { MorgueGraceDays = 10_000 };

        Assert.Equal(365, config.MorgueGraceDays);
        Assert.Equal(365, config.MorgueGraceDays);
    }
}
