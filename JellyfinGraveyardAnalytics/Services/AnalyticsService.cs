using System;
using System.Linq;
using System.Collections.Generic;
using JellyfinGraveyardAnalytics.Database;
using JellyfinGraveyardAnalytics.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Querying;

namespace JellyfinGraveyardAnalytics.Services
{
    public class AnalyticsService
    {
        private readonly Repository _repository;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly JellyfinGraveyardAnalytics.Configuration.IPluginConfigurationSource _configSource;

        /// <summary>
        /// Resolved from the container per request. It used to be constructed by hand in four
        /// controller actions, two of its five arguments pulled out of statics on
        /// <c>Plugin</c> — and one of those, <c>IUserDataManager</c>, was never used by
        /// anything here.
        /// </summary>
        public AnalyticsService(
            Repository repository,
            ILibraryManager libraryManager,
            IUserManager userManager,
            JellyfinGraveyardAnalytics.Configuration.IPluginConfigurationSource configSource)
        {
            _repository = repository;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _configSource = configSource;
        }

        /// <summary>
        /// Maximum plays for a row to count as "barely touched". Surfaced in the UI label,
        /// so the two must move together.
        /// </summary>
        public const int BarelyTouchedPlayCeiling = 2;

        /// <summary>
        /// Every episode in the library, grouped by series id. Built at most once per
        /// request (the service is registered scoped) and never at all when the request
        /// touches no series.
        /// </summary>
        /// <remarks>
        /// Replaces a <c>GetRecursiveChildren</c> walk per series. The old shape ran one
        /// library walk for every row it mapped, so a 500-series library did 500 walks to
        /// render ten rows — and it did that again on every debounced keystroke. One query
        /// answers all of them.
        /// </remarks>
        private Dictionary<Guid, List<BaseItem>>? _episodesBySeries;

        private Dictionary<Guid, List<BaseItem>> EpisodesBySeries
        {
            get
            {
                if (_episodesBySeries is not null)
                {
                    return _episodesBySeries;
                }

                var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Episode },
                    IsVirtualItem = false,
                    Recursive = true
                };

                _episodesBySeries = _libraryManager.GetItemList(query)
                    .OfType<MediaBrowser.Controller.Entities.TV.Episode>()
                    .GroupBy(e => e.SeriesId)
                    .ToDictionary(g => g.Key, g => g.Cast<BaseItem>().ToList());

                return _episodesBySeries;
            }
        }

        /// <summary>
        /// Episodes of one series. Item 17: membership comes from the query's
        /// <c>BaseItemKind.Episode</c> filter rather than a <c>Path != null</c> test, which
        /// also admitted seasons and any other pathless child.
        /// </summary>
        private List<BaseItem> EpisodesOf(MediaBrowser.Controller.Entities.TV.Series series)
            => EpisodesBySeries.TryGetValue(series.Id, out var episodes)
                ? episodes
                : new List<BaseItem>();

        /// <summary>
        /// The Morgue: strictly zero-play items that have been in the library long enough
        /// for that to mean neglect (D1), and that playback history can actually speak to.
        /// Optionally widened to barely-touched rows, which are otherwise visible nowhere —
        /// the Sanctuary sorts by vitality descending.
        /// </summary>
        /// <param name="includeUnverifiable">
        /// Include candidates predating playback history. They cannot be confirmed unwatched,
        /// so this is opt-in: the list feeds Condemn and then deletion.
        /// </param>
        public JellyfinGraveyardAnalytics.Models.LeastWatchedResponse GetLeastWatchedItems(
          string mediaType,
          string? mediaSearch,
          int limit,
          PlaybackStats stats,
          bool includeBarelyTouched = false,
          bool includeUnverifiable = false)
        {
            var playCounts = stats.PlayCounts;
            var itemViewers = stats.ItemViewers;
            var lastPlayedDates = stats.LastPlayedDates;
            var historyFloorUtc = stats.HistoryFloorUtc;

            var now = DateTime.UtcNow;
            var graceDays = _configSource.Current.MorgueGraceDays;
            var cutoff = now.AddDays(-graceDays);

            // Floor gate, replacing D1's grace clamp. An item added before playback history
            // begins reads as zero-play whether it was loved or ignored, so by default it is
            // withheld rather than clamped into the list: shrinking the grace period to match
            // short coverage made the age test *easier* and admitted more of those items, the
            // opposite of what the clamp was for. Opt in to see them.
            int coverageDays = historyFloorUtc.HasValue
                ? (int)Math.Max(0, Math.Floor((now - historyFloorUtc.Value).TotalDays))
                : 0;

            // With no history nothing is verifiable, so only the explicit opt-in shows anything.
            if (!historyFloorUtc.HasValue && !includeUnverifiable)
            {
                return new JellyfinGraveyardAnalytics.Models.LeastWatchedResponse
                {
                    Items = new List<JellyfinGraveyardAnalytics.Models.LeastWatchedItem>(),
                    TotalSize = FormatBytes(0),

                    // Not "0 B": with no history there is no reclaimable figure to state.
                    TotalWasted = null,
                    TotalCoversAllMatches = false,
                    CoverageDays = 0,
                    GraceDays = graceDays,
                    IncludingUnverifiable = false,
                    UnverifiableCandidateCount = 0,
                    HistoryFloorUtc = null
                };
            }

            var query = BuildMediaQuery(mediaType, mediaSearch, chapelOnly: false);
            var candidates = ApplySearchAndDedupe(_libraryManager.GetItemList(query), mediaSearch);

            // The age test reads straight off the item and nothing the mapping computes can
            // change it, so it runs before the mapping that has to touch every episode of
            // every series. Only rows that could still qualify get mapped.
            //
            // The floor gate deliberately stays downstream: items it withholds are still
            // *counted* for the banner's disclosure, and that count is of zero-play
            // candidates, which is not known until the mapping has run.
            var aged = candidates
                .Where(item => item.DateCreated <= cutoff)
                .ToList();

            var mappedItems = aged.Select(item =>
            {
                string formattedId = item.Id.ToString("N");
                long totalSize = 0;
                int totalPlays = 0;
                int uniqueUsers = 0;
                System.DateTime? lastPlayed = null;

                if (item is MediaBrowser.Controller.Entities.TV.Series series)
                {
                    var validEpisodes = EpisodesOf(series);

                    totalSize = validEpisodes.Sum(e => e.Size ?? 0);
                    totalPlays = (playCounts.TryGetValue(formattedId, out int sCount) ? sCount : 0) +
                                 validEpisodes.Sum(e => playCounts.TryGetValue(e.Id.ToString("N"), out int cCount) ? cCount : 0);

                    var seriesUsers = new HashSet<string>();
                    if (itemViewers.TryGetValue(formattedId, out var sUsers)) seriesUsers.UnionWith(sUsers);

                    var episodeDates = new List<System.DateTime>();
                    if (lastPlayedDates.TryGetValue(formattedId, out var sDate)) episodeDates.Add(sDate);

                    foreach (var e in validEpisodes)
                    {
                        string episodeId = e.Id.ToString("N");

                        if (itemViewers.TryGetValue(episodeId, out var eUsers))
                        {
                            seriesUsers.UnionWith(eUsers);
                        }

                        if (lastPlayedDates.TryGetValue(episodeId, out var eDate))
                        {
                            episodeDates.Add(eDate);
                        }
                    }
                    uniqueUsers = seriesUsers.Count;

                    if (episodeDates.Count > 0)
                    {
                        lastPlayed = episodeDates.Max();
                    }
                }
                else
                {
                    totalSize = item.Size ?? 0;
                    totalPlays = playCounts.TryGetValue(formattedId, out int count) ? count : 0;

                    if (itemViewers.TryGetValue(formattedId, out var mUsers))
                    {
                        uniqueUsers = mUsers.Count;
                    }

                    if (lastPlayedDates.TryGetValue(formattedId, out var mDate))
                    {
                        lastPlayed = mDate;
                    }
                }

                return new JellyfinGraveyardAnalytics.Models.LeastWatchedItem
                {
                    MediaId = item.Id.ToString(),
                    Name = item.Name ?? "Unknown",
                    Type = item is MediaBrowser.Controller.Entities.Movies.Movie ? "Movie" : "Series",
                    PlayCount = totalPlays,
                    UniqueViewers = uniqueUsers,
                    Size = totalSize,
                    FormattedSize = FormatBytes(totalSize),
                    LastPlayed = lastPlayed,
                    DateAdded = item.DateCreated
                };
            })
            .Where(x => x != null)
            .ToList();

            // Zero plays, aged past the grace period. "Barely touched" widens the play test
            // only -- the age test and the floor gate always apply.
            int playCeiling = includeBarelyTouched ? BarelyTouchedPlayCeiling : 0;

            // Age was already applied to `aged` above, before the mapping.
            var morgueCandidates = mappedItems
                .Where(x => x.PlayCount <= playCeiling)
                .ToList();

            // Candidates history cannot speak to, counted whether or not they are shown so
            // the banner can say what is being withheld.
            int unverifiableCandidates = morgueCandidates.Count(x => IsUnverifiable(x, historyFloorUtc));

            var morgueItems = (includeUnverifiable ? morgueCandidates : morgueCandidates.Where(x => !IsUnverifiable(x, historyFloorUtc)))
                .OrderByDescending(x => x.Size)
                .ThenBy(x => x.PlayCount)
                .Take(limit)
                .ToList();

            return new JellyfinGraveyardAnalytics.Models.LeastWatchedResponse
            {
                Items = morgueItems,

                // Header and table now describe the same set (D1), so this is capped by
                // `limit` where the other two views' totals are not.
                TotalSize = FormatBytes(morgueItems.Sum(x => x.Size)),
                TotalCoversAllMatches = false,

                // Identical to TotalSize in the default state, since the rows are zero-play and
                // verifiable by construction. The two separate once either toggle is on.
                TotalWasted = FormatReclaimable(morgueItems, historyFloorUtc),
                CoverageDays = coverageDays,
                GraceDays = graceDays,
                IncludingUnverifiable = includeUnverifiable,
                UnverifiableCandidateCount = unverifiableCandidates,
                HistoryFloorUtc = historyFloorUtc
            };
        }

        /// <summary>
        /// One query shape for all three media views, so they can no longer disagree about
        /// what the library contains.
        /// </summary>
        private static MediaBrowser.Controller.Entities.InternalItemsQuery BuildMediaQuery(
            string mediaType, string? mediaSearch, bool chapelOnly)
        {
            var kinds = new List<Jellyfin.Data.Enums.BaseItemKind>();
            if (string.Equals(mediaType, "All", StringComparison.OrdinalIgnoreCase))
            {
                kinds.Add(Jellyfin.Data.Enums.BaseItemKind.Movie);
                kinds.Add(Jellyfin.Data.Enums.BaseItemKind.Series);
            }
            else if (Enum.TryParse<Jellyfin.Data.Enums.BaseItemKind>(mediaType, true, out var kind))
            {
                kinds.Add(kind);
            }
            else
            {
                kinds.Add(Jellyfin.Data.Enums.BaseItemKind.Movie);
            }

            var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                IncludeItemTypes = kinds.ToArray(),
                IsVirtualItem = false,
                Recursive = true
            };

            if (chapelOnly)
            {
                query.Tags = new[] { "[Chapel]" };
            }
            else
            {
                query.ExcludeTags = new[] { "[Chapel]" };
            }

            if (!string.IsNullOrWhiteSpace(mediaSearch))
            {
                query.SearchTerm = mediaSearch;
            }

            return query;
        }

        /// <summary>
        /// Whether playback history can speak to this item at all. An item added before
        /// history begins reads as zero-play whether it was loved or ignored, and with no
        /// history at all nothing is verifiable. This is D1's floor gate, shared so that
        /// anything *claiming* an item is unwatched applies the same test the Morgue does.
        /// </summary>
        private static bool IsUnverifiable(
            JellyfinGraveyardAnalytics.Models.LeastWatchedItem item, DateTime? historyFloorUtc)
            => !historyFloorUtc.HasValue
                || (item.DateAdded.HasValue && item.DateAdded.Value < historyFloorUtc.Value);

        /// <summary>
        /// Formatted size of the rows that can be *shown* to be unwatched — zero plays, and
        /// added inside the window history covers. Null when there is nothing to report, which
        /// covers both "no reclaimable space" and "no history to judge by": the UI treats them
        /// alike, and neither should print a claim.
        /// </summary>
        /// <remarks>
        /// The floor test is the point. Without it this reported "never played" about exactly
        /// the items D1 refuses to call unwatched — and on the Chapel, where the row action is
        /// Exorcise and there is no coverage banner to qualify it.
        /// </remarks>
        private static string? FormatReclaimable(
            IEnumerable<JellyfinGraveyardAnalytics.Models.LeastWatchedItem> rows,
            DateTime? historyFloorUtc)
        {
            if (!historyFloorUtc.HasValue)
            {
                return null;
            }

            long reclaimable = rows
                .Where(x => x.PlayCount == 0 && !IsUnverifiable(x, historyFloorUtc))
                .Sum(x => x.Size);

            return reclaimable > 0 ? FormatBytes(reclaimable) : null;
        }

        /// <summary>
        /// One search-and-dedupe rule for all three media views. Keys on
        /// (Name, ProductionYear, Kind) rather than lowercased name: the old rule collapsed
        /// The Thing (1982) and The Thing (2011) into a single row and lost the other's size.
        /// </summary>
        private static List<BaseItem> ApplySearchAndDedupe(
            IEnumerable<BaseItem> items, string? mediaSearch)
        {
            var filtered = items.Where(i =>
                string.IsNullOrWhiteSpace(mediaSearch)
                || (i.Name != null && i.Name.Contains(mediaSearch, StringComparison.OrdinalIgnoreCase)));

            return filtered
                .GroupBy(i => (
                    Name: (i.Name ?? string.Empty).Trim().ToLowerInvariant(),
                    Year: i.ProductionYear,
                    Kind: i.GetBaseItemKind()))
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// The one size formatter. The previous version divided the running <c>long</c>
        /// mid-loop (losing precision) and indexed its suffix array with an already-
        /// incremented counter, throwing <see cref="IndexOutOfRangeException"/> at 1 PB and
        /// above. Scaling happens in double and the index can never leave the array.
        /// </summary>
        public static string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };

            if (bytes == 0)
            {
                return "0 B";
            }

            var negative = bytes < 0;
            double value = negative ? -(double)bytes : bytes;

            int order = 0;
            while (value >= 1024 && order < suffixes.Length - 1)
            {
                value /= 1024;
                order++;
            }

            return $"{(negative ? "-" : string.Empty)}{value:0.##} {suffixes[order]}";
        }

        public JellyfinGraveyardAnalytics.Models.LeastWatchedResponse GetPurgatoryItems(
            string mediaType,
            string? mediaSearch,
            int limit,
            PlaybackStats stats)
        {
            var playCounts = stats.PlayCounts;
            var itemViewers = stats.ItemViewers;
            var lastPlayedDates = stats.LastPlayedDates;

            // Comes from the provider with the other three now. It used to be read straight
            // off the local database here, which meant this row's Time Played came from
            // Playback Reporting while its Plays came from Tracearr.
            var playDurations = stats.PlayDurations;

            var query = BuildMediaQuery(mediaType, mediaSearch, chapelOnly: true);
            var purgatoryItems = ApplySearchAndDedupe(_libraryManager.GetItemList(query), mediaSearch);

            var mappedItems = purgatoryItems.Select(item =>
            {
                string formattedId = item.Id.ToString("N");
                long itemSize = 0;
                int totalPlays = 0;
                int uniqueUsers = 0;
                DateTime? lastPlayed = null;
                long totalDurationSeconds = 0;

                if (item is MediaBrowser.Controller.Entities.TV.Series series)
                {
                    // One source of episodes, not two. This previously ran both an
                    // InternalItemsQuery and GetRecursiveChildren, then mixed the results:
                    // size and plays came from one list while durations came from the other.
                    var validEpisodes = EpisodesOf(series);

                    itemSize = validEpisodes.Sum(e => e.Size ?? 0);

                    totalPlays = (playCounts.TryGetValue(formattedId, out int sCount) ? sCount : 0) +
                                 validEpisodes.Sum(e => playCounts.TryGetValue(e.Id.ToString("N"), out int cCount) ? cCount : 0);

                    var seriesUsers = new HashSet<string>();
                    if (itemViewers.TryGetValue(formattedId, out var sUsers)) seriesUsers.UnionWith(sUsers);

                    var episodeDates = new List<DateTime>();
                    if (lastPlayedDates.TryGetValue(formattedId, out var sDate)) episodeDates.Add(sDate);

                    totalDurationSeconds = (playDurations.TryGetValue(formattedId, out long sDur) ? sDur : 0) +
                               validEpisodes.Sum(e => playDurations.TryGetValue(e.Id.ToString("N"), out long cDur) ? cDur : 0);

                    foreach (var e in validEpisodes)
                    {
                        string episodeId = e.Id.ToString("N");
                        if (itemViewers.TryGetValue(episodeId, out var eUsers)) seriesUsers.UnionWith(eUsers);
                        if (lastPlayedDates.TryGetValue(episodeId, out var eDate)) episodeDates.Add(eDate);
                    }

                    uniqueUsers = seriesUsers.Count;
                    if (episodeDates.Count > 0) lastPlayed = episodeDates.Max();
                }
                else
                {
                    itemSize = item.Size ?? 0;

                    totalPlays = playCounts.TryGetValue(formattedId, out int count) ? count : 0;

                    if (itemViewers.TryGetValue(formattedId, out var mUsers)) uniqueUsers = mUsers.Count;
                    if (lastPlayedDates.TryGetValue(formattedId, out var mDate)) lastPlayed = mDate;
                    if (playDurations.TryGetValue(formattedId, out long mDur)) totalDurationSeconds = mDur;
                }

                var ts = System.TimeSpan.FromSeconds(totalDurationSeconds);
                string formattedDuration = $"{(int)System.Math.Floor(ts.TotalHours):D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";

                return new JellyfinGraveyardAnalytics.Models.LeastWatchedItem
                {
                    MediaId = item.Id.ToString(),
                    Name = item.Name ?? "Unknown",
                    Type = item is MediaBrowser.Controller.Entities.Movies.Movie ? "Movie" : "Series",
                    Size = itemSize,
                    FormattedSize = FormatBytes(itemSize),
                    DateAdded = item.DateCreated,
                    FormattedDuration = formattedDuration,
                    PlayCount = totalPlays,
                    UniqueViewers = uniqueUsers,
                    LastPlayed = lastPlayed
                };
            }).ToList();

            // Over every condemned item, not just the `limit` shown: this header answers "how
            // much is sitting in The Chapel", which the display cap must not change.
            var totalSize = mappedItems.Sum(x => x.Size);

            return new JellyfinGraveyardAnalytics.Models.LeastWatchedResponse
            {
                Items = mappedItems.OrderByDescending(x => x.Size).Take(limit).ToList(),
                TotalSize = FormatBytes(totalSize),
                TotalCoversAllMatches = true,

                // A condemned item may well have been watched — condemning is a decision, not a
                // measurement — so the reclaimable part is its own figure here, and it counts
                // only rows history can actually vouch for.
                TotalWasted = FormatReclaimable(mappedItems, stats.HistoryFloorUtc)
            };
        }

        public JellyfinGraveyardAnalytics.Models.LeastWatchedResponse GetLivingItems(
            string mediaType,
            string? mediaSearch,
            int limit,
            PlaybackStats stats)
        {
            var playCounts = stats.PlayCounts;
            var itemViewers = stats.ItemViewers;
            var lastPlayedDates = stats.LastPlayedDates;
            var playDurations = stats.PlayDurations;

            var query = BuildMediaQuery(mediaType, mediaSearch, chapelOnly: false);
            var uniqueItems = ApplySearchAndDedupe(_libraryManager.GetItemList(query), mediaSearch);

            var mappedItems = uniqueItems.Select(item =>
            {
                string formattedId = item.Id.ToString("N");
                long totalSize = 0;
                int totalPlays = 0;
                int uniqueUsers = 0;
                System.DateTime? lastPlayed = null;
                long totalDurationSeconds = 0;

                if (item is MediaBrowser.Controller.Entities.TV.Series series)
                {
                    var validEpisodes = EpisodesOf(series);

                    totalSize = validEpisodes.Sum(e => e.Size ?? 0);
                    totalPlays = (playCounts.TryGetValue(formattedId, out int sCount) ? sCount : 0) +
                                 validEpisodes.Sum(e => playCounts.TryGetValue(e.Id.ToString("N"), out int cCount) ? cCount : 0);

                    totalDurationSeconds = (playDurations.TryGetValue(formattedId, out long sDur) ? sDur : 0) +
                               validEpisodes.Sum(e => playDurations.TryGetValue(e.Id.ToString("N"), out long cDur) ? cDur : 0);

                    var seriesUsers = new HashSet<string>();
                    if (itemViewers.TryGetValue(formattedId, out var sUsers)) seriesUsers.UnionWith(sUsers);

                    var episodeDates = new List<System.DateTime>();
                    if (lastPlayedDates.TryGetValue(formattedId, out var sDate)) episodeDates.Add(sDate);

                    foreach (var e in validEpisodes)
                    {
                        string episodeId = e.Id.ToString("N");
                        if (itemViewers.TryGetValue(episodeId, out var eUsers)) seriesUsers.UnionWith(eUsers);
                        if (lastPlayedDates.TryGetValue(episodeId, out var eDate)) episodeDates.Add(eDate);
                    }
                    uniqueUsers = seriesUsers.Count;
                    if (episodeDates.Count > 0) lastPlayed = episodeDates.Max();
                }
                else
                {
                    totalSize = item.Size ?? 0;
                    totalPlays = playCounts.TryGetValue(formattedId, out int count) ? count : 0;
                    if (playDurations.TryGetValue(formattedId, out long mDur)) totalDurationSeconds = mDur;
                    if (itemViewers.TryGetValue(formattedId, out var mUsers)) uniqueUsers = mUsers.Count;
                    if (lastPlayedDates.TryGetValue(formattedId, out var mDate)) lastPlayed = mDate;
                }

                if (totalPlays == 0) return null;

                var ts = System.TimeSpan.FromSeconds(totalDurationSeconds);
                string formattedDuration = $"{(int)System.Math.Floor(ts.TotalHours):D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";

                return new JellyfinGraveyardAnalytics.Models.LeastWatchedItem
                {
                    MediaId = item.Id.ToString(),
                    Name = item.Name ?? "Unknown",
                    Type = item is MediaBrowser.Controller.Entities.Movies.Movie ? "Movie" : "Series",
                    PlayCount = totalPlays,
                    UniqueViewers = uniqueUsers,
                    Size = totalSize,
                    FormattedSize = FormatBytes(totalSize),
                    LastPlayed = lastPlayed,
                    TotalDurationSeconds = totalDurationSeconds,
                    FormattedDuration = formattedDuration,
                    DateAdded = item.DateCreated
                };
            })
            .Where(x => x != null)
            .Cast<JellyfinGraveyardAnalytics.Models.LeastWatchedItem>()
            .ToList();

            long totalLivingSize = mappedItems.Sum(x => x.Size);

            return new JellyfinGraveyardAnalytics.Models.LeastWatchedResponse
            {
                Items = mappedItems
                    .OrderByDescending(x => x.PlayCount)
                    .Take(limit)
                    .ToList(),

                TotalSize = FormatBytes(totalLivingSize),
                TotalCoversAllMatches = true

                // TotalWasted stays null: every row here has at least one play, so there is
                // nothing this view could call wasted. It was reporting living media in a
                // field named for dead weight, and the UI renamed the label to hide it.
            };
        }

        /// <exception cref="PlaybackDataUnavailableException">
        /// The Playback Reporting database is not installed. Checked here rather than in the
        /// controller so the guard sits with the code that needs it — the media tabs get the
        /// same check from <see cref="PlaybackStatsProvider"/>, and this path used to reach
        /// around to <c>Plugin.Instance.Repository</c> for it.
        /// </exception>
        public JellyfinGraveyardAnalytics.Models.VisitorResponse GetVisitorActivity(string endDateString, int weeksBack)
        {
            if (!_repository.PlaybackDatabaseExists)
            {
                throw new PlaybackDataUnavailableException(
                    "The Playback Reporting plugin database was not found.");
            }

            // UTC end to end: the rows are stored as naive UTC, so a local-time window
            // silently shifted every bound by the server's offset.
            if (!System.DateTime.TryParse(
                    endDateString,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out System.DateTime endDate))
            {
                endDate = System.DateTime.UtcNow;
            }

            endDate = System.DateTime.SpecifyKind(endDate.Date.AddDays(1).AddTicks(-1), System.DateTimeKind.Utc);
            System.DateTime startDate = System.DateTime.SpecifyKind(
                endDate.AddDays(-7 * weeksBack).Date, System.DateTimeKind.Utc);

            var allUsers = _userManager.Users.ToList();
            var userDictionary = allUsers.ToDictionary(u => u.Id.ToString("N"), u => u.Username);
            var activeUserIds = new HashSet<string>();
            var userWatchTimes = new Dictionary<string, long>();

            var rowLimit = _configSource.Current.GuestbookRowLimit;

            var (rawData, truncated) = _repository.GetRawPlaybackActivity(
                startDate, endDate, rowLimit);

            var sessions = new List<JellyfinGraveyardAnalytics.Models.VisitorSession>();

            foreach (var row in rawData)
            {
                string userId = row.UserId?.Replace("-", string.Empty) ?? "Unknown";
                string visitorName = userDictionary.TryGetValue(userId, out string? name) ? name : "Deleted User";

                activeUserIds.Add(userId);

                long durationSeconds = row.PlayDuration ?? 0;

                if (!userWatchTimes.ContainsKey(visitorName)) userWatchTimes[visitorName] = 0;
                userWatchTimes[visitorName] += durationSeconds;

                var ts = System.TimeSpan.FromSeconds(durationSeconds);
                string formattedDuration = $"{(int)System.Math.Floor(ts.TotalHours):D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";

                // Same column, same stored format, same helper as the Morgue's Last Breath.
                // This was a bare DateTime.TryParse whose result was never checked: an
                // unparseable row silently became DateTime.MinValue and printed as a year-0001
                // session rather than admitting it could not read the timestamp. The helper
                // also pins InvariantCulture, so the row is not read through the operator's
                // locale, and the format provider is pinned for the same reason on the way out.
                string formattedTime = JellyfinGraveyardAnalytics.Database.Repository
                    .TryParseStoredUtc(row.DateCreated, out var rowDate)
                    ? rowDate.ToLocalTime().ToString(
                        "MMM dd, yyyy - h:mm tt",
                        System.Globalization.CultureInfo.InvariantCulture)
                    : "Unknown";

                sessions.Add(new JellyfinGraveyardAnalytics.Models.VisitorSession
                {
                    Time = formattedTime,
                    Visitor = visitorName,
                    Subject = row.ItemName ?? "Unknown",
                    Type = row.ItemType ?? "Unknown",
                    Device = row.DeviceName ?? "Unknown",
                    Player = row.ClientName ?? string.Empty,
                    Method = row.PlaybackMethod ?? "DirectPlay",
                    Duration = formattedDuration,
                    IsTranscode = row.PlaybackMethod?.Contains("Transcode", System.StringComparison.OrdinalIgnoreCase) == true
                });
            }

            var ghosts = allUsers
                .Where(u => !activeUserIds.Contains(u.Id.ToString("N")))
                .Select(u => u.Username)
                .ToList();

            var leaderboard = userWatchTimes
                .OrderByDescending(kvp => kvp.Value)
                .Take(3)
                .Select(kvp => {
                    var ts = System.TimeSpan.FromSeconds(kvp.Value);
                    return new JellyfinGraveyardAnalytics.Models.VisitorLeaderboardEntry
                    {
                        Name = kvp.Key,
                        TotalTime = $"{(int)System.Math.Floor(ts.TotalHours)}h {ts.Minutes}m"
                    };
                }).ToList();

            return new JellyfinGraveyardAnalytics.Models.VisitorResponse
            {
                Sessions = sessions,
                Ghosts = ghosts,
                Leaderboard = leaderboard,
                Truncated = truncated,
                RowLimit = rowLimit
            };
        }
    }
}
