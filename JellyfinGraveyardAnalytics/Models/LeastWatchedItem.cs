namespace JellyfinGraveyardAnalytics.Models
{
    public class LeastWatchedItem
    {
        public string MediaId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;

        // No Path here on purpose: this DTO is serialized straight to the admin UI,
        // which never displays a path, and shipping one leaks the media library layout.
        public int PlayCount { get; set; }
        public long Size { get; set; } // Raw bytes
        public string FormattedSize { get; set; } = "0 MB";
        public int UniqueViewers { get; set; }
        public System.DateTime? LastPlayed { get; set; }
        public long TotalDurationSeconds { get; set; }
        public string FormattedDuration { get; set; } = "00:00:00";
        public System.DateTime? DateAdded { get; set; }
    }
}
