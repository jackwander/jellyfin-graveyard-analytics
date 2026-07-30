using System.Reflection;
using JellyfinGraveyardAnalytics.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

namespace GraveyardAnalytics.Tests.Support;

/// <summary>
/// The Jellyfin side of the world, stubbed just far enough to drive
/// <see cref="JellyfinGraveyardAnalytics.Services.AnalyticsService"/>.
/// </summary>
/// <remarks>
/// <see cref="ILibraryManager"/> has well over a hundred members and the service calls one
/// of them, so it is a <see cref="DispatchProxy"/> rather than a hand-written class: every
/// other member throws if it is ever reached, which is the behaviour a stub should have —
/// a test that starts depending on something new fails loudly instead of silently reading
/// a default.
/// </remarks>
internal class TestLibraryManagerProxy : DispatchProxy
{
    public IReadOnlyList<BaseItem> Items { get; set; } = Array.Empty<BaseItem>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(ILibraryManager.GetItemList)
            && args?.Length == 1
            && args[0] is InternalItemsQuery)
        {
            return Items.ToList();
        }

        throw new NotSupportedException(
            $"The test library stub was asked for {targetMethod?.Name}, which no test has taught it. "
            + "Either the service started calling something new, or the test needs to say what it expects.");
    }
}

internal static class TestLibrary
{
    /// <summary>An <see cref="ILibraryManager"/> whose only query answers with these items.</summary>
    public static ILibraryManager Containing(params BaseItem[] items)
    {
        var proxy = DispatchProxy.Create<ILibraryManager, TestLibraryManagerProxy>();
        ((TestLibraryManagerProxy)(object)proxy).Items = items;
        return proxy;
    }

    /// <summary>
    /// A movie with the three fields the Morgue reads: when it entered the library, how big
    /// it is, and what to call it. <paramref name="dateAdded"/> is the whole subject of D1's
    /// floor gate, so it is required rather than defaulted.
    /// </summary>
    public static Movie MovieAdded(DateTime dateAdded, string name = "A Film", long size = 1024, string? id = null)
        => new()
        {
            Id = id is null ? Guid.NewGuid() : Guid.Parse(id),
            Name = name,
            DateCreated = dateAdded,
            Size = size
        };

    /// <summary>Configuration held in a field, so a test can change it between calls.</summary>
    public sealed class Configuration : IPluginConfigurationSource
    {
        public PluginConfiguration Current { get; } = new();
    }
}
