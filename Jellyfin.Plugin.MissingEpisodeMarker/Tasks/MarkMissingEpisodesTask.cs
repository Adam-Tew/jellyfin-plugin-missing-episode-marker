using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MissingEpisodeMarker.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Tasks;

/// <summary>
/// Scheduled task: check Sonarr and update poster markers.
/// </summary>
public class MarkMissingEpisodesTask : IScheduledTask
{
    private readonly MarkerRunner _runner;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkMissingEpisodesTask"/> class.
    /// </summary>
    /// <param name="runner">The runner.</param>
    public MarkMissingEpisodesTask(MarkerRunner runner)
    {
        _runner = runner;
    }

    /// <inheritdoc />
    public string Name => "Mark series with missing episodes";

    /// <inheritdoc />
    public string Key => "MissingEpisodeMarkerRun";

    /// <inheritdoc />
    public string Description => "Asks Sonarr what is missing or downloading and updates the poster markers. The plugin already checks every few minutes on its own (see its settings); run this to check right now.";

    /// <inheritdoc />
    public string Category => "Missing Episode Marker";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _runner.RunAsync(progress, "Scheduled task", true, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => Array.Empty<TaskTriggerInfo>(); // The plugin runs its own timer (every few minutes).
}

/// <summary>
/// Runs the check after each library scan, so markers come back if a scan replaced posters.
/// </summary>
public class MarkAfterLibraryScan : ILibraryPostScanTask
{
    private readonly MarkerRunner _runner;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkAfterLibraryScan"/> class.
    /// </summary>
    /// <param name="runner">The runner.</param>
    public MarkAfterLibraryScan(MarkerRunner runner)
    {
        _runner = runner;
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.RunAfterLibraryScan || string.IsNullOrWhiteSpace(config.SonarrApiKey))
        {
            return Task.CompletedTask;
        }

        return _runner.RunAsync(progress, "Library scan", false, cancellationToken);
    }
}
