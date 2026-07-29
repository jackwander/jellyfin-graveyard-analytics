using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JellyfinGraveyardAnalytics.Services;

namespace JellyfinGraveyardAnalytics.Api
{
    [ApiController]
    [Route("/GraveyardAnalytics/Tracearr")]
    [Authorize(Policy = "RequiresElevation")] // Admin-only by default; the webhook opts out explicitly.
    public class TracearrController : ControllerBase
    {
        /// <summary>
        /// Header carrying the Tracearr API key. A header keeps the secret out of
        /// access logs, which a query string does not.
        /// </summary>
        private const string WebhookTokenHeader = "X-Tracearr-Token";

        private readonly TracearrService _tracearrService;
        private readonly ILogger<TracearrController> _logger;

        public TracearrController(TracearrService tracearrService, ILogger<TracearrController> logger)
        {
            _tracearrService = tracearrService;
            _logger = logger;
        }

        /// <summary>
        /// Tests the connection to the Tracearr server.
        /// URL: GET /GraveyardAnalytics/Tracearr/Ping
        /// </summary>
        [HttpGet("Ping")]
        [Authorize(Policy = "RequiresElevation")] // Only admins can trigger this
        public async Task<IActionResult> PingTracearr()
        {
            var isSuccess = await _tracearrService.TestConnectionAsync();
            if (isSuccess)
            {
                return Ok(new { status = "Success", message = "Tracearr connection established." });
            }

            return BadRequest(new { status = "Error", message = "Could not connect to Tracearr. Check your URL and API Key." });
        }

        /// <summary>
        /// WEBHOOK RECEIVER: Tracearr will hit this URL automatically when a rule is triggered.
        /// URL: POST /GraveyardAnalytics/Tracearr/Webhook/Condemn
        /// Not implemented yet — authenticates the caller, then answers 501 rather than
        /// reporting a condemnation that never happened.
        /// </summary>
        [HttpPost("Webhook/Condemn")]
        [AllowAnonymous] // Tracearr won't have a Jellyfin user token, so we allow anonymous...
        public IActionResult ReceiveCondemnWebhook([FromBody] TracearrWebhookPayload? payload)
        {
            // ...but we secure it by requiring the Tracearr API Key in a header.
            var config = Plugin.Instance.Configuration;

            // The key outlives the toggle — savePluginConfig writes it either way — so an
            // integration that has been switched off must not keep an open door behind it.
            if (!config.EnableTracearr)
            {
                _logger.LogWarning("Rejected a Tracearr webhook: the Tracearr engine is disabled.");
                return Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(config.TracearrApiKey))
            {
                _logger.LogWarning(
                    "Rejected a Tracearr webhook: no Tracearr API key is configured, so the endpoint cannot authenticate callers.");
                return Unauthorized();
            }

            var configuredKey = config.TracearrApiKey;

            if (!Request.Headers.TryGetValue(WebhookTokenHeader, out var presented)
                || presented.Count != 1
                || !TokenMatches(presented[0], configuredKey))
            {
                _logger.LogWarning("Unauthorized webhook attempt from Tracearr.");
                return Unauthorized();
            }

            if (payload is null)
            {
                return BadRequest(new { status = "Error", message = "A webhook payload is required." });
            }

            _logger.LogInformation("Received Webhook from Tracearr to condemn media ID: {MediaId}", payload.MediaId);

            // TODO: Call the Condemn logic here to move the item to The Chapel.
            return StatusCode(
                StatusCodes.Status501NotImplemented,
                new { status = "NotImplemented", message = "Webhook-driven condemnation is not wired up yet." });
        }

        /// <summary>
        /// Fixed-time comparison so a caller cannot recover the key one character at a time.
        /// Length is not secret; content is.
        /// </summary>
        private static bool TokenMatches(string? presented, string configured)
        {
            if (string.IsNullOrEmpty(presented))
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(presented),
                Encoding.UTF8.GetBytes(configured));
        }
    }

    public class TracearrWebhookPayload
    {
        public string? MediaId { get; set; }
        public string? EventType { get; set; }
    }
}
