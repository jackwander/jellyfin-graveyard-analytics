using MediaBrowser.Model.Plugins;

namespace JellyfinGraveyardAnalytics.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // --- Start of Tracearr Integration ---
        public bool EnableTracearr { get; set; }
        public string TracearrUrl { get; set; }
        public string TracearrApiKey { get; set; }
        // --- End of Tracearr Integration ---

        public PluginConfiguration()
        {
            EnableTracearr = false;
            TracearrUrl = string.Empty;
            TracearrApiKey = string.Empty;
        }
    }
}
