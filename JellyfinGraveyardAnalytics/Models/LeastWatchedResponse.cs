using System.Collections.Generic;

namespace JellyfinAnalyticsPlugin.Models
{
    public class LeastWatchedResponse
    {
        // Use the explicit List type here to help the compiler resolve the reference
        public List<LeastWatchedItem> Items { get; set; } = new List<LeastWatchedItem>();
        public string TotalWastedSize { get; set; } = "0 GB";
    }
}
