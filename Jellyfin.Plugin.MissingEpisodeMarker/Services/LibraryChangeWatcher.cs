using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MissingEpisodeMarker.Configuration;
using Jellyfin.Plugin.MissingEpisodeMarker.Posters;
using Jellyfin.Plugin.MissingEpisodeMarker.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Services;

/// <summary>
/// Reacts to library changes so markers stay correct between full checks:
/// re-applies a marker as soon as Jellyfin swaps a marked poster, and rechecks a series
/// when episodes are added or removed. Also keeps the scheduled task's timer in line with the settings.
/// </summary>
public sealed class LibraryChangeWatcher : IHostedService, IDisposable
{
    private static readonly TimeSpan ReapplyDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RecheckDelay = TimeSpan.FromSeconds(90);

    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly MarkerRunner _runner;
    private readonly PosterMarkerService _posters;
    private readonly ILogger<LibraryChangeWatcher> _logger;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _pendingReapply = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _pendingRecheck = new();
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChangeWatcher"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="taskManager">Task manager.</param>
    /// <param name="runner">Runner.</param>
    /// <param name="posters">Poster service.</param>
    /// <param name="logger">Logger.</param>
    public LibraryChangeWatcher(
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        MarkerRunner runner,
        PosterMarkerService posters,
        ILogger<LibraryChangeWatcher> logger)
    {
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _runner = runner;
        _posters = posters;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemAdded += OnEpisodeChanged;
        _libraryManager.ItemRemoved += OnEpisodeChanged;

        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        }

        // Scheduled tasks may not be registered yet at startup; apply the schedule a little later.
        _ = Task.Run(
            async () =>
            {
                for (var attempt = 0; attempt < 12 && !_stopping.IsCancellationRequested; attempt++)
                {
                    if (ApplySchedule())
                    {
                        return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(10), _stopping.Token).ConfigureAwait(false);
                }
            },
            _stopping.Token);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemAdded -= OnEpisodeChanged;
        _libraryManager.ItemRemoved -= OnEpisodeChanged;
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        _stopping.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Dispose();
        foreach (var cts in _pendingReapply.Values.Concat(_pendingRecheck.Values))
        {
            cts.Dispose();
        }
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static TimeSpan? IntervalFor(CheckSchedule schedule) => schedule switch
    {
        CheckSchedule.Hourly => TimeSpan.FromHours(1),
        CheckSchedule.Every6Hours => TimeSpan.FromHours(6),
        CheckSchedule.Every12Hours => TimeSpan.FromHours(12),
        CheckSchedule.Daily => TimeSpan.FromDays(1),
        CheckSchedule.Weekly => TimeSpan.FromDays(7),
        _ => null,
    };

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e) => ApplySchedule();

    /// <summary>Sets the scheduled task's timer from the settings. Returns false if the task is not registered yet.</summary>
    private bool ApplySchedule()
    {
        try
        {
            var worker = _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is MarkMissingEpisodesTask);
            if (worker is null)
            {
                return false;
            }

            var interval = IntervalFor(Config.Schedule);
            var current = worker.Triggers;
            var alreadyRight = interval is null
                ? current.Count == 0
                : current.Count == 1
                    && current[0].Type == TaskTriggerInfoType.IntervalTrigger
                    && current[0].IntervalTicks == interval.Value.Ticks;

            if (!alreadyRight)
            {
                worker.Triggers = interval is null
                    ? Array.Empty<TaskTriggerInfo>()
                    : new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = interval.Value.Ticks } };
                _logger.LogInformation("Missing Episode Marker schedule set to {Schedule}", Config.Schedule);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not apply the Missing Episode Marker schedule");
            return true; // Don't keep retrying on a real error.
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        var item = e.Item;
        if (!Config.ReactToLibraryChanges || item is not (Series or Season))
        {
            return;
        }

        // Only care about items we marked whose poster Jellyfin has since replaced.
        if (_posters.GetEntry(item.Id) is null || !_posters.WasReplaced(item))
        {
            return;
        }

        Debounce(_pendingReapply, item.Id, ReapplyDelay, ct => _runner.ReapplyAsync(item.Id, ct));
    }

    private void OnEpisodeChanged(object? sender, ItemChangeEventArgs e)
    {
        if (!Config.ReactToLibraryChanges || e.Item is not Episode episode || episode.IsVirtualItem)
        {
            return;
        }

        var seriesId = episode.SeriesId;
        if (seriesId == Guid.Empty)
        {
            return;
        }

        var changedAt = DateTime.UtcNow;
        Debounce(
            _pendingRecheck,
            seriesId,
            RecheckDelay,
            ct =>
            {
                // A full check that finished after this change already covered it.
                if (_runner.LastReport.FinishedUtc is DateTime finished && finished > changedAt)
                {
                    return Task.CompletedTask;
                }

                return _runner.RecheckSeriesAsync(seriesId, ct);
            });
    }

    /// <summary>Runs an action after a delay; a new request for the same key restarts the delay.</summary>
    private void Debounce(ConcurrentDictionary<Guid, CancellationTokenSource> pending, Guid key, TimeSpan delay, Func<CancellationToken, Task> action)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        if (pending.TryRemove(key, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        pending[key] = cts;
        var token = cts.Token;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    if (pending.TryGetValue(key, out var current) && current == cts)
                    {
                        pending.TryRemove(key, out _);
                    }

                    await action(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Replaced by a newer request or shutting down.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Missing Episode Marker background update failed");
                }
            },
            CancellationToken.None);
    }
}
