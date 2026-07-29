using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using JellyfinGraveyardAnalytics.Configuration;

namespace JellyfinGraveyardAnalytics.Services
{
    public enum TracearrConnectionStatus
    {
        Success,
        NotConfigured,
        Unreachable,
        Unauthorized,
        UnexpectedResponse
    }

    /// <summary>
    /// Outcome of a connection test. <paramref name="StatusCode"/> is null when no HTTP
    /// response was obtained at all.
    /// </summary>
    public sealed record TracearrConnectionCheck(TracearrConnectionStatus Status, int? StatusCode);

    public class TracearrService
    {
        /// <summary>
        /// Ceiling on history pages walked in one request. Tracearr pages are small and the
        /// caller asks for a year at a time, so an uncapped loop is unbounded work on a busy
        /// server. Hitting the cap is logged, never silent.
        /// </summary>
        private const int MaxHistoryPages = 40;

        private readonly HttpClient _httpClient;
        private readonly ILogger<TracearrService> _logger;

        public TracearrService(HttpClient httpClient, ILogger<TracearrService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        private PluginConfiguration Config => Plugin.Instance.Configuration;

        /// <summary>
        /// Builds an authenticated request. <paramref name="endpoint"/> is relative to
        /// <c>/api/v1/public/</c> and must NOT repeat that prefix.
        /// </summary>
        private HttpRequestMessage BuildRequest(string endpoint)
        {
            if (!Config.EnableTracearr || string.IsNullOrWhiteSpace(Config.TracearrUrl) || string.IsNullOrWhiteSpace(Config.TracearrApiKey))
            {
                throw new InvalidOperationException("Tracearr is not configured or enabled.");
            }

            var url = $"{Config.TracearrUrl.TrimEnd('/')}/api/v1/public/{endpoint}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", $"Bearer {Config.TracearrApiKey}");
            return request;
        }

        private async Task<string> SendTracearrRequestAsync(string endpoint, CancellationToken cancellationToken)
        {
            var request = BuildRequest(endpoint);

            try
            {
                var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to communicate with Tracearr API at {Url}", request.RequestUri);
                throw;
            }
        }

        /// <summary>
        /// Tests the connection against the endpoint the plugin actually depends on.
        /// The previous probe used <c>system/status</c>, which Tracearr does not serve — it
        /// answers 404, so the test failed even when the URL and key were correct.
        /// Reports *why* it failed so the Settings page can stop blaming the API key for a
        /// wrong URL.
        /// </summary>
        public async Task<TracearrConnectionCheck> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            HttpRequestMessage request;
            try
            {
                request = BuildRequest("history?weeksBack=1&page=1");
            }
            catch (InvalidOperationException)
            {
                return new TracearrConnectionCheck(TracearrConnectionStatus.NotConfigured, null);
            }

            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return new TracearrConnectionCheck(TracearrConnectionStatus.Success, (int)response.StatusCode);
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _logger.LogWarning("Tracearr rejected the configured API key ({StatusCode}).", (int)response.StatusCode);
                    return new TracearrConnectionCheck(TracearrConnectionStatus.Unauthorized, (int)response.StatusCode);
                }

                _logger.LogWarning(
                    "Tracearr answered {StatusCode} for {Url}.", (int)response.StatusCode, request.RequestUri);
                return new TracearrConnectionCheck(TracearrConnectionStatus.UnexpectedResponse, (int)response.StatusCode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not reach Tracearr at {Url}.", request.RequestUri);
                return new TracearrConnectionCheck(TracearrConnectionStatus.Unreachable, null);
            }
        }

        // Fetching the Guestbook (Visitor History)
        public async Task<object?> GetVisitorHistoryAsync(string endDate, int weeksBack, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Fetching The Guestbook (History) via Tracearr Engine...");

            var endpoint = $"history?weeksBack={weeksBack}&endDate={Uri.EscapeDataString(endDate)}";

            var jsonResponse = await SendTracearrRequestAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<object>(jsonResponse);
        }

        public async Task<(Dictionary<string, int> playCounts, Dictionary<string, HashSet<string>> itemViewers, Dictionary<string, DateTime> lastPlayedDates)> GetTracearrPlaybackStatsAsync(int weeksBack = 52, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Aggregating The Morgue data via Tracearr History...");

            var playCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var itemViewers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var lastPlayedDates = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            int currentPage = 1;
            int totalPages = 1;
            bool truncated = false;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Bare path: the base URL already ends in /api/v1/public.
                var endpoint = $"history?weeksBack={weeksBack}&page={currentPage}";
                var jsonResponse = await SendTracearrRequestAsync(endpoint, cancellationToken).ConfigureAwait(false);

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
                    totalPages = ReadTotalPages(metaObj);
                }

                currentPage++;

                if (currentPage > MaxHistoryPages && currentPage <= totalPages)
                {
                    truncated = true;
                    break;
                }
            } while (currentPage <= totalPages);

            if (truncated)
            {
                _logger.LogWarning(
                    "Tracearr history was truncated at the {MaxPages}-page cap ({TotalPages} pages available for {WeeksBack} weeks). Play counts and viewer reach are undercounted for older activity.",
                    MaxHistoryPages,
                    totalPages,
                    weeksBack);
            }

            return (playCounts, itemViewers, lastPlayedDates);
        }

        /// <summary>
        /// Reads a page count out of a Tracearr <c>meta</c> block, tolerating a missing,
        /// zero or non-numeric page size rather than dividing by it.
        /// </summary>
        private static int ReadTotalPages(JsonElement meta)
        {
            if (meta.TryGetProperty("totalPages", out var explicitPages)
                && explicitPages.ValueKind == JsonValueKind.Number
                && explicitPages.TryGetInt32(out var pages))
            {
                return pages > 0 ? pages : 1;
            }

            int total = meta.TryGetProperty("total", out var t)
                && t.ValueKind == JsonValueKind.Number
                && t.TryGetInt32(out var totalValue) ? totalValue : 0;

            int pageSize = meta.TryGetProperty("pageSize", out var ps)
                && ps.ValueKind == JsonValueKind.Number
                && ps.TryGetInt32(out var pageSizeValue) ? pageSizeValue : 0;

            if (total <= 0 || pageSize <= 0)
            {
                return 1;
            }

            return (int)Math.Ceiling((double)total / pageSize);
        }
    }
}
