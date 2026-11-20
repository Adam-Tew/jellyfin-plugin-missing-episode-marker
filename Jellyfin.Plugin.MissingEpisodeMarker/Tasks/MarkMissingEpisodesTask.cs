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
    public string Description => "Asks Sonarr which episodes are missing and updates the markers on series and season posters.";

    /// <inheritdoc />
    public string Category => "Missing Episode Marker";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _runner.RunAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks,
        };
    }
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

        return _runner.RunAsync(progress, cancellationToken);
    }
}
