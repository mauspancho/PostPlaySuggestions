using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PostPlaySuggestions.Services;

/// <summary>
/// Watches native playback sessions (Android TV, iOS, native mobile players, ...) and,
/// when the end credits start, starts the best suggestion on that same device.
/// Native clients cannot render the web suggestions panel, so this keeps the
/// post-play experience working on the majority of devices.
/// </summary>
public sealed class PostPlayNativeService : IHostedService, IDisposable
{
    private static readonly TimeSpan TriggeredLifetime = TimeSpan.FromHours(2);

    private readonly ISessionManager _sessionManager;
    private readonly CreditsResolver _creditsResolver;
    private readonly ILibraryManager _libraryManager;
    private readonly ISimilarItemsManager _similarItemsManager;
    private readonly IUserManager _userManager;
    private readonly WebPanelRegistry _panelRegistry;
    private readonly ILogger<PostPlayNativeService> _logger;

    private readonly ConcurrentDictionary<string, DateTime> _triggered = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="PostPlayNativeService"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="creditsResolver">Credits resolver.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="similarItemsManager">Similar items manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="panelRegistry">Registry of sessions with an active web panel.</param>
    /// <param name="logger">Logger.</param>
    public PostPlayNativeService(
        ISessionManager sessionManager,
        CreditsResolver creditsResolver,
        ILibraryManager libraryManager,
        ISimilarItemsManager similarItemsManager,
        IUserManager userManager,
        WebPanelRegistry panelRegistry,
        ILogger<PostPlayNativeService> logger)
    {
        _sessionManager = sessionManager;
        _creditsResolver = creditsResolver;
        _libraryManager = libraryManager;
        _similarItemsManager = similarItemsManager;
        _userManager = userManager;
        _panelRegistry = panelRegistry;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _logger.LogInformation("[PostPlaySuggestions] Native playback watcher started");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _logger.LogInformation("[PostPlaySuggestions] Native playback watcher stopped");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        _ = HandleProgressAsync(e);
    }

    private async Task HandleProgressAsync(PlaybackProgressEventArgs e)
    {
        try
        {
            var configuration = PostPlaySuggestionsPlugin.Instance?.Configuration;
            if (configuration is null || !configuration.Enabled || !configuration.NativeAutoPlay)
            {
                return;
            }

            var session = e.Session;
            if (session is null || e.Item is null || e.IsPaused)
            {
                return;
            }

            if (!IsNativeSession(session))
            {
                return;
            }

            if (!await IsEligibleAsync(e.Item, configuration, CancellationToken.None).ConfigureAwait(false))
            {
                return;
            }

            var position = e.PlaybackPositionTicks ?? 0;
            if (position <= 0)
            {
                return;
            }

            var triggerTicks = await GetTriggerTicksAsync(e.Item, e.MediaInfo, configuration, CancellationToken.None)
                .ConfigureAwait(false);
            if (triggerTicks is null || position < triggerTicks.Value)
            {
                return;
            }

            var playSessionId = string.IsNullOrWhiteSpace(e.PlaySessionId)
                ? session.Id
                : e.PlaySessionId;
            var key = playSessionId + "|" + e.Item.Id;

            PruneTriggered();
            if (_triggered.ContainsKey(key) || !_pending.TryAdd(key, 0))
            {
                return;
            }

            try
            {
                // Give the web client a moment to render its own panel (for the
                // Android app using the web player) before acting.
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

                if (_panelRegistry.IsActive(session.Id))
                {
                    return;
                }

                var user = _userManager.GetUserById(session.UserId);
                if (user is null)
                {
                    return;
                }

                var suggestion = await FindSuggestionAsync(
                        e.Item,
                        user,
                        configuration.MaxSuggestions,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (suggestion is null)
                {
                    _logger.LogDebug(
                        "[PostPlaySuggestions] No native suggestion found for {ItemId}",
                        e.Item.Id);
                    return;
                }

                _triggered[key] = DateTime.UtcNow;

                // The first argument is the controlling session. The same session
                // acts as controller so no additional user remote-control session
                // is needed for a server-initiated playback.
                await _sessionManager.SendPlayCommand(
                        session.Id,
                        session.Id,
                        new PlayRequest
                        {
                            ItemIds = [suggestion.Id],
                            PlayCommand = PlayCommand.PlayNow
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);

                try
                {
                    await _sessionManager.SendGeneralCommand(
                            session.Id,
                            session.Id,
                            new GeneralCommand(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["Header"] = "Post Play Suggestions",
                                ["Text"] = "A continuación: " + (suggestion.Name ?? "sugerencia")
                            })
                            {
                                Name = GeneralCommandType.DisplayMessage
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[PostPlaySuggestions] Could not show the native message");
                }

                _logger.LogInformation(
                    "[PostPlaySuggestions] Native fallback started '{Suggestion}' on {Client} after '{Item}'",
                    suggestion.Name,
                    session.Client,
                    e.Item.Name);
            }
            finally
            {
                _pending.TryRemove(key, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[PostPlaySuggestions] Native playback watcher failed");
        }
    }

    private async Task<long?> GetTriggerTicksAsync(
        BaseItem item,
        MediaBrowser.Model.Dto.BaseItemDto? mediaInfo,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var credits = await _creditsResolver
            .GetCreditsAsync(item, cancellationToken)
            .ConfigureAwait(false);

        if (credits.StartTicks is > 0)
        {
            return credits.StartTicks;
        }

        var runTimeTicks = item.RunTimeTicks ?? mediaInfo?.RunTimeTicks ?? 0;
        if (runTimeTicks <= 0)
        {
            return null;
        }

        var seconds = Math.Clamp(configuration.TriggerSecondsBeforeEnd, 1, 300);
        var trigger = runTimeTicks - (seconds * (long)TimeSpan.TicksPerSecond);
        return trigger > 0 ? trigger : null;
    }

    private async Task<bool> IsEligibleAsync(
        BaseItem item,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (item is Movie)
        {
            return true;
        }

        if (item is not Episode episode)
        {
            return false;
        }

        if (!configuration.ShowOnSeriesFinaleOnly)
        {
            return true;
        }

        var finale = await _creditsResolver
            .GetFinaleInfoAsync(episode, cancellationToken)
            .ConfigureAwait(false);
        return finale.IsFinale;
    }

    private async Task<BaseItem?> FindSuggestionAsync(
        BaseItem current,
        Jellyfin.Database.Implementations.Entities.User user,
        int maxSuggestions,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maxSuggestions, 1, 12);
        var currentSeriesId = current is Episode episode ? episode.SeriesId : Guid.Empty;

        // Prefer content similar to what the user just finished, mirroring the
        // "Items/{id}/Similar" behaviour of the web panel.
        try
        {
            var similar = await _similarItemsManager.GetSimilarItemsAsync(
                    current,
                    [],
                    user,
                    new DtoOptions(true),
                    Math.Max(limit * 2, 20),
                    _libraryManager.GetLibraryOptions(current),
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var candidate in similar)
            {
                if (!IsValidSuggestion(candidate, current.Id, currentSeriesId))
                {
                    continue;
                }

                var playable = TryResolvePlayable(candidate, user);
                if (playable is not null)
                {
                    return playable;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[PostPlaySuggestions] Could not load similar items for {ItemId}", current.Id);
        }

        // Fall back to unwatched recent movies/series, then to any recent item.
        foreach (var played in new bool?[] { false, null })
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                User = user,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                IsVirtualItem = false,
                IsPlayed = played,
                OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
                Limit = Math.Max(limit * 4, 40),
                EnableTotalRecordCount = false
            });

            foreach (var candidate in items)
            {
                if (!IsValidSuggestion(candidate, current.Id, currentSeriesId))
                {
                    continue;
                }

                var playable = TryResolvePlayable(candidate, user);
                if (playable is not null)
                {
                    return playable;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Native clients expect a directly playable item. Series are resolved to their
    /// next unwatched episode (or the first episode) before sending the play command.
    /// </summary>
    private BaseItem? TryResolvePlayable(
        BaseItem candidate,
        Jellyfin.Database.Implementations.Entities.User user)
    {
        if (candidate is Movie || candidate is Episode)
        {
            return candidate;
        }

        if (candidate is not Series series)
        {
            return null;
        }

        var next = FindSeriesEpisode(series, user, unplayedOnly: true)
            ?? FindSeriesEpisode(series, user, unplayedOnly: false);

        if (next is null)
        {
            _logger.LogDebug(
                "[PostPlaySuggestions] Series {SeriesId} has no playable episodes for native playback",
                series.Id);
        }

        return next;
    }

    private BaseItem? FindSeriesEpisode(
        Series series,
        Jellyfin.Database.Implementations.Entities.User user,
        bool unplayedOnly)
    {
        var query = new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = [series.Id],
            IsVirtualItem = false,
            OrderBy =
            [
                (ItemSortBy.ParentIndexNumber, SortOrder.Ascending),
                (ItemSortBy.IndexNumber, SortOrder.Ascending)
            ],
            Limit = 1,
            EnableTotalRecordCount = false
        };

        if (unplayedOnly)
        {
            query.IsPlayed = false;
        }

        return _libraryManager.GetItemList(query).FirstOrDefault();
    }

    private static bool IsValidSuggestion(BaseItem candidate, Guid currentId, Guid currentSeriesId)
    {
        if (candidate.Id == Guid.Empty || candidate.Id == currentId)
        {
            return false;
        }

        if (currentSeriesId != Guid.Empty
            && (candidate.Id == currentSeriesId
                || (candidate is Episode candidateEpisode && candidateEpisode.SeriesId == currentSeriesId)))
        {
            return false;
        }

        if (candidate is not Movie && candidate is not Series)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(candidate.Name);
    }

    private static bool IsNativeSession(SessionInfo session)
    {
        var combined = string.Join(
            " ",
            session.Client,
            session.DeviceName,
            session.DeviceType);

        if (combined.Contains("web", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("tizen", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("webos", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("xbox", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return combined.Contains("android", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("ios", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("apple tv", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("appletv", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("fire tv", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("firetv", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("swiftfin", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("findroid", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("infuse", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("kodi", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("roku", StringComparison.OrdinalIgnoreCase);
    }

    private void PruneTriggered()
    {
        if (_triggered.Count < 500)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - TriggeredLifetime;
        foreach (var entry in _triggered)
        {
            if (entry.Value < cutoff)
            {
                _triggered.TryRemove(entry.Key, out _);
            }
        }
    }
}
