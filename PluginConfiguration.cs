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
    /// Gets or sets a value indicating whether suggestions should only be shown
    /// after the final episode of a series.
    /// </summary>
    public bool ShowOnSeriesFinaleOnly { get; set; } = true;
}
