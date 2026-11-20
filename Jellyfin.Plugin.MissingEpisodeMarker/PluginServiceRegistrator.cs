using Jellyfin.Plugin.MissingEpisodeMarker.Filler;
using Jellyfin.Plugin.MissingEpisodeMarker.Posters;
using Jellyfin.Plugin.MissingEpisodeMarker.Services;
using Jellyfin.Plugin.MissingEpisodeMarker.Sonarr;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MissingEpisodeMarker;

/// <summary>
/// Registers the plugin's services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PluginPaths>();
        serviceCollection.AddSingleton<SonarrClient>();
        serviceCollection.AddSingleton<FillerService>();
        serviceCollection.AddSingleton<PosterMarkerService>();
        serviceCollection.AddSingleton<MarkerRunner>();
        serviceCollection.AddHostedService<LibraryChangeWatcher>();
    }
}
