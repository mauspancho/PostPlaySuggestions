using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PostPlaySuggestions;

/// <summary>
/// Plugin configuration.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the plugin is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of suggestions to show.
    /// </summary>
    public int MaxSuggestions { get; set; } = 6;

    /// <summary>
    /// Gets or sets how many seconds before the end the suggestions should appear.
    /// </summary>
    public int TriggerSecondsBeforeEnd { get; set; } = 20;

    /// <summary>
    /// Gets or sets the runtime percentage used to estimate the end credits start
    /// when the item has no media segments and no usable chapter markers.
    /// </summary>
    public int FallbackCreditsPercent { get; set; } = 94;

    /// <summary>
    /// Gets or sets a value indicating whether suggestions should only be shown
    /// after the final episode of a series.
    /// </summary>
    public bool ShowOnSeriesFinaleOnly { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether native clients (Android TV, iOS, native
    /// mobile players, ...) should automatically start the best suggestion when the
    /// end credits begin. Native clients cannot render the web panel.
    /// </summary>
    public bool NativeAutoPlay { get; set; } = true;
}
