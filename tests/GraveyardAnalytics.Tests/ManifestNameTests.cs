using System.Runtime.CompilerServices;
using System.Text.Json;
using JellyfinGraveyardAnalytics;

namespace GraveyardAnalytics.Tests;

/// <summary>
/// The catalogue name in <c>manifest.json</c> must equal <see cref="Plugin.Name"/>.
///
/// Jellyfin writes both into the installed copy's <c>meta.json</c>: the catalogue name on
/// install and on every catalogue refresh, <see cref="Plugin.Name"/> on every startup. Its
/// startup cleanup retires an older copy only when the names match, so while they differed
/// an update left the old copy loaded beside the new one whenever the catalogue refresh lost
/// a race on <c>header.png</c> — two controllers on every route, and every request failed
/// as ambiguous. That broke 1.2.0.5 → 1.2.0.6 on a live server. Read at Jellyfin v10.11.10:
/// <c>PluginManager.cs</c> 582-590 and <c>DiscoverPlugins</c>, <c>InstallationManager.cs</c> 192.
/// </summary>
public class ManifestNameTests
{
    [Fact]
    public void TheCatalogueNameMatchesThePluginName()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "manifest.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "manifest.json")));
        var catalogueName = manifest.RootElement[0].GetProperty("name").GetString();

        // Name is a constant expression; constructing the plugin needs a running server.
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));

        Assert.Equal(plugin.Name, catalogueName);
    }
}
