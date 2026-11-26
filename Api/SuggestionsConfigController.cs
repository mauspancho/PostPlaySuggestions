using System.Security.Claims;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.PostPlaySuggestions;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PostPlaySuggestions.Api;

/// <summary>
/// Lightweight endpoints consumed by the Jellyfin Web client.
/// </summary>
[ApiController]
[Route("PostPlaySuggestions")]
public sealed class SuggestionsConfigController : ControllerBase
{
    private const string JsResource = "Jellyfin.Plugin.PostPlaySuggestions.Web.postplay-suggestions.js";
    private const string CssResource = "Jellyfin.Plugin.PostPlaySuggestions.Web.postplay-suggestions.css";

    private static readonly Regex CreditsChapterPattern = new(
        "end\\s*credits|endcredit|credits|cr[eé]ditos|g[eé]n[eé]rique|abspann|nachspann",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _mediaSegmentManager;
    private readonly IChapterManager _chapterManager;
    private readonly ILogger<SuggestionsConfigController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SuggestionsConfigController"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSegmentManager">Media segment manager.</param>
    /// <param name="chapterManager">Chapter manager.</param>
    /// <param name="logger">Logger.</param>
    public SuggestionsConfigController(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IMediaSegmentManager mediaSegmentManager,
        IChapterManager chapterManager,
        ILogger<SuggestionsConfigController> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _mediaSegmentManager = mediaSegmentManager;
        _chapterManager = chapterManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current plugin configuration for the web client.
    /// </summary>
    /// <returns>Configuration payload.</returns>
    [AllowAnonymous]
    [HttpGet("Config")]
    public ActionResult<object> GetConfig()
    {
        var configuration = PostPlaySuggestionsPlugin.Instance?.Configuration ?? new PluginConfiguration();

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        return new
        {
            enabled = configuration.Enabled,
            maxSuggestions = Math.Clamp(configuration.MaxSuggestions, 1, 12),
            triggerSecondsBeforeEnd = Math.Clamp(configuration.TriggerSecondsBeforeEnd, 1, 300),
            showOnSeriesFinaleOnly = configuration.ShowOnSeriesFinaleOnly,
            pluginId = PostPlaySuggestionsPlugin.PluginId.ToString("D"),
            version = PostPlaySuggestionsPlugin.PluginVersion
        };
    }

    /// <summary>
    /// Serves the embedded frontend script.
    /// </summary>
    /// <returns>JavaScript file.</returns>
    [AllowAnonymous]
    [HttpGet("Script")]
    [HttpGet("postplay-suggestions.js")]
    public IActionResult GetScript()
    {
        return GetEmbeddedResource(JsResource, "application/javascript; charset=utf-8");
    }

    /// <summary>
    /// Serves the embedded frontend stylesheet.
    /// </summary>
    /// <returns>CSS file.</returns>
    [AllowAnonymous]
    [HttpGet("Styles")]
    [HttpGet("postplay-suggestions.css")]
    public IActionResult GetStyles()
    {
        return GetEmbeddedResource(CssResource, "text/css; charset=utf-8");
    }

    /// <summary>
    /// Gets the item currently playing in the caller's web session.
    /// </summary>
    /// <param name="deviceId">Optional device identifier.</param>
    /// <returns>Current playback payload, or an empty payload when nothing is playing.</returns>
    [Authorize]
    [HttpGet("NowPlaying")]
    public ActionResult<object> GetNowPlaying([FromQuery] string? deviceId)
    {
        var userId = GetUserId(User);
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

        var sessions = _sessionManager.GetSessions(userId, deviceId, 600, null, false);
        if (sessions.Count == 0 && !string.IsNullOrWhiteSpace(deviceId))
        {
            sessions = _sessionManager.GetSessions(userId, null, 600, null, false);
        }

        var session = sessions
            .Where(candidate => candidate.NowPlayingItem is not null)
            .OrderByDescending(candidate => candidate.LastPlaybackCheckIn)
            .FirstOrDefault();

        if (session?.NowPlayingItem is null)
        {
            return new { itemId = (Guid?)null };
        }

        var item = session.NowPlayingItem;
        var primaryImageTag = string.Empty;
        if (item.ImageTags is not null && item.ImageTags.TryGetValue(ImageType.Primary, out var tag))
        {
            primaryImageTag = tag ?? string.Empty;
        }

        return new
        {
            sessionId = session.Id,
            deviceId = session.DeviceId,
            itemId = item.Id,
            itemType = item.Type.ToString(),
            name = item.Name,
            seriesId = item.SeriesId,
            seriesName = item.SeriesName,
            seasonId = item.SeasonId,
            seasonName = item.SeasonName,
            indexNumber = item.IndexNumber,
            parentIndexNumber = item.ParentIndexNumber,
            runTimeTicks = item.RunTimeTicks,
            positionTicks = session.PlayState?.PositionTicks,
            isPaused = session.PlayState?.IsPaused ?? false,
            productionYear = item.ProductionYear,
            communityRating = item.CommunityRating,
            overview = item.Overview,
            primaryImageTag,
            parentPrimaryImageTag = item.ParentPrimaryImageTag,
            parentPrimaryImageItemId = item.ParentPrimaryImageItemId
        };
    }

    /// <summary>
    /// Determines whether the supplied episode is the final episode of its series.
    /// </summary>
    /// <param name="itemId">Episode identifier.</param>
    /// <returns>Finale information.</returns>
    [Authorize]
    [HttpGet("Finale")]
    public ActionResult<object> GetFinale([FromQuery] Guid itemId)
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

        var item = _libraryManager.GetItemById(itemId);
        if (item is not Episode episode || episode.SeriesId == Guid.Empty)
        {
            return new { isFinale = false, reason = "not-an-episode" };
        }

        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Episode],
                AncestorIds = [episode.SeriesId],
                IsVirtualItem = false
            })
            .OfType<Episode>()
            .ToList();

        var regularEpisodes = episodes.Where(candidate => (candidate.ParentIndexNumber ?? 0) > 0).ToList();
        if (regularEpisodes.Count == 0)
        {
            regularEpisodes = episodes;
        }

        var finalSeason = regularEpisodes.Count == 0
            ? 0
            : regularEpisodes.Max(candidate => candidate.ParentIndexNumber ?? 0);

        var finalEpisode = regularEpisodes
            .Where(candidate => (candidate.ParentIndexNumber ?? 0) == finalSeason)
            .OrderBy(candidate => candidate.IndexNumber ?? 0)
            .ThenBy(candidate => candidate.IndexNumberEnd ?? 0)
            .LastOrDefault();

        return new
        {
            isFinale = finalEpisode is not null && finalEpisode.Id == episode.Id,
            seriesId = episode.SeriesId,
            finalSeason,
            finalEpisodeId = finalEpisode?.Id,
            finalEpisodeName = finalEpisode?.Name
        };
    }

    /// <summary>
    /// Gets the estimated start of the end credits for an item.
    /// Prefers Jellyfin media segments (Outro) and falls back to chapter markers.
    /// </summary>
    /// <param name="itemId">Item identifier.</param>
    /// <returns>Credits information.</returns>
    [Authorize]
    [HttpGet("Credits")]
    public async Task<ActionResult<object>> GetCredits([FromQuery] Guid itemId)
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var runTimeTicks = item.RunTimeTicks ?? 0;
        long? startTicks = null;
        var source = "none";

        try
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);
            var segments = await _mediaSegmentManager
                .GetSegmentsAsync(item, [MediaSegmentType.Outro], libraryOptions, false)
                .ConfigureAwait(false);

            var outro = segments
                .Where(segment => segment.StartTicks > 0
                    && (runTimeTicks <= 0 || segment.StartTicks < runTimeTicks - TimeSpan.TicksPerSecond))
                .OrderBy(segment => segment.StartTicks)
                .FirstOrDefault();

            if (outro is not null)
            {
                startTicks = outro.StartTicks;
                source = "segment";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[PostPlaySuggestions] Could not read media segments for {ItemId}", itemId);
        }

        if (startTicks is null)
        {
            try
            {
                var chapters = _chapterManager.GetChapters(itemId);
                var ordered = chapters
                    .Where(chapter => chapter.StartPositionTicks > 0)
                    .OrderBy(chapter => chapter.StartPositionTicks)
                    .ToList();

                if (ordered.Count > 0)
                {
                    var named = ordered.FirstOrDefault(chapter => IsCreditsChapterName(chapter.Name));
                    if (named is not null
                        && (runTimeTicks <= 0 || named.StartPositionTicks >= runTimeTicks * 0.6))
                    {
                        startTicks = named.StartPositionTicks;
                        source = "chapter-name";
                    }
                    else if (runTimeTicks > 0)
                    {
                        var last = ordered[^1];
                        var isEpisode = item is Episode;
                        var minimumChapters = isEpisode ? 3 : 4;
                        var minimumRatio = isEpisode ? 0.85 : 0.80;
                        var maximumTail = isEpisode
                            ? Math.Max(5 * TimeSpan.TicksPerMinute, (long)(runTimeTicks * 0.10))
                            : 15 * TimeSpan.TicksPerMinute;

                        if (ordered.Count >= minimumChapters
                            && last.StartPositionTicks >= runTimeTicks * minimumRatio
                            && last.StartPositionTicks < runTimeTicks - TimeSpan.TicksPerSecond
                            && runTimeTicks - last.StartPositionTicks <= maximumTail)
                        {
                            startTicks = last.StartPositionTicks;
                            source = "chapter-last";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[PostPlaySuggestions] Could not read chapters for {ItemId}", itemId);
            }
        }

        return new
        {
            itemId,
            hasCredits = startTicks.HasValue,
            source,
            startTicks = startTicks ?? 0,
            startSeconds = startTicks.HasValue
                ? startTicks.Value / (double)TimeSpan.TicksPerSecond
                : (double?)null,
            runTimeTicks,
            isEpisode = item is Episode
        };
    }

    private static bool IsCreditsChapterName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) && CreditsChapterPattern.IsMatch(name);
    }

    private IActionResult GetEmbeddedResource(string resourceName, string contentType)
    {
        var stream = typeof(PostPlaySuggestionsPlugin).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=300";
        return File(stream, contentType);
    }

    private static Guid GetUserId(ClaimsPrincipal principal)
    {
        foreach (var claim in principal.Claims)
        {
            if ((claim.Type.Contains("UserId", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(claim.Type, ClaimTypes.NameIdentifier, StringComparison.OrdinalIgnoreCase))
                && Guid.TryParse(claim.Value, out var userId))
            {
                return userId;
            }
        }

        return Guid.Empty;
    }
}
