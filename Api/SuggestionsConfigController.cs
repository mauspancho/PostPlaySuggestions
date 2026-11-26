using System.Security.Claims;
using Jellyfin.Plugin.PostPlaySuggestions;
using Jellyfin.Plugin.PostPlaySuggestions.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
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

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly CreditsResolver _creditsResolver;
    private readonly WebPanelRegistry _panelRegistry;
    private readonly ILogger<SuggestionsConfigController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SuggestionsConfigController"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="creditsResolver">Credits resolver.</param>
    /// <param name="panelRegistry">Registry of sessions with an active web panel.</param>
    /// <param name="logger">Logger.</param>
    public SuggestionsConfigController(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        CreditsResolver creditsResolver,
        WebPanelRegistry panelRegistry,
        ILogger<SuggestionsConfigController> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _creditsResolver = creditsResolver;
        _panelRegistry = panelRegistry;
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
            nativeAutoPlay = configuration.NativeAutoPlay,
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
    public async Task<ActionResult<object>> GetFinale([FromQuery] Guid itemId)
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

        var item = _libraryManager.GetItemById(itemId);
        if (item is not Episode episode || episode.SeriesId == Guid.Empty)
        {
            return new { isFinale = false, reason = "not-an-episode" };
        }

        var info = await _creditsResolver
            .GetFinaleInfoAsync(episode, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        return new
        {
            isFinale = info.IsFinale,
            seriesId = episode.SeriesId,
            finalSeason = info.FinalSeason,
            finalEpisodeId = info.FinalEpisodeId,
            finalEpisodeName = info.FinalEpisodeName
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

        var info = await _creditsResolver
            .GetCreditsAsync(item, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        var startTicks = info.StartTicks;

        return new
        {
            itemId,
            hasCredits = startTicks.HasValue,
            source = info.Source,
            startTicks = startTicks ?? 0,
            startSeconds = startTicks.HasValue
                ? startTicks.Value / (double)TimeSpan.TicksPerSecond
                : (double?)null,
            runTimeTicks = item.RunTimeTicks ?? 0,
            isEpisode = item is Episode
        };
    }

    /// <summary>
    /// Records whether a web client is currently showing the suggestions panel.
    /// The native playback watcher uses this to avoid starting a second playback
    /// on clients that already render the panel.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="active">Whether the panel is active.</param>
    /// <returns>Empty response.</returns>
    [Authorize]
    [HttpPost("Panel")]
    public ActionResult ReportPanel([FromQuery] string? sessionId, [FromQuery] bool active)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _panelRegistry.Set(sessionId, active);
        }

        return NoContent();
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
