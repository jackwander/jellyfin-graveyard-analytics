using System.Collections.Generic;

namespace JellyfinGraveyardAnalytics.Models
{
    public class VisitorSession
    {
        public string Time { get; set; } = string.Empty;
        public string Visitor { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;

        /// <summary>
        /// Application doing the playing, shown as the sub-line of the Vessel cell.
        /// Distinct from <see cref="Device"/>: "Living Room TV" is the device, "Jellyfin
        /// Android TV" is the player.
        /// </summary>
        /// <remarks>
        /// There was a <c>Client</c> property here too, carrying the same value the local
        /// engine already put in this one. Nothing read it, and it was serialized on every
        /// Guestbook row.
        /// </remarks>
        public string Player { get; set; } = string.Empty;

        public string Method { get; set; } = string.Empty;
        public string Duration { get; set; } = string.Empty;
        public bool IsTranscode { get; set; }

        /// <summary>
        /// How far into the item the session got, 0-100. Null when the engine cannot say —
        /// the Playback Reporting database records elapsed time but not item runtime, so
        /// the local engine always leaves this null and the Fate cell stays empty.
        /// </summary>
        public double? ProgressPercent { get; set; }

        /// <summary>
        /// Engine's own verdict that the item was finished, which outranks
        /// <see cref="ProgressPercent"/> when set. Null when the engine does not report one.
        /// </summary>
        public bool? Watched { get; set; }
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
        /// Both engines set it: the local one from the SQL <c>LIMIT</c>, the Tracearr one
        /// when the paging loop stops with pages still unread.
        /// </summary>
        public bool Truncated { get; set; }

        /// <summary>
        /// The cap that was applied, for the truncation notice.
        /// </summary>
        public int RowLimit { get; set; }
    }
}
