using System;

namespace JellyfinAnalyticsPlugin.Models
{
    public class PlaybackEvent
    {
        public string EventType { get; set; } = string.Empty; // Start, Stop, Pause
        public string UserId { get; set; } = string.Empty;
        public string MediaId { get; set; } = string.Empty;
        public string MediaTitle { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public int? PlayDurationSeconds { get; set; }
        public double? CompletionPercentage { get; set; }
        public string DeviceName { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
    }
}
