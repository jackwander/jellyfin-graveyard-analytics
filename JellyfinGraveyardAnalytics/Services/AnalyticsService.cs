using System;
using System.Linq;
using System.Collections.Generic;
using JellyfinAnalyticsPlugin.Database;
using JellyfinAnalyticsPlugin.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Querying;

namespace JellyfinAnalyticsPlugin.Services
{
    public class AnalyticsService
    {
        private readonly Repository _repository;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserDataManager _userDataManager;
        private readonly IUserManager _userManager;

        public AnalyticsService(Repository repository, ILibraryManager libraryManager, IUserDataManager userDataManager, IUserManager userManager)
        {
            _repository = repository;
            _libraryManager = libraryManager;
            _userDataManager = userDataManager;
            _userManager = userManager;
        }

        public JellyfinAnalyticsPlugin.Models.LeastWatchedResponse GetLeastWatchedItems(string mediaType, string? mediaSearch, int limit)
        {
            var playCounts = _repository.GetItemPlayCounts();

            var itemViewers = _repository.GetItemViewers();

            var lastPlayedDates = _repository.GetItemLastPlayedDates();

            var kindList = new List<Jellyfin.Data.Enums.BaseItemKind>();
            if (mediaType == "All")
            {
                kindList.Add(Jellyfin.Data.Enums.BaseItemKind.Movie);
                kindList.Add(Jellyfin.Data.Enums.BaseItemKind.Series);
            }
            else
            {
                if (Enum.TryParse<Jellyfin.Data.Enums.BaseItemKind>(mediaType, true, out var kind))
                    kindList.Add(kind);
                else
                    kindList.Add(Jellyfin.Data.Enums.BaseItemKind.Movie);
            }

            var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                IncludeItemTypes = kindList.ToArray(),
                IsVirtualItem = false,
                Recursive = true,
                ExcludeTags = new[] { "[Chapel]" }
            };

            if (!string.IsNullOrWhiteSpace(mediaSearch))
            {
                query.SearchTerm = mediaSearch;
            }

            var allItems = _libraryManager.GetItemList(query).AsEnumerable();

            if (!string.IsNullOrWhiteSpace(mediaSearch))
            {
                allItems = allItems.Where(i =>
                    i.Name != null &&
                    i.Name.Contains(mediaSearch, StringComparison.OrdinalIgnoreCase));
            }

            var uniqueItems = allItems
              .Where(i => i.Tags == null || !i.Tags.Contains("[Chapel]", StringComparer.OrdinalIgnoreCase))
              .GroupBy(i => (i.Name ?? string.Empty).ToLower().Trim())
              .Select(g => g.First());

            var mappedItems = uniqueItems.Select(item =>
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

                return new JellyfinAnalyticsPlugin.Models.LeastWatchedItem
                {
                    MediaId = item.Id.ToString(),
                    Name = item.Name ?? "Unknown",
                    Type = item is MediaBrowser.Controller.Entities.Movies.Movie ? "Movie" : "Series",
                    Path = item.Path ?? string.Empty,
                    PlayCount = totalPlays,
                    UniqueViewers = uniqueUsers,
                    Size = totalSize,
                    FormattedSize = FormatBytes(totalSize),
                    LastPlayed = lastPlayed
                };
            })
            .Where(x => x != null)
            .ToList();

            long wasteBytes = mappedItems.Where(x => x.PlayCount == 0).Sum(x => x.Size);

            return new JellyfinAnalyticsPlugin.Models.LeastWatchedResponse
            {
                Items = mappedItems
                    .OrderBy(x => x.UniqueViewers)
                    .ThenBy(x => x.PlayCount)
                    .ThenByDescending(x => x.Size)
                    .Take(limit)
                    .ToList(),
                TotalWastedSize = FormatBytes(wasteBytes)
            };
        }

        // Helper to make sizes human-readable
        private string FormatBytes(long bytes)
        {
            string[] Suffix = { "B", "KB", "MB", "GB", "TB" };
            int i;
            double dblSByte = bytes;
            for (i = 0; i < Suffix.Length && bytes >= 1024; i++, bytes /= 1024)
            {
                dblSByte = bytes / 1024.0;
            }
            return $"{dblSByte:0.##} {Suffix[i]}";
        }

        public JellyfinAnalyticsPlugin.Models.LeastWatchedResponse GetPurgatoryItems(string mediaType, string? mediaSearch, int limit)
        {
            var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                IncludeItemTypes = mediaType == "All"
                    ? new[] { Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Series }
                    : new[] { Enum.Parse<Jellyfin.Data.Enums.BaseItemKind>(mediaType, true) },
                IsVirtualItem = false,
                Recursive = true,
                Tags = new[] { "[Chapel]" }
            };

            if (!string.IsNullOrWhiteSpace(mediaSearch))
            {
                query.SearchTerm = mediaSearch;
            }

            var purgatoryItems = _libraryManager.GetItemList(query).AsEnumerable();

            if (!string.IsNullOrWhiteSpace(mediaSearch))
            {
                purgatoryItems = purgatoryItems.Where(i =>
                    i.Name != null &&
                    i.Name.Contains(mediaSearch, StringComparison.OrdinalIgnoreCase));
            }

            var mappedItems = purgatoryItems.Select(item =>
            {
                long itemSize = 0;

                if (item is MediaBrowser.Controller.Entities.TV.Series series)
                {
                    var episodeQuery = new MediaBrowser.Controller.Entities.InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Episode },
                        AncestorIds = new[] { series.Id },
                        IsVirtualItem = false,
                        Recursive = true
                    };

                    var episodes = _libraryManager.GetItemList(episodeQuery);
                    itemSize = episodes.Sum(e => e.Size ?? 0);
                }
                else
                {
                    itemSize = item.Size ?? 0;
                }

                return new JellyfinAnalyticsPlugin.Models.LeastWatchedItem
                {
                    MediaId = item.Id.ToString(),
                    Name = item.Name ?? "Unknown",
                    Type = item is MediaBrowser.Controller.Entities.Movies.Movie ? "Movie" : "Series",
                    Path = item.Path ?? string.Empty,
                    Size = itemSize,
                    FormattedSize = FormatBytes(itemSize)
                };
            }).ToList();

            var totalSize = mappedItems.Sum(x => x.Size);

            return new JellyfinAnalyticsPlugin.Models.LeastWatchedResponse
            {
                Items = mappedItems.OrderByDescending(x => x.Size).Take(limit).ToList(),
                TotalWastedSize = FormatBytes(totalSize)
            };
        }

        public JellyfinAnalyticsPlugin.Models.LeastWatchedResponse GetLivingItems(string mediaType, int limit, string? mediaSearch)
        {
            var playCounts = _repository.GetItemPlayCounts();
            var itemViewers = _repository.GetItemViewers();
            var lastPlayedDates = _repository.GetItemLastPlayedDates();
            var playDurations = _repository.GetItemPlayDurations();

            var kindList = new List<Jellyfin.Data.Enums.BaseItemKind>();
            if (mediaType == "All")
            {
                kindList.Add(Jellyfin.Data.Enums.BaseItemKind.Movie);
                kindList.Add(Jellyfin.Data.Enums.BaseItemKind.Series);
            }
            else
            {
                if (Enum.TryParse<Jellyfin.Data.Enums.BaseItemKind>(mediaType, true, out var kind))
                    kindList.Add(kind);
                else
                    kindList.Add(Jellyfin.Data.Enums.BaseItemKind.Movie);
            }

            var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                IncludeItemTypes = kindList.ToArray(),
                IsVirtualItem = false,
                Recursive = true,
                ExcludeTags = new[] { "[Chapel]" }
            };

            var allItems = _libraryManager.GetItemList(query);

            var uniqueItems = allItems
              .Where(i => string.IsNullOrWhiteSpace(mediaSearch) || (i.Name != null && i.Name.Contains(mediaSearch, StringComparison.OrdinalIgnoreCase)))
              .ToList();

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

                return new JellyfinAnalyticsPlugin.Models.LeastWatchedItem
                {
                    MediaId = item.Id.ToString(),
                    Name = item.Name ?? "Unknown",
                    Type = item is MediaBrowser.Controller.Entities.Movies.Movie ? "Movie" : "Series",
                    Path = item.Path ?? string.Empty,
                    PlayCount = totalPlays,
                    UniqueViewers = uniqueUsers,
                    Size = totalSize,
                    FormattedSize = FormatBytes(totalSize),
                    LastPlayed = lastPlayed,
                    TotalDurationSeconds = totalDurationSeconds,
                    FormattedDuration = formattedDuration
                };
            })
            .Where(x => x != null)
            .Cast<JellyfinAnalyticsPlugin.Models.LeastWatchedItem>()
            .ToList();

            long totalLivingSize = mappedItems.Sum(x => x.Size);

            return new JellyfinAnalyticsPlugin.Models.LeastWatchedResponse
            {
                Items = mappedItems
                    .OrderByDescending(x => x.PlayCount)
                    .Take(limit)
                    .ToList(),

                TotalWastedSize = FormatBytes(totalLivingSize)
            };
        }

        public JellyfinAnalyticsPlugin.Models.VisitorResponse GetVisitorActivity(string endDateString, int weeksBack)
        {
            if (!System.DateTime.TryParse(endDateString, out System.DateTime endDate))
            {
                endDate = System.DateTime.UtcNow;
            }
            endDate = endDate.Date.AddDays(1).AddTicks(-1);
            System.DateTime startDate = endDate.AddDays(-7 * weeksBack).Date;

            var allUsers = _userManager.Users.ToList();
            var userDictionary = allUsers.ToDictionary(u => u.Id.ToString("N"), u => u.Username);
            var activeUserIds = new HashSet<string>();
            var userWatchTimes = new Dictionary<string, long>();

            var rawData = _repository.GetRawPlaybackActivity(startDate, endDate);

            var sessions = new List<JellyfinAnalyticsPlugin.Models.VisitorSession>();

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

                sessions.Add(new JellyfinAnalyticsPlugin.Models.VisitorSession
                {
                    Time = rowDate.ToLocalTime().ToString("MMM dd, yyyy - h:mm tt"),
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
                    return new JellyfinAnalyticsPlugin.Models.VisitorLeaderboardEntry
                    {
                        Name = kvp.Key,
                        TotalTime = $"{(int)System.Math.Floor(ts.TotalHours)}h {ts.Minutes}m"
                    };
                }).ToList();

            return new JellyfinAnalyticsPlugin.Models.VisitorResponse
            {
                Sessions = sessions,
                Ghosts = ghosts,
                Leaderboard = leaderboard
            };
        }
    }
}
