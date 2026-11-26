using System.Collections.Concurrent;

namespace Jellyfin.Plugin.PostPlaySuggestions.Services;

/// <summary>
/// Keeps track of the sessions where the web client is currently showing the suggestions panel.
/// The native playback watcher consults this registry so it does not start a second
/// playback on clients that already render the panel.
/// </summary>
public sealed class WebPanelRegistry
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, DateTime> _active = new(StringComparer.Ordinal);

    /// <summary>
    /// Marks a session as active or inactive.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="active">Whether the panel is active.</param>
    public void Set(string sessionId, bool active)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        if (active)
        {
            _active[sessionId] = DateTime.UtcNow;
        }
        else
        {
            _active.TryRemove(sessionId, out _);
        }
    }

    /// <summary>
    /// Determines whether the session currently has an active web panel.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <returns>True when the panel is active.</returns>
    public bool IsActive(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (!_active.TryGetValue(sessionId, out var timestamp))
        {
            return false;
        }

        if (DateTime.UtcNow - timestamp <= Lifetime)
        {
            return true;
        }

        _active.TryRemove(sessionId, out _);
        return false;
    }
}
