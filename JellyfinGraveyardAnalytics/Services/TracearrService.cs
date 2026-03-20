using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using JellyfinGraveyardAnalytics.Configuration;
using MediaBrowser.Controller.Library;

namespace JellyfinGraveyardAnalytics.Services
{
    public class TracearrService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<TracearrService> _logger;

        public TracearrService(HttpClient httpClient, ILogger<TracearrService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        private PluginConfiguration Config => Plugin.Instance.Configuration;

        private async Task<string> SendTracearrRequestAsync(string endpoint)
        {
            if (!Config.EnableTracearr || string.IsNullOrWhiteSpace(Config.TracearrUrl) || string.IsNullOrWhiteSpace(Config.TracearrApiKey))
            {
                throw new InvalidOperationException("Tracearr is not configured or enabled.");
            }

            var url = $"{Config.TracearrUrl.TrimEnd('/')}/api/v1/public/{endpoint}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);

            request.Headers.Add("Authorization", $"Bearer {Config.TracearrApiKey}");

            try
            {
                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to communicate with Tracearr API at {Url}", url);
                throw;
            }
        }

        public async Task<object?> GetStaleMediaAsync(string mediaType, string? mediaSearch, int limit)
        {
            _logger.LogInformation("Fetching The Morgue data via Tracearr Engine...");

            var endpoint = $"media/stale?type={mediaType}&limit={limit}";
            if (!string.IsNullOrWhiteSpace(mediaSearch))
            {
                endpoint += $"&search={Uri.EscapeDataString(mediaSearch)}";
            }

            var jsonResponse = await SendTracearrRequestAsync(endpoint);
            return JsonSerializer.Deserialize<object>(jsonResponse);
        }

        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                await SendTracearrRequestAsync("system/status");
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Fetching the Guestbook (Visitor History)
        public async Task<object?> GetVisitorHistoryAsync(string endDate, int weeksBack)
        {
            _logger.LogInformation("Fetching The Guestbook (History) via Tracearr Engine...");

            var endpoint = $"history?weeksBack={weeksBack}&endDate={Uri.EscapeDataString(endDate)}";

            var jsonResponse = await SendTracearrRequestAsync(endpoint);
            return JsonSerializer.Deserialize<object>(jsonResponse);
        }

        public async Task<JellyfinGraveyardAnalytics.Models.LeastWatchedResponse> GetStaleMediaAlignedAsync(
            string mediaType,
            string? mediaSearch,
            int limit,
            ILibraryManager libraryManager)
        {
            _logger.LogInformation("Fetching The Morgue data via Tracearr Engine and aligning with Jellyfin Library...");

            var endpoint = $"media/stale?type={mediaType}&limit={limit}";
            if (!string.IsNullOrWhiteSpace(mediaSearch))
            {
                endpoint += $"&search={Uri.EscapeDataString(mediaSearch)}";
            }

            var jsonResponse = await SendTracearrRequestAsync(endpoint);
            var mappedItems = new List<JellyfinGraveyardAnalytics.Models.LeastWatchedItem>();
            long wasteBytes = 0;

            try
            {
                using var document = JsonDocument.Parse(jsonResponse);
                var root = document.RootElement;

                JsonElement itemsArray = root.TryGetProperty("data", out var dataProp) ? dataProp : root;

                foreach (var item in itemsArray.EnumerateArray())
                {
                    string jellyfinId = "";
                    if (item.TryGetProperty("mediaId", out var idProp)) jellyfinId = idProp.GetString() ?? "";

                    if (string.IsNullOrEmpty(jellyfinId) && item.TryGetProperty("thumbPath", out var thumbProp))
                    {
                        var path = thumbProp.GetString();
                        if (!string.IsNullOrEmpty(path) && path.Contains("/Items/"))
                        {
                            var parts = path.Split('/');
                            if (parts.Length > 2) jellyfinId = parts[2];
                        }
                    }

                    int playCount = item.TryGetProperty("playCount", out var pcProp) ? pcProp.GetInt32() : 0;
                    int viewers = item.TryGetProperty("uniqueViewers", out var uvProp) ? uvProp.GetInt32() : 0;

                    DateTime? lastPlayed = null;
                    if (item.TryGetProperty("lastPlayed", out var lpProp) && lpProp.ValueKind == JsonValueKind.String)
                    {
                        if (DateTime.TryParse(lpProp.GetString(), out var dt)) lastPlayed = dt;
                    }

                    if (!string.IsNullOrEmpty(jellyfinId) && Guid.TryParse(jellyfinId, out var parsedGuid))
                    {
                        var jellyfinItem = libraryManager.GetItemById(parsedGuid);
                        if (jellyfinItem != null)
                        {
                            if (jellyfinItem.Tags != null && jellyfinItem.Tags.Contains("[Chapel]", StringComparer.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            long totalSize = 0;
                            if (jellyfinItem is MediaBrowser.Controller.Entities.TV.Series series)
                            {
                                var children = series.GetRecursiveChildren(null).Where(c => c.Path != null);
                                totalSize = children.Sum(c => c.Size ?? 0);
                            }
                            else
                            {
                                totalSize = jellyfinItem.Size ?? 0;
                            }

                            mappedItems.Add(new JellyfinGraveyardAnalytics.Models.LeastWatchedItem
                            {
                                MediaId = jellyfinItem.Id.ToString(),
                                Name = jellyfinItem.Name ?? "Unknown",
                                Type = jellyfinItem is MediaBrowser.Controller.Entities.Movies.Movie ? "Movie" : "Series",
                                Path = jellyfinItem.Path ?? string.Empty,
                                PlayCount = playCount,
                                UniqueViewers = viewers,
                                Size = totalSize,
                                FormattedSize = FormatBytes(totalSize),
                                LastPlayed = lastPlayed
                            });

                            if (playCount == 0)
                            {
                                wasteBytes += totalSize;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse Tracearr stale media JSON into LeastWatchedResponse.");
            }

            return new JellyfinGraveyardAnalytics.Models.LeastWatchedResponse
            {
                Items = mappedItems.OrderBy(x => x.UniqueViewers).ThenBy(x => x.PlayCount).ThenByDescending(x => x.Size).Take(limit).ToList(),
                TotalWastedSize = FormatBytes(wasteBytes)
            };
        }

        private string FormatBytes(long bytes)
        {
            string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
            if (bytes == 0) return "0 B";
            long place = Convert.ToInt64(Math.Floor(Math.Log(bytes, 1024)));
            double num = Math.Round(bytes / Math.Pow(1024, place), 1);
            return $"{num} {suf[place]}";
        }

        public async Task<(Dictionary<string, int> playCounts, Dictionary<string, HashSet<string>> itemViewers, Dictionary<string, DateTime> lastPlayedDates)> GetTracearrPlaybackStatsAsync(int weeksBack = 52)
        {
            _logger.LogInformation("Aggregating The Morgue data via Tracearr History...");

            var playCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var itemViewers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var lastPlayedDates = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            int currentPage = 1;
            int totalPages = 1;

            do
            {
                var endpoint = $"public/history?weeksBack={weeksBack}&page={currentPage}";
                var jsonResponse = await SendTracearrRequestAsync(endpoint);

                using var document = JsonDocument.Parse(jsonResponse);
                var root = document.RootElement;

                if (root.TryGetProperty("data", out var dataArray))
                {
                    foreach (var item in dataArray.EnumerateArray())
                    {
                        string itemId = "";
                        if (item.TryGetProperty("thumbPath", out var thumbProp))
                        {
                            var path = thumbProp.GetString();
                            if (!string.IsNullOrEmpty(path) && path.StartsWith("/Items/"))
                            {
                                var parts = path.Split('/');
                                if (parts.Length > 2) itemId = parts[2]; // Grabs the ID segment
                            }
                        }

                        if (string.IsNullOrEmpty(itemId)) continue;

                        // 2. Tally Play Counts
                        if (!playCounts.ContainsKey(itemId)) playCounts[itemId] = 0;
                        playCounts[itemId]++;

                        // 3. Tally Unique Viewers
                        string username = "Unknown";
                        if (item.TryGetProperty("user", out var userObj) && userObj.TryGetProperty("username", out var userProp))
                        {
                            username = userProp.GetString() ?? "Unknown";
                        }
                        if (!itemViewers.ContainsKey(itemId)) itemViewers[itemId] = new HashSet<string>();
                        itemViewers[itemId].Add(username);

                        // 4. Track Last Played Date
                        if (item.TryGetProperty("startedAt", out var startedProp) && startedProp.ValueKind == JsonValueKind.String)
                        {
                            if (DateTime.TryParse(startedProp.GetString(), out var dt))
                            {
                                if (!lastPlayedDates.ContainsKey(itemId) || dt > lastPlayedDates[itemId])
                                {
                                    lastPlayedDates[itemId] = dt;
                                }
                            }
                        }
                    }
                }

                // Handle Pagination
                if (root.TryGetProperty("meta", out var metaObj))
                {
                    int total = metaObj.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
                    int pageSize = metaObj.TryGetProperty("pageSize", out var ps) ? ps.GetInt32() : 25;
                    totalPages = (int)Math.Ceiling((double)total / pageSize);
                }

                currentPage++;

            } while (currentPage <= totalPages);

            return (playCounts, itemViewers, lastPlayedDates);
        }
    }
}
