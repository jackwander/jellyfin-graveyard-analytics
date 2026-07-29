using System.Collections.Generic;

namespace JellyfinGraveyardAnalytics.Models
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

        /// <summary>
        /// True when the row cap cut the result short, so the UI can say the window is
        /// partial instead of presenting it as the whole timeframe. The leaderboard and
        /// ghosts are derived from the returned rows, so they are partial too.
        /// </summary>
        public bool Truncated { get; set; }

        /// <summary>
        /// The cap that was applied, for the truncation notice.
        /// </summary>
        public int RowLimit { get; set; }
    }
}
