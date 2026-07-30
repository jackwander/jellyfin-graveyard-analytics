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
        /// <summary>
        /// What the client is told when something fails unexpectedly. Exception text can carry
        /// filesystem paths and connection strings, so it goes to the log and nowhere else.
        /// </summary>
        private const string GenericFailure = "The request failed. Check the Jellyfin server log for details.";

        /// <summary>
        /// The one diagnostic worth telling the admin, because it is actionable and
        /// names no path. Returned as a literal so no exception text is ever echoed.
        /// </summary>
        private const string PlaybackUnavailableMessage =
            "The Playback Reporting plugin database was not found. Install Playback Reporting first.";

        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<GraveyardAnalyticsController> _logger;
        private readonly ICollectionManager _collectionManager;
        private readonly IUserManager _userManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IProviderManager _providerManager;
        private readonly TracearrService _tracearrService;
        private readonly PlaybackStatsProvider _playbackStats;

        public GraveyardAnalyticsController(
            TracearrService tracearrService,
            PlaybackStatsProvider playbackStats,
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
            _playbackStats = playbackStats;
        }

        private AnalyticsService NewAnalyticsService()
            => new AnalyticsService(
                Plugin.Instance.Repository,
                _libraryManager,
                Plugin.UserDataManager,
                _userManager,
                Plugin.Instance.Configuration);

        [HttpGet("LeastWatched")]
        public async Task<IActionResult> GetLeastWatched([FromQuery] string mediaType, [FromQuery] string? mediaSearch, [FromQuery] int limit = 20, [FromQuery] bool includeBarelyTouched = false, [FromQuery] bool includeUnverifiable = false, CancellationToken cancellationToken = default)
        {
            try
            {
                var stats = await _playbackStats.GetAsync(cancellationToken).ConfigureAwait(false);
                return Ok(NewAnalyticsService().GetLeastWatchedItems(
                    mediaType,
                    mediaSearch,
                    limit,
                    stats,
                    includeBarelyTouched,
                    includeUnverifiable));
            }
            catch (PlaybackDataUnavailableException ex)
            {
                _logger.LogWarning(ex, "Could not assemble The Morgue: playback data is unavailable.");
                return BadRequest(new { message = PlaybackUnavailableMessage });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to assemble The Morgue.");
                return StatusCode(500, new { message = GenericFailure });
            }
        }

        [HttpGet("Living")]
        public async Task<IActionResult> GetLiving([FromQuery] string mediaType = "All", [FromQuery] string? mediaSearch = null, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
        {
            try
            {
                var stats = await _playbackStats.GetAsync(cancellationToken).ConfigureAwait(false);
                return Ok(NewAnalyticsService().GetLivingItems(mediaType, mediaSearch, limit, stats));
            }
            catch (PlaybackDataUnavailableException ex)
            {
                _logger.LogWarning(ex, "Could not assemble The Sanctuary: playback data is unavailable.");
                return BadRequest(new { message = PlaybackUnavailableMessage });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to assemble The Sanctuary.");
                return StatusCode(500, new { message = GenericFailure });
            }
        }

        [HttpGet("Purgatory")]
        public async Task<IActionResult> GetPurgatory([FromQuery] string mediaType = "All", [FromQuery] string? mediaSearch = null, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
        {
            try
            {
                var stats = await _playbackStats.GetAsync(cancellationToken).ConfigureAwait(false);
                return Ok(NewAnalyticsService().GetPurgatoryItems(mediaType, mediaSearch, limit, stats));
            }
            catch (PlaybackDataUnavailableException ex)
            {
                _logger.LogWarning(ex, "Could not assemble The Chapel: playback data is unavailable.");
                return BadRequest(new { message = PlaybackUnavailableMessage });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to assemble The Chapel.");
                return StatusCode(500, new { message = GenericFailure });
            }
        }

        [HttpPost("LastRites/{itemId}")]
        public IActionResult PerformLastRites(string itemId)
        {
            try
            {
                if (!Guid.TryParse(itemId, out Guid parsedId)) return BadRequest(new { message = "Invalid ID format." });

                var item = _libraryManager.GetItemById(parsedId);
                if (item == null) return NotFound(new { message = "Subject not found in the records." });

                // This deletes files from disk, so it is restricted to subjects that have
                // already been condemned. Anything else has to be Condemned first.
                if (item.Tags == null || !item.Tags.Contains("[Chapel]", StringComparer.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Refused Last Rites for {ItemName}: the subject does not carry the [Chapel] tag.", item.Name);
                    return BadRequest(new { message = "Only subjects condemned to The Chapel can receive Last Rites." });
                }

                _logger.LogWarning("Performing Last Rites for: {ItemName} ({ItemPath})", item.Name, item.Path);

                var options = new MediaBrowser.Controller.Library.DeleteOptions
                {
                    DeleteFileLocation = true
                };

                _libraryManager.DeleteItem(item, options, true);

                // The item is gone from the library, so every cached aggregate that mentions
                // it is stale. Without this the tab it was deleted from keeps showing it for
                // up to the TTL.
                _playbackStats.Invalidate();
                return Ok(new { message = "Subject has been laid to rest." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to perform Last Rites on {ItemId}", itemId);
                return StatusCode(500, new { message = "The rite failed. Check the Jellyfin server log for details." });
            }
        }

        [HttpPost("Condemn/{itemId}")]
        public async Task<IActionResult> CondemnSubject(string itemId)
        {
            try
            {
                if (!Guid.TryParse(itemId, out Guid parsedId)) return BadRequest(new { message = "Invalid ID format." });

                var item = _libraryManager.GetItemById(parsedId);
                if (item == null) return NotFound(new { message = "Subject not found." });

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

                // Moves the item between the Morgue and the Chapel, so both tabs' cached
                // views are now wrong.
                _playbackStats.Invalidate();
                return Ok(new { message = "Subject condemned to The Chapel." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to condemn {ItemId}.", itemId);
                return StatusCode(500, new { message = GenericFailure });
            }
        }

        [HttpPost("Pardon/{itemId}")]
        public async Task<IActionResult> PardonSubject(string itemId)
        {
            try
            {
                if (!Guid.TryParse(itemId, out Guid parsedId)) return BadRequest(new { message = "Invalid ID format." });

                var item = _libraryManager.GetItemById(parsedId);
                if (item == null) return NotFound(new { message = "Subject not found." });

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

                // The reverse of Condemn, and stale in the same two places.
                _playbackStats.Invalidate();
                return Ok(new { message = "Subject has been pardoned." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pardon {ItemId}.", itemId);
                return StatusCode(500, new { message = GenericFailure });
            }
        }

        [HttpGet("Visitors")]
        public async Task<IActionResult> GetVisitors([FromQuery] string endDate, [FromQuery] int weeksBack = 1, CancellationToken cancellationToken = default)
        {
            // Everything is inside the try, including the config read and the
            // Repository construction it triggers — both can throw on a bad data path.
            try
            {
                var config = Plugin.Instance.Configuration;

                if (config.EnableTracearr)
                {
                    try
                    {
                        var tracearrData = await _tracearrService
                            .GetVisitorHistoryAsync(endDate, weeksBack, config.GuestbookRowLimit, cancellationToken)
                            .ConfigureAwait(false);

                        // Ghosts are Jellyfin users with nothing in the window, so they can
                        // only be derived here — Tracearr knows who watched, not who exists
                        // on this server. Matching is by username across two namespaces;
                        // a Tracearr-side rename shows its old owner as a ghost.
                        var active = new HashSet<string>(
                            tracearrData.Sessions.Select(s => s.Visitor),
                            StringComparer.OrdinalIgnoreCase);

                        tracearrData.Ghosts = _userManager.Users
                            .Select(u => u.Username)
                            .Where(name => !active.Contains(name))
                            .ToList();

                        return Ok(tracearrData);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to read visitor history from the Tracearr engine.");
                        return BadRequest(new { message = "The Tracearr engine could not be reached. Check the URL and API key in Settings." });
                    }
                }

                if (!System.IO.File.Exists(Plugin.Instance.Repository.PlaybackDbPath))
                {
                    return BadRequest(new { message = PlaybackUnavailableMessage });
                }

                var service = new AnalyticsService(Plugin.Instance.Repository, _libraryManager, Plugin.UserDataManager, _userManager, Plugin.Instance.Configuration);
                return Ok(service.GetVisitorActivity(endDate, weeksBack));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get visitor activity.");
                return StatusCode(500, new { message = GenericFailure });
            }
        }

        [HttpGet("Ping")]
        [AllowAnonymous]
        public IActionResult Ping()
        {
            return Ok(new { message = "The Graveyard Controller is ALIVE!" });
        }
    }

    /// <summary>
    /// Raised when the playback data source is missing or unusable, so the caller can be
    /// told something actionable instead of the generic failure. Its message is logged, not
    /// echoed — the client always receives a literal from the controller.
    /// </summary>
    public sealed class PlaybackDataUnavailableException : Exception
    {
        public PlaybackDataUnavailableException(string message)
            : base(message)
        {
        }
    }
}
