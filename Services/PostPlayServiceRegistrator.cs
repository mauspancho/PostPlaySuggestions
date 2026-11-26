using Jellyfin.Plugin.PostPlaySuggestions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PostPlaySuggestions.Services;

/// <summary>
/// Registers the plugin services in Jellyfin's dependency injection container.
/// </summary>
public sealed class PostPlayServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<CreditsResolver>();
        serviceCollection.AddSingleton<WebPanelRegistry>();
        serviceCollection.AddHostedService<PostPlayNativeService>();
        serviceCollection.AddSingleton<IStartupFilter, PostPlayStartupFilter>();
    }
}
