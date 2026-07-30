using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Controller;
using JellyfinGraveyardAnalytics.Configuration;
using JellyfinGraveyardAnalytics.Database;
using JellyfinGraveyardAnalytics.Services;
using Microsoft.Extensions.DependencyInjection;

namespace JellyfinGraveyardAnalytics
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public override string Name => "Graveyard Analytics";
        public override Guid Id => Guid.Parse("8f1c5c1b-7f1e-4b6c-9a2e-3d4c9f7a6e21");

        /// <summary>
        /// Jellyfin constructs the plugin, so its configuration can only be reached through a
        /// static. This is the only one left: <see cref="PluginConfigurationSource"/> reads it
        /// and everything else takes what it needs through a constructor. The
        /// <c>LibraryManager</c> / <c>UserManager</c> / <c>UserDataManager</c> statics that
        /// used to sit here were a second copy of services the DI container already had, and
        /// the <c>UserDataManager</c> one was passed around without ever being used.
        /// </summary>
        public static Plugin Instance { get; private set; } = null!;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return [
                new PluginPageInfo
                {
                    Name = "GraveyardAnalytics",
                    EmbeddedResourcePath = "JellyfinGraveyardAnalytics.WebUI.dashboard.html",
                    EnableInMainMenu = true,
                    MenuSection = "admin",
                    MenuIcon = "analytics"
                }
            ];
        }
    }

    public class GraveyardServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddHttpClient<TracearrService>();

            // The one adapter over Plugin.Instance. Everything downstream of it is a normal
            // constructor dependency, which is what makes the services constructible outside
            // a running Jellyfin.
            serviceCollection.AddSingleton<IPluginConfigurationSource, PluginConfigurationSource>();

            // One handle on the Playback Reporting database for the whole server. This used to
            // be a hand-rolled lazy getter on Plugin that two concurrent requests could both
            // run; the container does the same job correctly.
            serviceCollection.AddSingleton<Repository>();

            // The cache is the singleton — a per-request one would cache nothing. The
            // provider around it stays scoped so it can depend on the transient
            // TracearrService without pinning its HttpClient.
            serviceCollection.AddSingleton(
                _ => new TtlCache<PlaybackStats>(PlaybackStatsProvider.CacheLifetime));
            serviceCollection.AddScoped<PlaybackStatsProvider>();

            // Scoped, i.e. one per request, which is what it was already getting when each
            // action newed one up. It memoizes the library's episode index for the life of one
            // request and must not outlive it.
            serviceCollection.AddScoped<AnalyticsService>();
        }
    }
}
