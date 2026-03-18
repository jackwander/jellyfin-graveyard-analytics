using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using JellyfinGraveyardAnalytics.Configuration;

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
    }
}
