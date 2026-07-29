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
        private readonly IUserDataManager _userDataManager;
        private readonly IUserManager _userManager;
        private readonly JellyfinGraveyardAnalytics.Configuration.PluginConfiguration _config;

        public AnalyticsService(
            Repository repository,
            ILibraryManager libraryManager,
            IUserDataManager userDataManager,
            IUserManager userManager,
            JellyfinGraveyardAnalytics.Configuration.PluginConfiguration config)
        {
            _repository = repository;
            _libraryManager = libraryManager;
            _userDataManager = userDataManager;
            _userManager = userManager;
            _config = config;
        }

        /// <summary>
        /// Maximum plays for a row to count as "barely touched". Surfaced in the UI label,
        /// so the two must move together.
        /// </summary>
        public const int BarelyTouchedPlayCeiling = 2;

        /// <summary>
        /// The Morgue: strictly zero-play items that have been in the library long enough
        /// for that to mean neglect (D1). Optionally widened to barely-touched rows, which
        /// are otherwise visible nowhere — the Sanctuary sorts by vitality descending.
        /// </summary>
        /// <param name="historyFloorUtc">
        /// Oldest playback activity on record, used to clamp the grace period to the history
        /// that actually exists. Null when there is no history at all.
        /// </param>
        public JellyfinGraveyardAnalytics.Models.LeastWatchedResponse GetLeastWatchedItems(
          string mediaType,
          string? mediaSearch,
          int limit,
          Dictionary<string, int> playCounts,
          Dictionary<string, HashSet<string>> itemViewers,
          Dictionary<string, DateTime> lastPlayedDates,
          bool includeBarelyTouched = false,
          DateTime? historyFloorUtc = null)
        {
            var now = DateTime.UtcNow;
            var configuredGrace = _config.MorgueGraceDays;

            // Coverage is what history can actually speak to. D1 clamps the grace period to
            // it so the view never claims a 180-day judgement on a 20-day-old database.
            int coverageDays = historyFloorUtc.HasValue
                ? (int)Math.Max(0, Math.Floor((now - historyFloorUtc.Value).TotalDays))
                : 0;

            int effectiveGrace = Math.Min(configuredGrace, coverageDays);
            var cutoff = now.AddDays(-effectiveGrace);

            // No history at all means no item can be shown to be unwatched. Returning the
            // whole library here would be the exact false-positive flood D1's clamp exists
            // to prevent, so the view stays empty and the banner explains why.
            if (!historyFloorUtc.HasValue)
            {
                return new JellyfinGraveyardAnalytics.Models.LeastWatchedResponse
                {
                    Items = new List<JellyfinGraveyardAnalytics.Models.LeastWatchedItem>(),
                    TotalWastedSize = FormatBytes(0),
                    CoverageDays = 0,
                    EffectiveGraceDays = 0,
                    ConfiguredGraceDays = configuredGrace,
                    UnverifiableItemCount = 0
                };
            }

            var query = BuildMediaQuery(mediaType, mediaSearch, chapelOnly: false);
            var candidates = ApplySearchAndDedupe(_libraryManager.GetItemList(query), mediaSearch);

            var mappedItems = candidates.Select(item =>
            {
                string formattedId = item.Id.ToString("N");
                long totalSize = 0;
                int totalPlays = 0;
                int uniqueUsers = 0;
                System.DateTime? lastPlayed = null;

                if (item is MediaBrowser.Controller.Entities.TV.Series series)
                {
                    var children = series.GetRecursiveChildren(null);

                    var validEpisodes = children.Where(c => c.Path != null).ToList();

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

                    if (episodeDates.Any())
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

            // D1: zero plays, aged past the (clamped) grace period. "Barely touched" widens
            // the play test only -- the age test always applies.
            int playCeiling = includeBarelyTouched ? BarelyTouchedPlayCeiling : 0;

            var morgueItems = mappedItems
                .Where(x => x.PlayCount <= playCeiling)
                .Where(x => x.DateAdded.HasValue && x.DateAdded.Value <= cutoff)
                .OrderByDescending(x => x.Size)
                .ThenBy(x => x.PlayCount)
                .Take(limit)
                .ToList();

            // Items older than the history floor cannot be confirmed unwatched: the history
            // does not reach them. They are shown, but counted so the UI can say so.
            int unverifiable = historyFloorUtc.HasValue
                ? morgueItems.Count(x => x.DateAdded.HasValue && x.DateAdded.Value < historyFloorUtc.Value)
                : morgueItems.Count;

            return new JellyfinGraveyardAnalytics.Models.LeastWatchedResponse
            {
                Items = morgueItems,

                // Header and table now describe the same set (D1).
                TotalWastedSize = FormatBytes(morgueItems.Sum(x => x.Size)),
                CoverageDays = coverageDays,
                EffectiveGraceDays = effectiveGrace,
                ConfiguredGraceDays = configuredGrace,
                UnverifiableItemCount = unverifiable
            };
        }

        /// <summary>
        /// One query shape for all three media views, so they can no longer disagree about
        /// what the library contains.
        /// </summary>
        private MediaBrowser.Controller.Entities.InternalItemsQuery BuildMediaQuery(
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
            Dictionary<string, int> playCounts,
            Dictionary<string, HashSet<string>> itemViewers,
            Dictionary<string, DateTime> lastPlayedDates)
        {
            // Hoisted: this is a full-table SUM/GROUP BY. Inside the Select lambda below it
            // re-ran once per Chapel item.
            var playDurations = _repository.GetItemPlayDurations(_config.MinPlayDurationSeconds);

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
                    var validEpisodes = series.GetRecursiveChildren(null)
                        .Where(c => c.GetBaseItemKind() == Jellyfin.Data.Enums.BaseItemKind.Episode)
                        .ToList();

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
                    if (episodeDates.Any()) lastPlayed = episodeDates.Max();
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

            var totalSize = mappedItems.Sum(x => x.Size);

            return new JellyfinGraveyardAnalytics.Models.LeastWatchedResponse
            {
                Items = mappedItems.OrderByDescending(x => x.Size).Take(limit).ToList(),
                TotalWastedSize = FormatBytes(totalSize)
            };
        }

        public JellyfinGraveyardAnalytics.Models.LeastWatchedResponse GetLivingItems(
            string mediaType,
            string? mediaSearch,
            int limit,
            Dictionary<string, int> playCounts,
            Dictionary<string, HashSet<string>> itemViewers,
            Dictionary<string, DateTime> lastPlayedDates)
        {
            var playDurations = _repository.GetItemPlayDurations(_config.MinPlayDurationSeconds);

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
                    var children = series.GetRecursiveChildren(null);
                    var validEpisodes = children.Where(c => c.Path != null).ToList();

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
                    if (episodeDates.Any()) lastPlayed = episodeDates.Max();
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

                TotalWastedSize = FormatBytes(totalLivingSize)
            };
        }

        public JellyfinGraveyardAnalytics.Models.VisitorResponse GetVisitorActivity(string endDateString, int weeksBack)
        {
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

            var (rawData, truncated) = _repository.GetRawPlaybackActivity(
                startDate, endDate, _config.GuestbookRowLimit);

            var sessions = new List<JellyfinGraveyardAnalytics.Models.VisitorSession>();

            foreach (var row in rawData)
            {
                string userId = row.UserId?.ToString().Replace("-", "") ?? "Unknown";
                string visitorName = userDictionary.TryGetValue(userId, out string? name) ? name : "Deleted User";

                activeUserIds.Add(userId);

                long durationSeconds = row.PlayDuration != null ? (long)row.PlayDuration : 0;

                if (!userWatchTimes.ContainsKey(visitorName)) userWatchTimes[visitorName] = 0;
                userWatchTimes[visitorName] += durationSeconds;

                var ts = System.TimeSpan.FromSeconds(durationSeconds);
                string formattedDuration = $"{(int)System.Math.Floor(ts.TotalHours):D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";

                System.DateTime rowDate;
                System.DateTime.TryParse(row.DateCreated?.ToString(), out rowDate);

                sessions.Add(new JellyfinGraveyardAnalytics.Models.VisitorSession
                {
                    Time = System.DateTime.SpecifyKind(rowDate, System.DateTimeKind.Utc)
                        .ToLocalTime().ToString("MMM dd, yyyy - h:mm tt"),
                    Visitor = visitorName,
                    Subject = row.ItemName?.ToString() ?? "Unknown",
                    Type = row.ItemType?.ToString() ?? "Unknown",
                    Client = row.ClientName?.ToString() ?? "Unknown",
                    Device = row.DeviceName?.ToString() ?? "Unknown",
                    Method = row.PlaybackMethod?.ToString() ?? "DirectPlay",
                    Duration = formattedDuration,
                    IsTranscode = row.PlaybackMethod?.ToString().Contains("Transcode", System.StringComparison.OrdinalIgnoreCase) == true
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
                RowLimit = _config.GuestbookRowLimit
            };
        }
    }
}
