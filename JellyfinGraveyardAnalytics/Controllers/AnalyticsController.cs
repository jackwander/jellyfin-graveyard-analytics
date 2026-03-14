using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using JellyfinAnalyticsPlugin.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Collections;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace JellyfinAnalyticsPlugin.Controllers
{
    [ApiController]
    [Route("Analytics")]
    [Authorize(Policy = "RequiresElevation")]
    public class AnalyticsController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<AnalyticsController> _logger;
        private readonly ICollectionManager _collectionManager;
        private readonly IUserManager _userManager;

        public AnalyticsController(ILibraryManager libraryManager, ILogger<AnalyticsController> logger, ICollectionManager collectionManager, IUserManager userManager)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _collectionManager = collectionManager;
            _userManager = userManager;
        }

        [HttpGet("LeastWatched")]
        public IActionResult GetLeastWatched(
          [FromQuery] string mediaType = "All",
          [FromQuery] string? mediaSearch = null,
          [FromQuery] int limit = 20)
        {
            if (!System.IO.File.Exists(Plugin.Instance.Repository.PlaybackDbPath))
            {
                return BadRequest("CRITICAL ERROR: The Playback Reporting plugin database was not found. Please install Playback Reporting first.");
            }
            var service = new AnalyticsService(Plugin.Instance.Repository, _libraryManager, Plugin.UserDataManager, _userManager);

            // FIX: Pass 'limit' into the service call here!
            return Ok(service.GetLeastWatchedItems(mediaType, mediaSearch, limit));
        }

        [HttpPost("LastRites/{itemId}")]
        public IActionResult PerformLastRites(string itemId)
        {
            try
            {
                var item = _libraryManager.GetItemById(itemId);
                if (item == null) return NotFound("Subject not found in the records.");

                _logger.LogWarning("Performing Last Rites for: {0} ({1})", item.Name, item.Path);

                // Define options with only the core requirement
                var options = new MediaBrowser.Controller.Library.DeleteOptions
                {
                    DeleteFileLocation = true
                    // We removed NotifyChange because it's either named differently
                    // or handled by the third parameter in the method call below.
                };

                // Signature: DeleteItem(BaseItem item, DeleteOptions options, bool notifyObservers)
                _libraryManager.DeleteItem(item, options, true);

                return Ok(new { message = "Subject has been laid to rest." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to perform Last Rites on {0}", itemId);
                return StatusCode(500, "The rite failed: " + ex.Message);
            }
        }

        [HttpGet("Purgatory")]
        public ActionResult<JellyfinAnalyticsPlugin.Models.LeastWatchedResponse> GetPurgatory(
            [FromQuery] string mediaType = "All",
            [FromQuery] string? mediaSearch = null,
            [FromQuery] int limit = 50)
        {
            if (!System.IO.File.Exists(Plugin.Instance.Repository.PlaybackDbPath))
            {
                return BadRequest("CRITICAL ERROR: The Playback Reporting plugin database was not found. Please install Playback Reporting first.");
            }
            try
            {
                var service = new AnalyticsService(Plugin.Instance.Repository, _libraryManager, Plugin.UserDataManager, _userManager);
                return Ok(service.GetPurgatoryItems(mediaType, mediaSearch, limit));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }
        [HttpPost("Condemn/{itemId}")]
        public async Task<IActionResult> CondemnSubject(string itemId)
        {
            try
            {
                if (!Guid.TryParse(itemId, out Guid parsedId)) return BadRequest("Invalid ID format.");

                var item = _libraryManager.GetItemById(parsedId);
                if (item == null) return NotFound("Subject not found.");

                var tags = item.Tags?.ToList() ?? new List<string>();

                if (!tags.Contains("[Chapel]", StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add("[Chapel]");
                    item.Tags = tags.ToArray();

                    var parentItem = item.ParentId != Guid.Empty ? _libraryManager.GetItemById(item.ParentId) : null;
                    await _libraryManager.UpdateItemAsync(item, parentItem!, MediaBrowser.Controller.Library.ItemUpdateType.MetadataEdit, CancellationToken.None);

                    // --- THE NEW COLLECTION LOGIC ---
                    // 1. Find the Chapel Collection (or create it if it doesn't exist)
                    var chapelCollection = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.BoxSet },
                        Name = "Leaving Soon: The Chapel"
                    }).FirstOrDefault();

                    if (chapelCollection == null)
                    {
                        // Create the collection
                        chapelCollection = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                        {
                            Name = "Leaving Soon: The Chapel",
                            IsLocked = false
                        });

                        // NEW: Inject the thematic overview warning
                        chapelCollection.Overview = "Welcome to The Chapel. The media gathered here has been condemned due to severe neglect. These titles have sat unwatched, taking up valuable server space, and are currently awaiting their Last Rites. If you wish to save a title from permanent deletion, you must watch it immediately. Once the grace period ends, these files will be exorcised from the server forever.";

                        // NEW: Save the overview to the Jellyfin database
                        var collectionParent = chapelCollection.ParentId != Guid.Empty ? _libraryManager.GetItemById(chapelCollection.ParentId) : null;
                        await _libraryManager.UpdateItemAsync(chapelCollection, collectionParent!, MediaBrowser.Controller.Library.ItemUpdateType.MetadataEdit, CancellationToken.None);
                    }

                    // 2. Add the item to the collection
                    await _collectionManager.AddToCollectionAsync(chapelCollection.Id, new[] { item.Id });
                }

                return Ok(new { message = "Subject condemned to The Chapel." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        [HttpPost("Pardon/{itemId}")]
        public async Task<IActionResult> PardonSubject(string itemId)
        {
            try
            {
                if (!Guid.TryParse(itemId, out Guid parsedId)) return BadRequest("Invalid ID format.");

                var item = _libraryManager.GetItemById(parsedId);
                if (item == null) return NotFound("Subject not found.");

                var tags = item.Tags?.ToList() ?? new List<string>();

                if (tags.Contains("[Chapel]", StringComparer.OrdinalIgnoreCase))
                {
                    tags.RemoveAll(t => t.Equals("[Chapel]", StringComparison.OrdinalIgnoreCase));
                    item.Tags = tags.ToArray();

                    var parentItem = item.ParentId != Guid.Empty ? _libraryManager.GetItemById(item.ParentId) : null;
                    await _libraryManager.UpdateItemAsync(item, parentItem!, MediaBrowser.Controller.Library.ItemUpdateType.MetadataEdit, CancellationToken.None);

                    // --- REMOVE FROM COLLECTION LOGIC ---
                    var chapelCollection = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.BoxSet },
                        Name = "Leaving Soon: The Chapel"
                    }).FirstOrDefault();

                    if (chapelCollection != null)
                    {
                        await _collectionManager.RemoveFromCollectionAsync(chapelCollection.Id, new[] { item.Id });
                    }
                }

                return Ok(new { message = "Subject has been pardoned." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        [HttpGet("Living")]
        public ActionResult<JellyfinAnalyticsPlugin.Models.LeastWatchedResponse> GetLiving(
          [FromQuery] string mediaType = "All",
          [FromQuery] int limit = 50,
          [FromQuery] string mediaSearch = "")
        {
            if (!System.IO.File.Exists(Plugin.Instance.Repository.PlaybackDbPath))
            {
                return BadRequest("CRITICAL ERROR: The Playback Reporting plugin database was not found. Please install Playback Reporting first.");
            }
            var service = new AnalyticsService(Plugin.Instance.Repository, _libraryManager, Plugin.UserDataManager, _userManager);
            return Ok(service.GetLivingItems(mediaType, limit, mediaSearch));
        }

        [HttpGet("Visitors")]
        public ActionResult<JellyfinAnalyticsPlugin.Models.VisitorResponse> GetVisitors([FromQuery] string endDate, [FromQuery] int weeksBack = 1)
        {
            if (!System.IO.File.Exists(Plugin.Instance.Repository.PlaybackDbPath))
            {
                return BadRequest("CRITICAL ERROR: The Playback Reporting plugin database was not found. Please install Playback Reporting first.");
            }
            try
            {
                // Pass the new _userManager into the Service
                var service = new AnalyticsService(Plugin.Instance.Repository, _libraryManager, Plugin.UserDataManager, _userManager);
                return Ok(service.GetVisitorActivity(endDate, weeksBack));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get visitor activity.");
                return StatusCode(500, ex.Message);
            }
        }
    }
}
