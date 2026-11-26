using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PostPlaySuggestions.Services;

/// <summary>
/// Adds the Post Play Suggestions injection middleware at the front of the Jellyfin Web pipeline.
/// </summary>
public sealed class PostPlayStartupFilter : IStartupFilter
{
    private readonly ILogger<PostPlayStartupFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostPlayStartupFilter"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public PostPlayStartupFilter(ILogger<PostPlayStartupFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            next(app);

            app.UseMiddleware<PostPlayInjectionMiddleware>();
            MoveLastMiddlewareToFront(app);

            _logger.LogInformation("[PostPlaySuggestions] Injection middleware registered");
        };
    }

    private void MoveLastMiddlewareToFront(IApplicationBuilder app)
    {
        try
        {
            var field = app.GetType().GetField("_components", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(app) is not IList components || components.Count == 0)
            {
                _logger.LogWarning("[PostPlaySuggestions] Could not reorder middleware pipeline");
                return;
            }

            var last = components[components.Count - 1];
            components.RemoveAt(components.Count - 1);
            components.Insert(0, last);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PostPlaySuggestions] Could not move middleware to the front");
        }
    }
}
