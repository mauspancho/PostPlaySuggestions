using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PostPlaySuggestions;

/// <summary>
/// Jellyfin Post Play Suggestions plugin entry point.
/// </summary>
public sealed class PostPlaySuggestionsPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Stable plugin identifier.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("f5051302-9a1f-417f-902d-c4e94faaa7fd");

    /// <summary>
    /// Plugin version used for cache busting and diagnostics.
    /// </summary>
    public const string PluginVersion = "0.4.0.0";

    /// <summary>
    /// Initializes a new instance of the <see cref="PostPlaySuggestionsPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public PostPlaySuggestionsPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Post Play Suggestions";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
        "Muestra sugerencias estilo Netflix al terminar películas o el último episodio de una serie.";

    /// <summary>
    /// Gets the active plugin instance.
    /// </summary>
    public static PostPlaySuggestionsPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "PostPlaySuggestions",
                DisplayName = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Web.configPage.html",
                    GetType().Namespace),
                MenuIcon = "movie",
                EnableInMainMenu = false
            }
        ];
    }
}
