using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MissingEpisodeMarker.Configuration;
using Jellyfin.Plugin.MissingEpisodeMarker.Posters;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Services;

/// <summary>
/// Keeps markers current: runs a check every few minutes (setting), after episodes are added or removed,
/// and after settings change; and re-applies a marker as soon as a poster arrives or Jellyfin replaces one.
/// Sonarr's webhook (see the controller) asks for checks too.
/// </summary>
public sealed class LibraryChangeWatcher : IHostedService, IDisposable
{
    private static readonly TimeSpan ReapplyDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EpisodeChangeDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TimerTick = TimeSpan.FromSeconds(20);

    private readonly ILibraryManager _libraryManager;
    private readonly MarkerRunner _runner;
    private readonly SyncScheduler _scheduler;
    private readonly PosterMarkerService _posters;
    private readonly ILogger<LibraryChangeWatcher> _logger;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _pendingReapply = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _snapshotLock = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private string? _lastCheckSnapshot;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChangeWatcher"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="runner">Runner.</param>
    /// <param name="scheduler">Check scheduler.</param>
    /// <param name="posters">Poster service.</param>
    /// <param name="logger">Logger.</param>
    public LibraryChangeWatcher(
        ILibraryManager libraryManager,
        MarkerRunner runner,
        SyncScheduler scheduler,
        PosterMarkerService posters,
        ILogger<LibraryChangeWatcher> logger)
    {
        _libraryManager = libraryManager;
        _runner = runner;
        _scheduler = scheduler;
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
            _lastCheckSnapshot = CheckSnapshot(Plugin.Instance.Configuration);
        }

        _ = Task.Run(() => TimerLoopAsync(_stopping.Token), CancellationToken.None);
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
        foreach (var cts in _pendingReapply.Values)
        {
            cts.Dispose();
        }
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// A fingerprint of every setting that affects detection or marking.
    /// "When to check" and menu settings are left out: changing them needs no new check.
    /// </summary>
    private static string CheckSnapshot(PluginConfiguration config)
    {
        var copy = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(config)) ?? new PluginConfiguration();
        copy.ShowInMenu = false;
        copy.ReactToLibraryChanges = false;
        copy.RunAfterLibraryScan = false;
        copy.CheckIntervalMinutes = 0;
        copy.WebhookToken = string.Empty;
        copy.FillerRefreshDays = 0;
        return JsonSerializer.Serialize(copy);
    }

    /// <summary>Runs a check every N minutes (setting). The first check runs a minute after Jellyfin starts.</summary>
    private async Task TimerLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimerTick, token).ConfigureAwait(false);

                var config = Config;
                if (string.IsNullOrWhiteSpace(config.SonarrApiKey) || _runner.IsRunning || _scheduler.NextDueUtc is not null)
                {
                    continue;
                }

                var now = DateTime.UtcNow;
                if (_runner.LastRunUtc is null)
                {
                    if (now - _startedUtc >= StartupDelay)
                    {
                        _scheduler.RequestSync(TimeSpan.Zero, "Startup", fullRepair: true);
                    }

                    continue;
                }

                if (config.CheckIntervalMinutes <= 0)
                {
                    continue; // Timer off; webhook and library changes still trigger checks.
                }

                var interval = TimeSpan.FromMinutes(Math.Clamp(config.CheckIntervalMinutes, 1, 1440));
                if (now - _runner.LastRunUtc.Value >= interval)
                {
                    _scheduler.RequestSync(TimeSpan.Zero, "Timer");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        if (e is not PluginConfiguration config)
        {
            return;
        }

        // Settings that change what gets marked or how it looks: check again so posters follow at once.
        var snapshot = CheckSnapshot(config);
        lock (_snapshotLock)
        {
            if (snapshot == _lastCheckSnapshot)
            {
                return;
            }

            _lastCheckSnapshot = snapshot;
        }

        if (!string.IsNullOrWhiteSpace(config.SonarrApiKey))
        {
            _logger.LogInformation("Missing Episode Marker settings changed; checking again");
            _scheduler.RequestSync(TimeSpan.FromSeconds(3), "Settings changed");
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        var item = e.Item;
        if (!Config.ReactToLibraryChanges || item is not (Series or Season))
        {
            return;
        }

        // Items that should be marked but don't show the marked copy: the poster just arrived,
        // or Jellyfin replaced the marked one (e.g. a metadata refresh).
        if (!_posters.NeedsMarker(item))
        {
            return;
        }

        var id = item.Id;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        if (_pendingReapply.TryRemove(id, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        _pendingReapply[id] = cts;
        var token = cts.Token;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(ReapplyDelay, token).ConfigureAwait(false);
                    if (_pendingReapply.TryGetValue(id, out var current) && current == cts)
                    {
                        _pendingReapply.TryRemove(id, out _);
                    }

                    await _runner.ReapplyAsync(id, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Replaced by a newer update or shutting down.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Missing Episode Marker could not re-apply a marker");
                }
            },
            CancellationToken.None);
    }

    private void OnEpisodeChanged(object? sender, ItemChangeEventArgs e)
    {
        if (!Config.ReactToLibraryChanges
            || e.Item is not Episode episode
            || episode.IsVirtualItem
            || string.IsNullOrWhiteSpace(Config.SonarrApiKey))
        {
            return;
        }

        // Let Sonarr finish importing (and other episodes of the same batch arrive) first.
        _scheduler.RequestSync(EpisodeChangeDelay, "Episodes added or removed");
    }
}
