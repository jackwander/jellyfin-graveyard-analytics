/*
 * -----------------------------------------------------------------------
 * Spectral Assistant Disclosure:
 * This file contains C# logic optimized with the assistance of AI.
 * AI was used specifically for code refinement and .NET 9 compatibility.
 * All logic has been reviewed, tested, and verified by the maintainer.
 * -----------------------------------------------------------------------
 */

using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Providers;
using JellyfinGraveyardAnalytics.Services;

namespace JellyfinGraveyardAnalytics.Controllers
{
    [ApiController]
    [Route("/GraveyardAnalytics")]
    [Authorize(Policy = "RequiresElevation")]
    public class GraveyardAnalyticsController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<GraveyardAnalyticsController> _logger;
        private readonly ICollectionManager _collectionManager;
        private readonly IUserManager _userManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IProviderManager _providerManager;
        private readonly TracearrService _tracearrService;

        public GraveyardAnalyticsController(
            TracearrService tracearrService,
            ILibraryManager libraryManager,
            ILogger<GraveyardAnalyticsController> logger,
            ICollectionManager collectionManager,
            IUserManager userManager,
            IHttpClientFactory httpClientFactory,
            IProviderManager providerManager)
        {
            _tracearrService = tracearrService;
            _libraryManager = libraryManager;
            _logger = logger;
            _collectionManager = collectionManager;
            _userManager = userManager;
            _httpClientFactory = httpClientFactory;
            _providerManager = providerManager;
        }

        [HttpGet("LeastWatched")]
        public async Task<IActionResult> GetLeastWatched(
          [FromQuery] string mediaType = "All",
          [FromQuery] string? mediaSearch = null,
          [FromQuery] int limit = 20)
        {
            var config = Plugin.Instance.Configuration;

            if (config.EnableTracearr)
            {
                try
                {
                    // var tracearrData = await _tracearrService.GetStaleMediaAsync(mediaType, mediaSearch, limit);
                    // return Ok(tracearrData);
                      return Ok();
                }
                catch (Exception ex)
                {
                    return BadRequest($"Tracearr Engine Error: {ex.Message}");
                }
            }

            if (!System.IO.File.Exists(Plugin.Instance.Repository.PlaybackDbPath))
            {
                return BadRequest("CRITICAL ERROR: The Playback Reporting plugin database was not found. Please install Playback Reporting first.");
            }

            var service = new AnalyticsService(Plugin.Instance.Repository, _libraryManager, Plugin.UserDataManager, _userManager);
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

                var options = new MediaBrowser.Controller.Library.DeleteOptions
                {
                    DeleteFileLocation = true
                };

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
        public IActionResult GetPurgatory(
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

                    var chapelCollection = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.BoxSet },
                        Name = "Leaving Soon: The Chapel"
                    }).FirstOrDefault() as MediaBrowser.Controller.Entities.Movies.BoxSet;

                    if (chapelCollection == null)
                    {
                        chapelCollection = await _collectionManager.CreateCollectionAsync(new MediaBrowser.Controller.Collections.CollectionCreationOptions
                        {
                            Name = "Leaving Soon: The Chapel",
                            IsLocked = false
                        }).ConfigureAwait(false) as MediaBrowser.Controller.Entities.Movies.BoxSet;

                        if (chapelCollection != null)
                        {
                            chapelCollection.Overview = "Welcome to The Chapel. The media gathered here has been condemned due to severe neglect. These titles have sat unwatched, taking up valuable server space, and are currently awaiting their Last Rites. If you wish to save a title from permanent deletion, you must watch it immediately. Once the grace period ends, these files will be exorcised from the server forever.";

                            var parent = _libraryManager.GetItemById(chapelCollection.ParentId) ?? chapelCollection.GetParent() ?? _libraryManager.RootFolder;

                            await _libraryManager.UpdateItemAsync(
                                chapelCollection,
                                parent,
                                MediaBrowser.Controller.Library.ItemUpdateType.MetadataEdit,
                                CancellationToken.None
                            ).ConfigureAwait(false);
                        }
                    }

                    // --- IMAGE LOGIC ---
                    if (chapelCollection is not null && !chapelCollection.HasImage(MediaBrowser.Model.Entities.ImageType.Primary, 0))
                    {
                      try
                      {
                          using var httpClient = _httpClientFactory.CreateClient();

                          var thumbUrl = "https://raw.githubusercontent.com/jackwander/jellyfin-graveyard-analytics/master/images/thechapelcollectionthumbnail.png";
                          using (var response = await httpClient.GetAsync(thumbUrl).ConfigureAwait(false))
                          {
                              if (response.IsSuccessStatusCode && response.Content is not null)
                              {
                                  using var imageStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                                  await _providerManager.SaveImage(
                                      chapelCollection!,
                                      imageStream,
                                      "image/png",
                                      MediaBrowser.Model.Entities.ImageType.Primary,
                                      null,
                                      CancellationToken.None
                                  ).ConfigureAwait(false);
                              }
                          }

                          _logger.LogInformation("The Chapel has been fully branded with custom iconography.");
                      }
                      catch (Exception ex)
                      {
                          _logger.LogError(ex, "Failed to apply thematic branding to The Chapel.");
                      }
                    }

                    if (chapelCollection is not null && !chapelCollection.HasImage(MediaBrowser.Model.Entities.ImageType.Backdrop, 0))
                    {
                      try
                      {
                          using var httpClient = _httpClientFactory.CreateClient();

                          var backdropUrl = "https://raw.githubusercontent.com/jackwander/jellyfin-graveyard-analytics/master/images/thechapelcollectionbackdrop.png";
                          using (var response = await httpClient.GetAsync(backdropUrl).ConfigureAwait(false))
                          {
                              if (response.IsSuccessStatusCode && response.Content is not null)
                              {
                                  using var imageStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                                  await _providerManager.SaveImage(
                                      chapelCollection!,
                                      imageStream,
                                      "image/png",
                                      MediaBrowser.Model.Entities.ImageType.Backdrop,
                                      null,
                                      CancellationToken.None
                                  ).ConfigureAwait(false);
                              }
                          }

                          _logger.LogInformation("The Chapel has been fully branded with custom iconography.");
                      }
                      catch (Exception ex)
                      {
                          _logger.LogError(ex, "Failed to apply thematic branding to The Chapel.");
                      }
                    }

                    if (chapelCollection != null)
                    {
                        await _collectionManager.AddToCollectionAsync(chapelCollection.Id, new[] { item.Id });
                    }
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
        public IActionResult GetLiving(
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
        public async Task<IActionResult> GetVisitors([FromQuery] string endDate, [FromQuery] int weeksBack = 1)
        {
            var config = Plugin.Instance.Configuration;

            if (config.EnableTracearr)
            {
                try
                {
                    var tracearrData = await _tracearrService.GetVisitorHistoryAsync(endDate, weeksBack);
                    return Ok(tracearrData);
                }
                catch (Exception ex)
                {
                    return BadRequest($"Tracearr Engine Error: {ex.Message}");
                }
            }

            if (!System.IO.File.Exists(Plugin.Instance.Repository.PlaybackDbPath))
            {
                return BadRequest("CRITICAL ERROR: The Playback Reporting plugin database was not found. Please install Playback Reporting first.");
            }
            try
            {
                var service = new AnalyticsService(Plugin.Instance.Repository, _libraryManager, Plugin.UserDataManager, _userManager);
                return Ok(service.GetVisitorActivity(endDate, weeksBack));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get visitor activity.");
                return StatusCode(500, ex.Message);
            }
        }

        [HttpGet("Ping")]
        [AllowAnonymous]
        public IActionResult Ping()
        {
            return Ok(new { message = "The Graveyard Controller is ALIVE!" });
        }
    }
}
