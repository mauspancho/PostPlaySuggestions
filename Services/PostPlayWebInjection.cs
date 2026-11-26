namespace Jellyfin.Plugin.PostPlaySuggestions.Services;

/// <summary>
/// Shared HTML injection helpers.
/// </summary>
public static class PostPlayWebInjection
{
    /// <summary>
    /// Start marker used to avoid duplicate injections.
    /// </summary>
    public const string StartMarker = "<!-- PostPlaySuggestions:start -->";

    /// <summary>
    /// End marker used to avoid duplicate injections.
    /// </summary>
    public const string EndMarker = "<!-- PostPlaySuggestions:end -->";

    /// <summary>
    /// Marker used to detect an already injected page.
    /// </summary>
    public const string ScriptMarker = "data-pps-script";

    /// <summary>
    /// Gets the loader block injected into Jellyfin Web's index.html.
    /// </summary>
    /// <returns>HTML block.</returns>
    public static string GetLoaderBlock()
    {
        return $$"""
            {{StartMarker}}
            <script data-pps-loader="true">
            (function () {
                if (window.__ppsLoaderStarted) {
                    return;
                }
                window.__ppsLoaderStarted = true;

                var path = location.pathname || '';
                var webIndex = path.toLowerCase().indexOf('/web');
                var basePath = webIndex >= 0 ? path.slice(0, webIndex) : '';
                var version = '{{PostPlaySuggestionsPlugin.PluginVersion}}';

                if (!document.querySelector('link[data-pps-css]')) {
                    var link = document.createElement('link');
                    link.rel = 'stylesheet';
                    link.href = basePath + '/PostPlaySuggestions/Styles?v=' + encodeURIComponent(version);
                    link.setAttribute('data-pps-css', 'true');
                    document.head.appendChild(link);
                }

                var script = document.createElement('script');
                script.src = basePath + '/PostPlaySuggestions/Script?v=' + encodeURIComponent(version);
                script.defer = true;
                script.setAttribute('data-pps-script', 'true');
                document.documentElement.appendChild(script);
            })();
            </script>
            {{EndMarker}}
            """;
    }

    /// <summary>
    /// Injects the loader block into an HTML document if needed.
    /// </summary>
    /// <param name="html">HTML document.</param>
    /// <returns>Injected HTML document.</returns>
    public static string InjectIntoHtml(string html)
    {
        if (html.Contains(ScriptMarker, StringComparison.OrdinalIgnoreCase)
            || !html.Contains("</body>", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        return html.Replace(
            "</body>",
            GetLoaderBlock() + Environment.NewLine + "</body>",
            StringComparison.OrdinalIgnoreCase);
    }
}
