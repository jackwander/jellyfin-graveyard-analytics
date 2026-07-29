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
        /// Grace period actually applied, after clamping to <see cref="CoverageDays"/>.
        /// </summary>
        public int? EffectiveGraceDays { get; set; }

        /// <summary>
        /// Configured grace period before clamping, so the UI can say when the two differ.
        /// </summary>
        public int? ConfiguredGraceDays { get; set; }

        /// <summary>
        /// How many returned rows were added to the library before playback history begins.
        /// History does not reach back far enough to call those unwatched, so the UI flags
        /// them instead of presenting them as confirmed dead weight.
        /// </summary>
        public int? UnverifiableItemCount { get; set; }
    }
}
