using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PostPlaySuggestions.Services;

/// <summary>
/// Result of the end-credits detection for an item.
/// </summary>
/// <param name="StartTicks">Start position of the credits, in ticks.</param>
/// <param name="Source">Detection source: segment, chapter-name, chapter-last, estimate or none.</param>
public sealed record CreditsInfo(long? StartTicks, string Source);

/// <summary>
/// Information about the final episode of a series.
/// </summary>
/// <param name="IsFinale">Whether the episode is the last one of the series.</param>
/// <param name="FinalSeason">Final season number.</param>
/// <param name="FinalEpisodeId">Identifier of the final episode.</param>
/// <param name="FinalEpisodeName">Name of the final episode.</param>
public sealed record FinaleInfo(bool IsFinale, int FinalSeason, Guid? FinalEpisodeId, string? FinalEpisodeName);

/// <summary>
/// Shared end-credits and series-finale detection used by the web API and native playback watcher.
/// </summary>
public sealed class CreditsResolver
{
    private static readonly Regex CreditsChapterPattern = new(
        "end\\s*credits?|endcredit|closing\\s*credits?|end\\s*titles?|credits?|cr[eé]ditos?(\\s*finales)?|t[ií]tulos?\\s*de\\s*cr[eé]ditos?|g[eé]n[eé]rique|abspann|nachspann",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly TimeSpan FinaleCacheLifetime = TimeSpan.FromMinutes(10);

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _mediaSegmentManager;
    private readonly IChapterManager _chapterManager;
    private readonly ILogger<CreditsResolver> _logger;
    private readonly ConcurrentDictionary<Guid, (DateTime Timestamp, FinaleInfo Info)> _finaleCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CreditsResolver"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSegmentManager">Media segment manager.</param>
    /// <param name="chapterManager">Chapter manager.</param>
    /// <param name="logger">Logger.</param>
    public CreditsResolver(
        ILibraryManager libraryManager,
        IMediaSegmentManager mediaSegmentManager,
        IChapterManager chapterManager,
        ILogger<CreditsResolver> logger)
    {
        _libraryManager = libraryManager;
        _mediaSegmentManager = mediaSegmentManager;
        _chapterManager = chapterManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the start of the end credits for an item.
    /// Prefers Jellyfin media segments (Outro), then chapter markers and finally a runtime estimate.
    /// </summary>
    /// <param name="item">Item to inspect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Credits information.</returns>
    public async Task<CreditsInfo> GetCreditsAsync(BaseItem item, CancellationToken cancellationToken)
    {
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
            _logger.LogDebug(ex, "[PostPlaySuggestions] Could not read media segments for {ItemId}", item.Id);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (startTicks is null)
        {
            try
            {
                var chapters = _chapterManager.GetChapters(item.Id);
                var ordered = chapters
                    .Where(chapter => chapter.StartPositionTicks > 0)
                    .OrderBy(chapter => chapter.StartPositionTicks)
                    .ToList();

                if (ordered.Count > 0)
                {
                    var isEpisode = item is Episode;
                    var finalPartStart = runTimeTicks > 0
                        ? (long)(runTimeTicks * 0.6)
                        : TimeSpan.TicksPerSecond;

                    // Look only at the final 40 % of the item so that "Opening Credits"
                    // chapters do not shadow the real end credits chapter.
                    var named = ordered
                        .Where(chapter => chapter.StartPositionTicks >= finalPartStart)
                        .FirstOrDefault(chapter => IsCreditsChapterName(chapter.Name));

                    if (named is not null)
                    {
                        startTicks = named.StartPositionTicks;
                        source = "chapter-name";
                    }
                    else if (runTimeTicks > 0)
                    {
                        var last = ordered[^1];
                        var lastStart = last.StartPositionTicks;
                        var ratio = lastStart / (double)runTimeTicks;
                        var tail = runTimeTicks - lastStart;
                        var maximumTail = isEpisode
                            ? Math.Max(5 * TimeSpan.TicksPerMinute, (long)(runTimeTicks * 0.10))
                            : 15 * TimeSpan.TicksPerMinute;

                        var isLastChapterCredits = lastStart < runTimeTicks - TimeSpan.TicksPerSecond
                            && (isEpisode
                                ? ratio >= 0.92
                                    || (ordered.Count >= 3 && ratio >= 0.85 && tail <= maximumTail)
                                : ratio >= 0.90
                                    || (ordered.Count >= 4 && ratio >= 0.80 && tail <= maximumTail));

                        if (isLastChapterCredits)
                        {
                            startTicks = lastStart;
                            source = "chapter-last";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[PostPlaySuggestions] Could not read chapters for {ItemId}", item.Id);
            }
        }

        if (startTicks is null && runTimeTicks > 5 * TimeSpan.TicksPerMinute)
        {
            // No segment and no usable chapter information: estimate the credits
            // start from a percentage of the runtime so the panel still opens near
            // the end credits instead of only a few seconds before the file ends.
            var configuration = PostPlaySuggestionsPlugin.Instance?.Configuration ?? new PluginConfiguration();
            var percent = item is Episode
                ? 97
                : Math.Clamp(configuration.FallbackCreditsPercent, 80, 99);
            var estimate = (long)(runTimeTicks * (percent / 100.0));

            if (estimate > 0 && estimate < runTimeTicks - TimeSpan.TicksPerSecond)
            {
                startTicks = estimate;
                source = "estimate";
            }
        }

        return new CreditsInfo(startTicks, source);
    }

    /// <summary>
    /// Gets information about the final episode of the series that owns the supplied episode.
    /// </summary>
    /// <param name="episode">Episode to inspect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Finale information.</returns>
    public Task<FinaleInfo> GetFinaleInfoAsync(Episode episode, CancellationToken cancellationToken)
    {
        if (episode.SeriesId == Guid.Empty)
        {
            return Task.FromResult(new FinaleInfo(false, 0, null, null));
        }

        if (_finaleCache.TryGetValue(episode.SeriesId, out var cached)
            && DateTime.UtcNow - cached.Timestamp < FinaleCacheLifetime)
        {
            return Task.FromResult(cached.Info);
        }

        cancellationToken.ThrowIfCancellationRequested();

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

        var info = new FinaleInfo(
            finalEpisode is not null && finalEpisode.Id == episode.Id,
            finalSeason,
            finalEpisode?.Id,
            finalEpisode?.Name);

        _finaleCache[episode.SeriesId] = (DateTime.UtcNow, info);
        return Task.FromResult(info);
    }

    private static bool IsCreditsChapterName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) && CreditsChapterPattern.IsMatch(name);
    }
}
