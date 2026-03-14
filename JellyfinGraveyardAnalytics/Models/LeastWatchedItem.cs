namespace JellyfinAnalyticsPlugin.Models
{
    public class LeastWatchedItem
    {
        public string MediaId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public int PlayCount { get; set; }
        public long Size { get; set; } // Raw bytes
        public string FormattedSize { get; set; } = "0 MB";
        public int UniqueViewers { get; set; }
        public System.DateTime? LastPlayed { get; set; }
        public long TotalDurationSeconds { get; set; }
        public string FormattedDuration { get; set; } = "00:00:00";
    }
}
