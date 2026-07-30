using System.Collections.Generic;

namespace JellyfinGraveyardAnalytics.Models
{
    public class LeastWatchedResponse
    {
        public List<LeastWatchedItem> Items { get; set; } = new List<LeastWatchedItem>();

        /// <summary>
        /// Formatted total size of the rows this response describes. Named for what it is:
        /// as <c>TotalWastedSize</c> it also carried the Sanctuary's "total size of living
        /// media", so the one field meant two opposite things and the UI relabelled it per
        /// tab to cover for that.
        /// </summary>
        /// <remarks>
        /// Whether it covers the rows in <see cref="Items"/> or every matching row differs by
        /// view, so <see cref="TotalCoversAllMatches"/> says which — the figure alone is
        /// ambiguous, and the same card in the same position on screen means one thing on the
        /// Morgue and another on the Chapel.
        /// </remarks>
        public string TotalSize { get; set; } = "0 B";

        /// <summary>
        /// True when <see cref="TotalSize"/> is the total over every matching item; false when
        /// it covers only the rows in <see cref="Items"/>, i.e. is capped by <c>limit</c>.
        /// </summary>
        /// <remarks>
        /// The Morgue's total is over the rows returned, so its header and table describe the
        /// same set (D1) — false. The Chapel's and Sanctuary's headers answer "how much is in
        /// here", which a display cap must not change — true. Without this the two are
        /// indistinguishable on the wire.
        /// </remarks>
        public bool TotalCoversAllMatches { get; set; }

        /// <summary>
        /// Formatted size of those rows that can be *shown* never to have been played — the
        /// part of <see cref="TotalSize"/> that deleting would reclaim without losing anything
        /// anyone has watched. Null when there is nothing to state.
        /// </summary>
        /// <remarks>
        /// Null covers three cases the UI treats alike, because none of them should print a
        /// claim: the Sanctuary, which lists only items *with* plays; no playback history to
        /// judge by; and nothing reclaimable. Rows added before history begins are excluded —
        /// they read as unplayed whether or not they were played, which is D1's floor gate, and
        /// asserting otherwise would be worst on the Chapel, where the row action is Exorcise
        /// and no coverage banner qualifies it.
        /// Equal to <see cref="TotalSize"/> in the Morgue's default state, since that view is
        /// zero-play and verifiable by construction.
        /// </remarks>
        public string? TotalWasted { get; set; }

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
