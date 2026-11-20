using System.IO;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.MissingEpisodeMarker;

/// <summary>
/// Folders used by the plugin. They live in Jellyfin's data folder (not the plugin folder),
/// so generated posters survive plugin updates.
/// </summary>
public class PluginPaths
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginPaths"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    public PluginPaths(IApplicationPaths applicationPaths)
    {
        DataDirectory = Path.Combine(applicationPaths.DataPath, "missing-episode-marker");
        PosterDirectory = Path.Combine(DataDirectory, "posters");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(PosterDirectory);
    }

    /// <summary>Gets the plugin's data folder.</summary>
    public string DataDirectory { get; }

    /// <summary>Gets the folder where marked posters are written.</summary>
    public string PosterDirectory { get; }
}
