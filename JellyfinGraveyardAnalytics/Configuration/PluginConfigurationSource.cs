namespace JellyfinGraveyardAnalytics.Configuration
{
    /// <summary>
    /// Supplies the live plugin configuration to the services that need it.
    /// </summary>
    /// <remarks>
    /// Read on every use rather than captured once: the admin can save new settings at any
    /// time, and a captured instance would keep serving the values that were current when
    /// the object was built.
    /// </remarks>
    public interface IPluginConfigurationSource
    {
        PluginConfiguration Current { get; }
    }

    /// <summary>
    /// The one type that reaches <see cref="Plugin.Instance"/>. Jellyfin constructs the
    /// plugin itself, so the static is unavoidable somewhere; confining it here is what lets
    /// every service take its configuration through the constructor instead of pulling it out
    /// of a static, which is why four of them could not be constructed without a running
    /// server.
    /// </summary>
    public sealed class PluginConfigurationSource : IPluginConfigurationSource
    {
        public PluginConfiguration Current => Plugin.Instance.Configuration;
    }
}
