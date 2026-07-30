using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using JellyfinGraveyardAnalytics.Configuration;
using JellyfinGraveyardAnalytics.Models;

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
        /// Ceiling on history pages walked in one request. Backstop only: with a real date
        /// window and the maximum page size, reaching it takes 4,000 sessions in the
        /// requested window. Hitting it is logged, never silent.
        /// </summary>
        private const int MaxHistoryPages = 40;

        /// <summary>
        /// Page size requested explicitly rather than taking Tracearr's default of 25.
        /// 100 is the documented maximum — anything larger is rejected with 400, which is
        /// what the earlier "500 and 1000 come back unparseable" note was seeing.
        /// </summary>
        private const int HistoryPageSize = 100;

        /// <summary>
        /// Tracearr interprets the date window in this zone. The local engine bounds its
        /// window in UTC, so both engines must agree or the same timeframe returns different
        /// sessions depending on which one is enabled. An unrecognized zone is a 400.
        /// </summary>
        private const string HistoryTimeZone = "UTC";

        private readonly HttpClient _httpClient;
        private readonly IPluginConfigurationSource _configSource;
        private readonly ILogger<TracearrService> _logger;

        public TracearrService(
            HttpClient httpClient,
            IPluginConfigurationSource configSource,
            ILogger<TracearrService> logger)
        {
            _httpClient = httpClient;
            _configSource = configSource;
            _logger = logger;
        }

        /// <summary>
        /// Read per use, not captured: the URL and key can change between two requests, and a
        /// typed HttpClient is transient so this object may well outlive nothing at all.
        /// </summary>
        private PluginConfiguration Config => _configSource.Current;

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

        /// <summary>
        /// Builds a <c>history</c> query for one page of a date window.
        /// </summary>
        /// <remarks>
        /// Tracearr's history endpoint takes <c>startDate</c> / <c>endDate</c> / <c>timezone</c>
        /// and has <b>no</b> <c>weeksBack</c> parameter. Every earlier caller here sent
        /// <c>weeksBack</c>, which Tracearr silently ignored: unknown query keys are dropped
        /// rather than rejected, so the requests returned 200 and the plugin walked the
        /// server's <i>entire</i> history every time while believing it had asked for a
        /// window. Both callers now go through this one builder so that cannot recur.
        /// </remarks>
        private static string BuildHistoryEndpoint(DateTime startDate, DateTime endDate, int page)
        {
            // Day-granular, which is all the endpoint accepts; it expands them to the start
            // and end of the day in the given zone.
            var start = startDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var end = endDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

            return $"history?startDate={start}&endDate={end}&timezone={HistoryTimeZone}"
                + $"&page={page}&pageSize={HistoryPageSize}";
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
                // One row is enough to prove the endpoint answers; the probe must not pull a
                // page of real history just to say "connected".
                request = BuildRequest("history?page=1&pageSize=1");
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

        /// <summary>
        /// Fetches the Guestbook and normalizes it into the same <see cref="VisitorResponse"/>
        /// the local engine returns, so the dashboard renders one table instead of sniffing
        /// the payload shape to pick a renderer.
        /// </summary>
        /// <param name="rowLimit">
        /// Ceiling on sessions returned, matching the local engine's <c>GuestbookRowLimit</c>
        /// so the same timeframe cannot serialize a different amount of JSON depending on
        /// which engine is enabled.
        /// </param>
        /// <remarks>
        /// Ghosts are left empty here: they are Jellyfin users with no activity, and Tracearr
        /// knows who watched rather than who has an account. The controller fills them.
        /// </remarks>
        public async Task<VisitorResponse> GetVisitorHistoryAsync(
            string endDate,
            int weeksBack,
            int rowLimit,
            CancellationToken cancellationToken = default)
        {
            var windowEnd = ParseEndDate(endDate);
            var windowStart = windowEnd.AddDays(-7 * Math.Max(weeksBack, 1));

            _logger.LogInformation(
                "Fetching The Guestbook via Tracearr Engine for {Start:yyyy-MM-dd}..{End:yyyy-MM-dd}.",
                windowStart,
                windowEnd);

            var sessions = new List<VisitorSession>();
            var watchTimes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            bool truncated = false;
            int currentPage = 1;
            int totalPages = 1;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var endpoint = BuildHistoryEndpoint(windowStart, windowEnd, currentPage);
                var jsonResponse = await SendTracearrRequestAsync(endpoint, cancellationToken).ConfigureAwait(false);

                using var document = JsonDocument.Parse(jsonResponse);
                var root = document.RootElement;

                if (root.TryGetProperty("meta", out var metaObj))
                {
                    totalPages = ReadTotalPages(metaObj);
                }

                if (root.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in dataArray.EnumerateArray())
                    {
                        if (sessions.Count >= rowLimit)
                        {
                            truncated = true;
                            break;
                        }

                        var session = MapSession(row, out var visitor, out var durationSeconds);
                        sessions.Add(session);

                        watchTimes.TryGetValue(visitor, out var running);
                        watchTimes[visitor] = running + durationSeconds;
                    }
                }

                if (truncated)
                {
                    break;
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
                    "Guestbook truncated: stopped at {Rows} rows / page {Page} of {TotalPages} for {Start:yyyy-MM-dd}..{End:yyyy-MM-dd}. The leaderboard covers only the returned rows.",
                    sessions.Count,
                    currentPage,
                    totalPages,
                    windowStart,
                    windowEnd);
            }

            return new VisitorResponse
            {
                Sessions = sessions,
                Leaderboard = BuildLeaderboard(watchTimes),
                Truncated = truncated,
                RowLimit = rowLimit
            };
        }

        /// <summary>
        /// Maps one Tracearr history row onto a <see cref="VisitorSession"/>. Field names are
        /// the ones measured against the live server; the two <c>*Ms</c> progress fields
        /// arrive as JSON <em>strings</em> while <c>durationMs</c> arrives as a number, which
        /// is why every read goes through <see cref="ReadInt64"/> rather than
        /// <c>GetInt64()</c>.
        /// </summary>
        private static VisitorSession MapSession(JsonElement row, out string visitor, out long durationSeconds)
        {
            visitor = "Unknown Entity";
            if (row.TryGetProperty("user", out var user)
                && user.ValueKind == JsonValueKind.Object
                && user.TryGetProperty("username", out var username))
            {
                visitor = ReadString(username) ?? visitor;
            }

            var mediaTitle = ReadString(row, "mediaTitle");
            var showTitle = ReadString(row, "showTitle");
            var subject = string.IsNullOrEmpty(showTitle)
                ? (mediaTitle ?? "Unknown Relic")
                : $"{showTitle} - {mediaTitle}";

            durationSeconds = ReadInt64(row, "durationMs") / 1000;
            var elapsed = TimeSpan.FromSeconds(durationSeconds);

            var startedAt = ReadString(row, "startedAt");
            var time = TryParseUtc(startedAt, out var started)
                ? started.ToLocalTime().ToString("MMM dd, yyyy - h:mm tt", System.Globalization.CultureInfo.CurrentCulture)
                : "Unknown Date";

            var progressMs = ReadInt64(row, "progressMs");
            var totalDurationMs = ReadInt64(row, "totalDurationMs");
            double? progressPercent = totalDurationMs > 0
                ? Math.Clamp((double)progressMs / totalDurationMs * 100d, 0d, 100d)
                : null;

            bool? watched = row.TryGetProperty("watched", out var watchedProp)
                && (watchedProp.ValueKind == JsonValueKind.True || watchedProp.ValueKind == JsonValueKind.False)
                ? watchedProp.GetBoolean()
                : null;

            var decision = ReadString(row, "videoDecision");

            return new VisitorSession
            {
                Time = time,
                Visitor = visitor,
                Subject = subject,
                Type = TitleCase(ReadString(row, "mediaType")),
                Device = ReadString(row, "device") ?? "Unknown Vessel",
                Player = ReadString(row, "player") ?? string.Empty,
                Method = string.IsNullOrEmpty(decision)
                    ? "Unknown"
                    : decision.ToUpperInvariant(),
                Duration = $"{(int)Math.Floor(elapsed.TotalHours):D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}",
                IsTranscode = row.TryGetProperty("isTranscode", out var transcode)
                    && transcode.ValueKind == JsonValueKind.True,
                ProgressPercent = progressPercent,
                Watched = watched
            };
        }

        private static List<VisitorLeaderboardEntry> BuildLeaderboard(Dictionary<string, long> watchTimes)
        {
            return watchTimes
                .OrderByDescending(kvp => kvp.Value)
                .Take(3)
                .Select(kvp =>
                {
                    var ts = TimeSpan.FromSeconds(kvp.Value);
                    return new VisitorLeaderboardEntry
                    {
                        Name = kvp.Key,
                        TotalTime = $"{(int)Math.Floor(ts.TotalHours)}h {ts.Minutes}m"
                    };
                })
                .ToList();
        }

        /// <summary>
        /// Reads an integer that Tracearr may send as either a number or a string —
        /// <c>durationMs</c> is a number while <c>progressMs</c> and <c>totalDurationMs</c>
        /// are strings on the same row. Returns 0 for anything unreadable.
        /// </summary>
        private static long ReadInt64(JsonElement parent, string property)
        {
            if (!parent.TryGetProperty(property, out var value))
            {
                return 0;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number => value.TryGetInt64(out var number) ? number : 0,
                JsonValueKind.String => long.TryParse(
                    value.GetString(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed) ? parsed : 0,
                _ => 0
            };
        }

        private static string? ReadString(JsonElement parent, string property)
            => parent.TryGetProperty(property, out var value) ? ReadString(value) : null;

        private static string? ReadString(JsonElement value)
            => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        /// <summary>
        /// Tracearr sends <c>mediaType</c> lowercase ("episode"); the local engine's
        /// <c>ItemType</c> is already title-cased, and the shared table shows them in the
        /// same cell.
        /// </summary>
        private static string TitleCase(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "Unknown";
            }

            return char.ToUpperInvariant(value[0]) + value.Substring(1);
        }

        /// <summary>
        /// The four media-tab aggregates, built from Tracearr history.
        /// </summary>
        /// <remarks>
        /// <paramref name="weeksBack"/> is converted to a real <c>startDate</c>/<c>endDate</c>
        /// window — Tracearr has no <c>weeksBack</c> parameter and silently ignored the one
        /// this used to send.
        /// </remarks>
        public async Task<(Dictionary<string, int> playCounts, Dictionary<string, HashSet<string>> itemViewers, Dictionary<string, DateTime> lastPlayedDates, Dictionary<string, long> playDurations)> GetTracearrPlaybackStatsAsync(int weeksBack = 52, CancellationToken cancellationToken = default)
        {
            var windowEnd = DateTime.UtcNow.Date;
            var windowStart = windowEnd.AddDays(-7 * Math.Max(weeksBack, 1));

            _logger.LogInformation(
                "Aggregating The Morgue via Tracearr history for {Start:yyyy-MM-dd}..{End:yyyy-MM-dd}.",
                windowStart,
                windowEnd);

            var playCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var itemViewers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var lastPlayedDates = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            // Fourth aggregate, "Time Played". The Chapel and Sanctuary previously read this
            // from the local Playback Reporting database even when Tracearr was the engine,
            // so a single row mixed two sources — and with no local database present those
            // two tabs failed outright.
            var playDurations = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            // D2's floor. The local engine pushes MinPlayDurationSeconds into all three of
            // these aggregates at the SQL level; Tracearr has no equivalent filter, so it is
            // applied here per row. Without it the same library reads as more-watched purely
            // because the Tracearr engine is enabled, and a two-second false start counts as
            // a play that keeps an item out of the Morgue.
            var minPlaySeconds = Config.MinPlayDurationSeconds;
            int belowThreshold = 0;

            int currentPage = 1;
            int totalPages = 1;
            bool truncated = false;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var endpoint = BuildHistoryEndpoint(windowStart, windowEnd, currentPage);
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
                            if (!string.IsNullOrEmpty(path) && path.StartsWith("/Items/", StringComparison.Ordinal))
                            {
                                var parts = path.Split('/');
                                if (parts.Length > 2) itemId = parts[2]; // Grabs the ID segment
                            }
                        }

                        if (string.IsNullOrEmpty(itemId)) continue;

                        // 1b. Apply the play threshold. durationMs is the elapsed session
                        // time — the analog of Playback Reporting's PlayDuration — not the
                        // item runtime, which is totalDurationMs.
                        var playSeconds = ReadInt64(item, "durationMs") / 1000;
                        if (playSeconds < minPlaySeconds)
                        {
                            belowThreshold++;
                            continue;
                        }

                        // 2. Tally Play Counts
                        if (!playCounts.ContainsKey(itemId)) playCounts[itemId] = 0;
                        playCounts[itemId]++;

                        // 2b. Tally Time Played
                        playDurations.TryGetValue(itemId, out var runningSeconds);
                        playDurations[itemId] = runningSeconds + playSeconds;

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
                            // Through the same parse as everything else here (finding 30). A bare
                            // TryParse honoured the trailing Z by converting to *local* time, so
                            // this engine put a Local value in the field the local engine fills
                            // with UTC — same column, two meanings, differing by the offset.
                            if (TryParseUtc(startedProp.GetString(), out var dt))
                            {
                                if (!lastPlayedDates.TryGetValue(itemId, out var existing) || dt > existing)
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
                    "Tracearr history was truncated at the {MaxPages}-page cap ({TotalPages} pages available for {Start:yyyy-MM-dd}..{End:yyyy-MM-dd}). Play counts and viewer reach are undercounted for older activity, so items may read as zero-play in the Morgue.",
                    MaxHistoryPages,
                    totalPages,
                    windowStart,
                    windowEnd);
            }

            if (belowThreshold > 0)
            {
                _logger.LogDebug(
                    "Discarded {Count} Tracearr session(s) shorter than the {Threshold}s play threshold.",
                    belowThreshold,
                    minPlaySeconds);
            }

            return (playCounts, itemViewers, lastPlayedDates, playDurations);
        }

        /// <summary>
        /// Parses the dashboard's end-date string the same way the local engine does — as
        /// UTC, falling back to today — so switching engines does not shift the window.
        /// </summary>
        private static DateTime ParseEndDate(string endDate)
            => TryParseUtc(endDate, out var parsed) ? parsed.Date : DateTime.UtcNow.Date;

        /// <summary>
        /// Parses a Tracearr timestamp to a <see cref="DateTimeKind.Utc"/> value. One helper for
        /// all three callers, because a bare <c>DateTime.TryParse</c> returns Local for an
        /// offset-bearing string and Unspecified for one without, and both end up in fields the
        /// local engine fills with UTC.
        /// </summary>
        private static bool TryParseUtc(string? value, out DateTime utc)
            => DateTime.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out utc);

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
