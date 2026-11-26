using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PostPlaySuggestions.Services;

/// <summary>
/// Injects the Post Play Suggestions loader into Jellyfin Web HTML responses.
/// </summary>
public sealed class PostPlayInjectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PostPlayInjectionMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostPlayInjectionMiddleware"/> class.
    /// </summary>
    /// <param name="next">Next middleware.</param>
    /// <param name="logger">Logger.</param>
    public PostPlayInjectionMiddleware(RequestDelegate next, ILogger<PostPlayInjectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Executes the middleware.
    /// </summary>
    /// <param name="context">HTTP context.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task Invoke(HttpContext context)
    {
        if (!ShouldInspectRequest(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var originalBody = context.Response.Body;
        var originalAcceptEncoding = context.Request.Headers.AcceptEncoding.ToString();
        context.Request.Headers.AcceptEncoding = string.Empty;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);

            buffer.Position = 0;
            if (!ShouldInjectResponse(context))
            {
                await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
                return;
            }

            using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var html = await reader.ReadToEndAsync().ConfigureAwait(false);
            var injected = PostPlayWebInjection.InjectIntoHtml(html);
            var bytes = Encoding.UTF8.GetBytes(injected);

            context.Response.Body = originalBody;
            context.Response.ContentLength = bytes.Length;
            context.Response.Headers.Remove("ETag");
            context.Response.Headers.Remove("Content-MD5");
            context.Response.Headers.Remove("Content-Encoding");
            await originalBody.WriteAsync(bytes).ConfigureAwait(false);

            if (!string.Equals(html, injected, StringComparison.Ordinal))
            {
                _logger.LogDebug("[PostPlaySuggestions] Injected frontend assets into Jellyfin Web");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PostPlaySuggestions] Failed to inject frontend assets");
            context.Response.Body = originalBody;
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
        }
        finally
        {
            context.Request.Headers.AcceptEncoding = originalAcceptEncoding;
            context.Response.Body = originalBody;
        }
    }

    private static bool ShouldInspectRequest(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return false;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        return path.EndsWith("/web", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldInjectResponse(HttpContext context)
    {
        var configuration = PostPlaySuggestionsPlugin.Instance?.Configuration;
        if (configuration is null || !configuration.Enabled)
        {
            return false;
        }

        return context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
    }
}
