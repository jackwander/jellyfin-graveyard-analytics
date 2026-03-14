using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Controller.Library;
using JellyfinAnalyticsPlugin.Configuration;
using JellyfinAnalyticsPlugin.Database;

namespace JellyfinAnalyticsPlugin
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public override string Name => "Graveyard Analytics";
        public override Guid Id => Guid.Parse("8f1c5c1b-7f1e-4b6c-9a2e-3d4c9f7a6e21");

        public static Plugin Instance { get; private set; } = null!;
        public static ILibraryManager LibraryManager { get; private set; } = null!;
        public static IUserManager UserManager { get; private set; } = null!;
        public static IUserDataManager UserDataManager { get; private set; } = null!;

        private Repository? _repository;
        private readonly IApplicationPaths _appPaths;

        public Repository Repository
        {
            get
            {
                if (_repository == null)
                {
                    _repository = new Repository(_appPaths);
                }
                return _repository;
            }
        }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _appPaths = applicationPaths; // Fixed: Now correctly assigned
            LibraryManager = libraryManager;
            UserManager = userManager;
            UserDataManager = userDataManager;
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return [
                new PluginPageInfo
                {
                    Name = "GraveyardAnalytics",
                    EmbeddedResourcePath = "JellyfinAnalyticsPlugin.WebUI.dashboard.html",
                    EnableInMainMenu = true,
                    MenuSection = "admin",
                    MenuIcon = "analytics"
                }
            ];
        }
    }
}
