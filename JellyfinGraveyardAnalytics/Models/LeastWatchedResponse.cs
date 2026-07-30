using System.Collections.Generic;

namespace JellyfinGraveyardAnalytics.Models
{
    public class LeastWatchedResponse
    {
        public List<LeastWatchedItem> Items { get; set; } = new List<LeastWatchedItem>();

        /// <summary>
        /// Sum of the rows actually returned, formatted. Header and table now describe the
        /// same set, which they did not before (D1).
        /// </summary>
        public string TotalWastedSize { get; set; } = "0 GB";

        /// <summary>
        /// Days of playback history available. Null where not computed (only the Morgue
        /// needs it); zero means the history database is empty.
        /// </summary>
        public int? CoverageDays { get; set; }

        /// <summary>
        /// Grace period applied. No longer clamped to coverage — the floor gate replaced
        /// that, so this is simply the configured value.
        /// </summary>
        public int? GraceDays { get; set; }

        /// <summary>
        /// Candidates added before playback history begins — items history cannot call
        /// unwatched either way. Counted whether or not they are shown, so the UI can report
        /// what is being withheld as well as what is included.
        /// </summary>
        public int? UnverifiableCandidateCount { get; set; }

        /// <summary>
        /// Whether those unverifiable candidates are included in <see cref="Items"/>.
        /// </summary>
        public bool IncludingUnverifiable { get; set; }

        /// <summary>
        /// Start of playback history, so the UI can mark individual rows that predate it.
        /// Null when there is no history at all.
        /// </summary>
        public System.DateTime? HistoryFloorUtc { get; set; }
    }
}
