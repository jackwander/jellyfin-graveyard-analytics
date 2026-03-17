using System.Collections.Generic;

namespace JellyfinGraveyardAnalytics.Models
{
    public class LeastWatchedResponse
    {
        public List<LeastWatchedItem> Items { get; set; } = new List<LeastWatchedItem>();
        public string TotalWastedSize { get; set; } = "0 GB";
    }
}
