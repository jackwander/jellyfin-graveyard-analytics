using System.Reflection;

namespace GraveyardAnalytics.Tests;

/// <summary>
/// The plugin ships three files inside its own assembly: the dashboard, and the two pieces
/// of Chapel collection artwork.
///
/// The artwork used to be fetched from raw.githubusercontent.com when an item was first
/// condemned, which made the branding depend on the Jellyfin server having outbound
/// internet and on the repository still being at that path — and when either failed the
/// collection was simply created unbranded, with a logged error nobody reads.
///
/// Embedding moves the failure from runtime to build time, but only if the names agree.
/// A resource is addressed by a string built from its path in the csproj, and the
/// controller holds that string as a constant: rename the folder, or switch the file
/// extension, and the constant still compiles while the lookup returns null. That is the
/// one way this can still break silently, so it is the thing under test.
/// </summary>
public class EmbeddedResourceTests
{
    private static readonly Assembly Plugin = typeof(JellyfinGraveyardAnalytics.Plugin).Assembly;

    [Theory]
    [InlineData("JellyfinGraveyardAnalytics.WebUI.dashboard.html")]
    [InlineData("JellyfinGraveyardAnalytics.Resources.thechapelcollectionthumbnail.jpg")]
    [InlineData("JellyfinGraveyardAnalytics.Resources.thechapelcollectionbackdrop.jpg")]
    public void TheResourceIsPresentAndNotEmpty(string name)
    {
        using var stream = Plugin.GetManifestResourceStream(name);

        Assert.NotNull(stream);
        Assert.True(stream.Length > 0, $"{name} is embedded but empty.");
    }

    /// <summary>
    /// The names the controller asks for are the names the build produces. Read off the
    /// controller's own constants rather than retyped, so this compares the two sources
    /// instead of comparing a copy of one against itself.
    /// </summary>
    [Fact]
    public void TheControllerAsksForNamesTheAssemblyActuallyHas()
    {
        var controller = typeof(JellyfinGraveyardAnalytics.Controllers.GraveyardAnalyticsController);
        var available = Plugin.GetManifestResourceNames();

        var resourceConstants = controller
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(v => v.StartsWith("JellyfinGraveyardAnalytics.Resources.", StringComparison.Ordinal))
            .ToList();

        // If this is ever zero the test has stopped testing anything — the constants were
        // renamed or moved, and every assertion below would pass vacuously.
        Assert.Equal(2, resourceConstants.Count);

        foreach (var name in resourceConstants)
        {
            Assert.Contains(name, available);
        }
    }

    /// <summary>
    /// The artwork is JPEG, and the controller tells Jellyfin so when it saves it. A PNG
    /// dropped in under the same name would be served with the wrong content type.
    /// </summary>
    [Theory]
    [InlineData("JellyfinGraveyardAnalytics.Resources.thechapelcollectionthumbnail.jpg")]
    [InlineData("JellyfinGraveyardAnalytics.Resources.thechapelcollectionbackdrop.jpg")]
    public void TheArtworkIsActuallyJpeg(string name)
    {
        using var stream = Plugin.GetManifestResourceStream(name);
        Assert.NotNull(stream);

        var header = new byte[3];
        Assert.Equal(3, stream.ReadAtLeast(header, 3, throwOnEndOfStream: false));

        // JPEG's SOI marker, then the JFIF/EXIF application segment.
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, header);
    }
}
