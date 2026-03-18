using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JellyfinGraveyardAnalytics.Services;

namespace JellyfinGraveyardAnalytics.Api
{
    [ApiController]
    [Route("/GraveyardAnalytics/Tracearr")]
    public class TracearrController : ControllerBase
    {
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
        /// </summary>
        [HttpPost("Webhook/Condemn")]
        [AllowAnonymous] // Tracearr won't have a Jellyfin user token, so we allow anonymous...
        public IActionResult ReceiveCondemnWebhook([FromBody] TracearrWebhookPayload payload, [FromQuery] string token)
        {
            // ...but we secure it by requiring the Tracearr API Key in the query string!
            if (token != Plugin.Instance.Configuration.TracearrApiKey)
            {
                _logger.LogWarning("Unauthorized webhook attempt from Tracearr.");
                return Unauthorized();
            }

            _logger.LogInformation("Received Webhook from Tracearr to condemn media ID: {MediaId}", payload.MediaId);

            // TODO: Call your existing SQLite logic here to move the item to The Chapel!
            // _sqliteService.CondemnItem(payload.MediaId);

            return Ok(new { status = "Condemned" });
        }
    }

    public class TracearrWebhookPayload
    {
        public string? MediaId { get; set; }
        public string? EventType { get; set; }
    }
}
