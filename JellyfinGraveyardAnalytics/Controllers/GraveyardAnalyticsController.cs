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
using JellyfinGraveyardAnalytics.Configuration;
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

        /// <summary>
        /// The Chapel collection artwork, embedded rather than fetched. These two used to be
        /// pulled from raw.githubusercontent.com at condemn time, which made the branding
        /// depend on the server having outbound internet and on the repository still being at
        /// that path — and it silently produced an unbranded collection when either failed.
        /// Names are the csproj's <c>EmbeddedResource</c> paths with separators as dots.
        /// </summary>
        private const string ChapelThumbnailResource =
            "JellyfinGraveyardAnalytics.Resources.thechapelcollectionthumbnail.jpg";

        private const string ChapelBackdropResource =
            "JellyfinGraveyardAnalytics.Resources.thechapelcollectionbackdrop.jpg";

        /// <summary>
        /// The public collection paired with the <c>[Chapel]</c> tag. Condemn looks it up (and
        /// creates it), Pardon looks it up to remove from it — spelled out in three places
        /// before, which is one rename away from the two halves disagreeing.
        /// </summary>
        private const string ChapelCollectionName = "Leaving Soon: The Chapel";

        /// <summary>
        /// What viewers read on the Chapel collection. Written once, when Condemn creates the
        /// collection; an admin who edits it afterwards keeps their version.
        /// </summary>
        /// <remarks>
        /// The last sentence used to read "Once the grace period ends, these files will be
        /// exorcised" — which tells a viewer a deadline exists somewhere they could look it up.
        /// None does: Condemn records no timestamp and nothing expires, because Last Rites is a
        /// deliberate act by the server owner. The grace period is the Undertaker's judgement,
        /// so the text now says so. That matters more since the collection can be surfaced on
        /// the home screen, where it is read by everyone rather than by whoever opens it.
        /// </remarks>
        private const string ChapelCollectionOverview =
            "Welcome to The Chapel. The media gathered here has been condemned due to severe "
            + "neglect. These titles have sat unwatched, taking up valuable server space, and are "
            + "currently awaiting their Last Rites. If you wish to save a title from permanent "
            + "deletion, you must watch it immediately. When the Undertaker judges that the grace "
            + "period is over, these files will be exorcised from the server forever.";

        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<GraveyardAnalyticsController> _logger;
        private readonly ICollectionManager _collectionManager;
        private readonly IUserManager _userManager;
        private readonly IProviderManager _providerManager;
        private readonly TracearrService _tracearrService;
        private readonly PlaybackStatsProvider _playbackStats;
        private readonly AnalyticsService _analytics;
        private readonly IPluginConfigurationSource _configSource;

        /// <summary>
        /// Everything arrives through the container now. <see cref="AnalyticsService"/> was
        /// being constructed by hand in four actions, with two of its arguments read from
        /// statics on <c>Plugin</c>, which is why none of this could be exercised without a
        /// running server.
        /// </summary>
        public GraveyardAnalyticsController(
            TracearrService tracearrService,
            PlaybackStatsProvider playbackStats,
            AnalyticsService analytics,
            IPluginConfigurationSource configSource,
            ILibraryManager libraryManager,
            ILogger<GraveyardAnalyticsController> logger,
            ICollectionManager collectionManager,
            IUserManager userManager,
            IProviderManager providerManager)
        {
            _tracearrService = tracearrService;
            _libraryManager = libraryManager;
            _logger = logger;
            _collectionManager = collectionManager;
            _userManager = userManager;
            _providerManager = providerManager;
            _playbackStats = playbackStats;
            _analytics = analytics;
            _configSource = configSource;
        }

        [HttpGet("LeastWatched")]
        public async Task<IActionResult> GetLeastWatched([FromQuery] string mediaType, [FromQuery] string? mediaSearch, [FromQuery] int limit = 20, [FromQuery] bool includeBarelyTouched = false, [FromQuery] bool includeUnverifiable = false, CancellationToken cancellationToken = default)
        {
            try
            {
                var stats = await _playbackStats.GetAsync(cancellationToken).ConfigureAwait(false);
                return Ok(_analytics.GetLeastWatchedItems(
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
                return Ok(_analytics.GetLivingItems(mediaType, mediaSearch, limit, stats));
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
                return Ok(_analytics.GetPurgatoryItems(mediaType, mediaSearch, limit, stats));
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

                    var chapelCollection = FindChapelCollection();

                    if (chapelCollection == null)
                    {
                        chapelCollection = await _collectionManager.CreateCollectionAsync(new MediaBrowser.Controller.Collections.CollectionCreationOptions
                        {
                            Name = ChapelCollectionName,
                            IsLocked = false
                        }).ConfigureAwait(false) as MediaBrowser.Controller.Entities.Movies.BoxSet;

                        if (chapelCollection != null)
                        {
                            chapelCollection.Overview = ChapelCollectionOverview;

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
                        await ApplyChapelImageAsync(
                            chapelCollection,
                            ChapelThumbnailResource,
                            MediaBrowser.Model.Entities.ImageType.Primary).ConfigureAwait(false);
                    }

                    if (chapelCollection is not null && !chapelCollection.HasImage(MediaBrowser.Model.Entities.ImageType.Backdrop, 0))
                    {
                        await ApplyChapelImageAsync(
                            chapelCollection,
                            ChapelBackdropResource,
                            MediaBrowser.Model.Entities.ImageType.Backdrop).ConfigureAwait(false);
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

        /// <summary>
        /// The Chapel collection, or null if it has not been created yet. Both Condemn and
        /// Pardon need it and each spelled the query out; the query is also indexable, so
        /// <c>FirstOrDefault()</c> was enumerating a list it could have subscripted.
        /// </summary>
        private MediaBrowser.Controller.Entities.Movies.BoxSet? FindChapelCollection()
        {
            var matches = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.BoxSet },
                Name = ChapelCollectionName
            });

            return matches.Count > 0
                ? matches[0] as MediaBrowser.Controller.Entities.Movies.BoxSet
                : null;
        }

        /// <summary>
        /// Saves one embedded artwork resource onto the Chapel collection. Branding is
        /// cosmetic, so a failure here is logged and swallowed rather than failing the
        /// condemn that the admin actually asked for — that was the old behaviour too, and
        /// it is the reason this is a separate method instead of an inline try/catch pair.
        /// </summary>
        private async Task ApplyChapelImageAsync(
            MediaBrowser.Controller.Entities.Movies.BoxSet collection,
            string resourceName,
            MediaBrowser.Model.Entities.ImageType imageType)
        {
            try
            {
                using var imageStream = typeof(GraveyardAnalyticsController).Assembly
                    .GetManifestResourceStream(resourceName);

                if (imageStream is null)
                {
                    // Only reachable if the csproj stops embedding the file or the resource is
                    // renamed, so name it: a silent unbranded collection is what this replaced.
                    _logger.LogError("Chapel artwork {Resource} is missing from the plugin assembly.", resourceName);
                    return;
                }

                await _providerManager.SaveImage(
                    collection,
                    imageStream,
                    "image/jpeg",
                    imageType,
                    null,
                    CancellationToken.None
                ).ConfigureAwait(false);

                _logger.LogInformation("Applied {ImageType} artwork to The Chapel.", imageType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply {ImageType} branding to The Chapel.", imageType);
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

                    var chapelCollection = FindChapelCollection();

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
            // Everything is inside the try, including the config read: it reaches
            // Plugin.Instance, which is not guaranteed to be there.
            try
            {
                var config = _configSource.Current;

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

                        tracearrData.Ghosts = Services.UserManagerCompat.AllUsers(_userManager)
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

                // The missing-database check lives in the service now, thrown as the same
                // exception the media tabs already answer 400 for.
                return Ok(_analytics.GetVisitorActivity(endDate, weeksBack));
            }
            catch (PlaybackDataUnavailableException ex)
            {
                _logger.LogWarning(ex, "Could not assemble The Guestbook: playback data is unavailable.");
                return BadRequest(new { message = PlaybackUnavailableMessage });
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
}
