using System.Collections.Generic;

namespace JellyfinAnalyticsPlugin.Models
{
    public class VisitorSession
    {
        public string Time { get; set; } = string.Empty;
        public string Visitor { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Client { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public string Duration { get; set; } = string.Empty;
        public bool IsTranscode { get; set; }
    }

    public class VisitorLeaderboardEntry
    {
        public string Name { get; set; } = string.Empty;
        public string TotalTime { get; set; } = string.Empty;
    }

    public class VisitorResponse
    {
        public List<VisitorSession> Sessions { get; set; } = new();
        public List<VisitorLeaderboardEntry> Leaderboard { get; set; } = new();
        public List<string> Ghosts { get; set; } = new();
    }
}
