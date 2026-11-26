using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MissingEpisodeMarker.Configuration;
using Jellyfin.Plugin.MissingEpisodeMarker.Detection;
using Jellyfin.Plugin.MissingEpisodeMarker.Filler;
using Jellyfin.Plugin.MissingEpisodeMarker.Posters;
using Jellyfin.Plugin.MissingEpisodeMarker.Sonarr;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Services;

/// <summary>
/// Runs a check: asks Sonarr what is missing and what is downloading, works out the marker for every
/// series and season, and marks/restores posters. A check is three Sonarr requests, so it can run often.
/// </summary>
public class MarkerRunner
{
    private const int MaxEpisodesInReport = 25;
    private static readonly TimeSpan RepairPassInterval = TimeSpan.FromHours(6);

    private readonly ILibraryManager _libraryManager;
    private readonly SonarrClient _sonarr;
    private readonly FillerService _filler;
    private readonly PosterMarkerService _posters;
    private readonly PosterRepairService _repair;
    private readonly ILogger<MarkerRunner> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly object _reportLock = new();
    private DateTime _lastRepairPassUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkerRunner"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="sonarr">Sonarr client.</param>
    /// <param name="filler">Filler service.</param>
    /// <param name="posters">Poster marker service.</param>
    /// <param name="repair">Poster repair service.</param>
    /// <param name="logger">Logger.</param>
    public MarkerRunner(
        ILibraryManager libraryManager,
        SonarrClient sonarr,
        FillerService filler,
        PosterMarkerService posters,
        PosterRepairService repair,
        ILogger<MarkerRunner> logger)
    {
        _libraryManager = libraryManager;
        _sonarr = sonarr;
        _filler = filler;
        _posters = posters;
        _repair = repair;
        _logger = logger;
    }

    /// <summary>Gets the report of the last check.</summary>
    public MarkerReport LastReport { get; private set; } = new MarkerReport();

    /// <summary>Gets a value indicating whether a check is in progress.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Gets the progress of the current check, 0-100.</summary>
    public double Progress { get; private set; }

    /// <summary>Gets when the current check started (UTC).</summary>
    public DateTime? RunStartedUtc { get; private set; }

    /// <summary>Gets when the last check finished (UTC), successful or not.</summary>
    public DateTime? LastRunUtc { get; private set; }

    /// <summary>Gets the estimated seconds left of the current check, if it can be estimated.</summary>
    public double? EtaSeconds
    {
        get
        {
            if (!IsRunning || RunStartedUtc is null || Progress < 3)
            {
                return null;
            }

            var elapsed = (DateTime.UtcNow - RunStartedUtc.Value).TotalSeconds;
            return Math.Max(0, elapsed * (100 - Progress) / Progress);
        }
    }

    /// <summary>Runs a check. If another check is in progress, this waits for it.</summary>
    /// <param name="progress">Progress (0-100).</param>
    /// <param name="reason">What started the check (for the report and log).</param>
    /// <param name="fullRepair">Whether to check every poster for problems now (otherwise every 6 hours).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(IProgress<double> progress, string reason, bool fullRepair, CancellationToken cancellationToken)
    {
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsRunning = true;
        Progress = 0;
        RunStartedUtc = DateTime.UtcNow;
        var tracking = new ActionProgress(p =>
        {
            Progress = p;
            progress.Report(p);
        });

        try
        {
            var report = await RunInternalAsync(tracking, reason, fullRepair, cancellationToken).ConfigureAwait(false);
            lock (_reportLock)
            {
                LastReport = report;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Missing Episode Marker check failed");
            lock (_reportLock)
            {
                LastReport = new MarkerReport
                {
                    FinishedUtc = DateTime.UtcNow,
                    Success = false,
                    Reason = reason,
                    Message = ex.Message,
                    PostersMarked = _posters.CountMarked(),
                };
            }
        }
        finally
        {
            IsRunning = false;
            Progress = 0;
            RunStartedUtc = null;
            LastRunUtc = DateTime.UtcNow;
            _runLock.Release();
        }
    }

    /// <summary>
    /// Puts a marker on an item whose poster arrived late or was replaced (e.g. after a metadata refresh).
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ReapplyAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        var spec = _posters.GetWanted(itemId);
        if (!config.MarkingEnabled || spec is null)
        {
            return;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || !_posters.NeedsMarker(item))
        {
            return;
        }

        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("Poster of {Name} arrived or was replaced; applying the marker", item.Name);
            await _posters.ApplyAsync(item, spec, config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not re-apply marker for {Name}", item.Name);
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Restores every marked poster.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RemoveAllMarkersAsync(CancellationToken cancellationToken)
    {
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _posters.RestoreAllAsync(cancellationToken).ConfigureAwait(false);
            lock (_reportLock)
            {
                LastReport = new MarkerReport
                {
                    FinishedUtc = DateTime.UtcNow,
                    Success = true,
                    Reason = "Remove all markers",
                    Message = "All markers removed. They come back on the next check unless marking is turned off.",
                };
            }
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Lists Sonarr series, for picking anime in the settings page.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The series, sorted by title.</returns>
    public async Task<List<SonarrSeries>> GetSonarrSeriesAsync(CancellationToken cancellationToken)
    {
        var config = GetConfig();
        var series = await _sonarr.GetSeriesAsync(config.SonarrUrl, config.SonarrApiKey, cancellationToken).ConfigureAwait(false);
        return series.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static PluginConfiguration GetConfig()
        => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static Dictionary<int, SeriesRule> GetRules(PluginConfiguration config)
        => config.SeriesRules
            .Where(r => r.TvdbId > 0)
            .GroupBy(r => r.TvdbId)
            .ToDictionary(g => g.Key, g => g.First());

    private static bool NeedsFillerData(PluginConfiguration config, SeriesRule? rule)
    {
        // Filler data only matters when missing filler should be ignored.
        var countFiller = (rule?.Filler ?? FillerOverride.Default) switch
        {
            FillerOverride.Count => true,
            FillerOverride.Ignore => false,
            _ => config.CountMissingFiller,
        };
        return !countFiller;
    }

    private static string Describe(ApplyResult result) => result switch
    {
        ApplyResult.Marked => "Marked",
        ApplyResult.NoPoster => "No poster yet; marked as soon as one arrives",
        ApplyResult.NotLocal => "Poster is not a file on the server",
        ApplyResult.OriginalMissing => "Poster file is missing",
        ApplyResult.OriginalUnknown => "Refresh this series' metadata once",
        ApplyResult.RenderFailed => "Could not draw the marker (see log)",
        _ => result.ToString(),
    };

    private static MarkerSpec Spec(PluginConfiguration config, MarkerState state, int count)
        => new MarkerSpec { State = state, Text = MarkerStyle.For(config, state).FormatText(count) };

    private static List<BaseItem> GetItems(ILibraryManager libraryManager, BaseItemKind kind)
        => libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [kind],
            Recursive = true,
            IsVirtualItem = false,
        }).ToList();

    private async Task<MarkerReport> RunInternalAsync(IProgress<double> progress, string reason, bool fullRepair, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        var report = new MarkerReport { Reason = reason };

        if (!config.MarkingEnabled)
        {
            if (_posters.GetMarkedItemIds().Count > 0)
            {
                await _posters.RestoreAllAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _posters.SetWanted(new Dictionary<Guid, MarkerSpec>());
            }

            report.FinishedUtc = DateTime.UtcNow;
            report.Success = true;
            report.Message = "Marking is turned off; all posters restored.";
            return report;
        }

        if (string.IsNullOrWhiteSpace(config.SonarrApiKey))
        {
            report.FinishedUtc = DateTime.UtcNow;
            report.Success = false;
            report.Message = "Enter the Sonarr address and API key on the General tab.";
            return report;
        }

        // 1. Ask Sonarr: all series, everything missing, and the download queue.
        progress.Report(2);
        var sonarrSeries = await _sonarr.GetSeriesAsync(config.SonarrUrl, config.SonarrApiKey, cancellationToken).ConfigureAwait(false);
        progress.Report(10);
        var missing = await _sonarr.GetMissingAsync(config.SonarrUrl, config.SonarrApiKey, cancellationToken).ConfigureAwait(false);
        progress.Report(25);
        var queue = await _sonarr.GetQueueAsync(config.SonarrUrl, config.SonarrApiKey, cancellationToken).ConfigureAwait(false);
        progress.Report(35);

        var missingBySeries = missing.ToLookup(e => e.SeriesId);
        var queueBySeries = queue
            .Where(q => q.SeriesId.HasValue || q.Episode is not null)
            .ToLookup(q => q.SeriesId ?? q.Episode!.SeriesId);

        // 2. What Jellyfin has.
        var jellyfinSeries = GetItems(_libraryManager, BaseItemKind.Series);
        var seasonsBySeries = GetItems(_libraryManager, BaseItemKind.Season)
            .OfType<Season>()
            .ToLookup(s => s.SeriesId);
        var index = BuildProviderIndex(jellyfinSeries);
        var rules = GetRules(config);
        var now = DateTime.UtcNow;

        // 3. Work out the marker for each series and season.
        var desired = new Dictionary<Guid, (BaseItem Item, MarkerSpec Spec)>();
        var showPosterOwner = new Dictionary<Guid, SeriesReport>();
        var markedCovers = new List<BaseItem>();
        var done = 0;

        foreach (var series in sonarrSeries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            done++;
            progress.Report(35 + (40.0 * done / Math.Max(1, sonarrSeries.Count)));

            if (!series.Monitored)
            {
                report.SeriesUnmonitored++;
                continue;
            }

            if (series.IsAnime && !config.AnimeEnabled)
            {
                continue;
            }

            var matches = FindJellyfinSeries(index, series);
            if (matches.Count == 0)
            {
                report.SeriesNotInJellyfin++;
                continue;
            }

            report.SeriesChecked++;

            var seriesMissing = missingBySeries[series.Id].ToList();
            var seriesQueue = queueBySeries[series.Id].ToList();
            if (seriesMissing.Count == 0 && seriesQueue.Count == 0)
            {
                continue; // Complete and nothing downloading: no marker.
            }

            try
            {
                rules.TryGetValue(series.TvdbId, out var rule);
                FillerInfo? filler = null;
                if (series.IsAnime && NeedsFillerData(config, rule))
                {
                    filler = await _filler.GetFillerAsync(config, series.Title, rule, cancellationToken).ConfigureAwait(false);
                }

                var result = StateDetector.Detect(series, seriesMissing, seriesQueue, config, rule, filler, now);
                if (result.State == MarkerState.None)
                {
                    continue;
                }

                var seriesReport = BuildSeriesReport(series, result, filler, config);
                report.Series.Add(seriesReport);

                foreach (var jf in matches)
                {
                    if (config.MarkSeries)
                    {
                        desired[jf.Id] = (jf, Spec(config, result.State, result.Count));
                        showPosterOwner[jf.Id] = seriesReport;
                        markedCovers.Add(jf);
                    }

                    if (config.MarkSeasons)
                    {
                        foreach (var season in seasonsBySeries[jf.Id])
                        {
                            if (season.IndexNumber is int number && result.Seasons.TryGetValue(number, out var s))
                            {
                                desired[season.Id] = (season, Spec(config, s.State, s.Count));
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not check series {Title}", series.Title);
            }
        }

        // 4. Remember what should be marked, so markers also follow posters that arrive later.
        _posters.SetWanted(desired.ToDictionary(d => d.Key, d => d.Value.Spec));

        // 5. Apply markers (only items whose marker changed are redrawn).
        var applied = 0;
        foreach (var (id, (item, spec)) in desired.OrderBy(d => d.Value.Item is Season ? 1 : 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await ApplyOneAsync(item, spec, config, cancellationToken).ConfigureAwait(false);
            if (showPosterOwner.TryGetValue(id, out var owner))
            {
                owner.PosterStatus = status;
            }

            applied++;
            progress.Report(75 + (15.0 * applied / Math.Max(1, desired.Count)));
        }

        // 6. Seasons without a poster of their own would show the marked show cover in apps.
        //    Fetch a season poster, or else give them an unmarked copy of the show poster.
        var standIns = new HashSet<Guid>();
        foreach (var cover in markedCovers)
        {
            foreach (var season in seasonsBySeries[cover.Id])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (desired.ContainsKey(season.Id))
                {
                    continue; // Gets its own marker (and a stand-in if needed) above.
                }

                if (await EnsureSeasonPosterAsync(season, config, cancellationToken).ConfigureAwait(false))
                {
                    standIns.Add(season.Id);
                }
            }
        }

        // 7. Restore everything that should no longer be marked (complete, unmonitored, deleted, ...).
        var restored = 0;
        foreach (var id in _posters.GetMarkedItemIds())
        {
            if (!desired.ContainsKey(id) && !standIns.Contains(id))
            {
                await _posters.RestoreAsync(id, cancellationToken).ConfigureAwait(false);
                restored++;
            }
        }

        // 8. Every few hours (or when asked): fix missing or wrong posters.
        if (fullRepair || now - _lastRepairPassUtc >= RepairPassInterval)
        {
            _lastRepairPassUtc = now;
            report.PostersRepaired = await RepairPassAsync(config, jellyfinSeries, desired, progress, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(100);
        report.Series.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        report.PostersMarked = _posters.CountMarked();
        report.FinishedUtc = DateTime.UtcNow;
        report.Success = true;
        report.Message = string.Format(
            CultureInfo.InvariantCulture,
            "Checked {0} monitored series: {1} with a marker, {2} posters marked{3}.",
            report.SeriesChecked,
            report.Series.Count,
            report.PostersMarked,
            report.PostersRepaired > 0 ? ", " + report.PostersRepaired + " posters fetched again" : string.Empty);

        if (restored > 0 || fullRepair || LastReport.FinishedUtc is null)
        {
            _logger.LogInformation("Missing Episode Marker ({Reason}): {Message}", reason, report.Message);
        }
        else
        {
            _logger.LogDebug("Missing Episode Marker ({Reason}): {Message}", reason, report.Message);
        }

        return report;
    }

    private async Task<string> ApplyOneAsync(BaseItem item, MarkerSpec spec, PluginConfiguration config, CancellationToken cancellationToken)
    {
        // A landscape "poster" is usually a video frame: fetch a real poster before marking it.
        if (config.RepairShowPosters && item is Series && _repair.Inspect(item) == PosterProblem.Landscape)
        {
            await _repair.RepairIfNeededAsync(item, cancellationToken).ConfigureAwait(false);
        }

        ApplyResult result;
        try
        {
            result = await _posters.ApplyAsync(item, spec, config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not mark poster for {Name}", item.Name);
            result = ApplyResult.RenderFailed;
        }

        if (result == ApplyResult.NoPoster && item is Season season
            && await EnsureSeasonPosterAsync(season, config, cancellationToken).ConfigureAwait(false))
        {
            result = await _posters.ApplyAsync(_libraryManager.GetItemById(item.Id) ?? item, spec, config, cancellationToken).ConfigureAwait(false);
        }
        else if (result == ApplyResult.NoPoster && item is Season)
        {
            var fresh = _libraryManager.GetItemById(item.Id);
            if (fresh is not null && fresh.GetImageInfo(ImageType.Primary, 0) is not null)
            {
                result = await _posters.ApplyAsync(fresh, spec, config, cancellationToken).ConfigureAwait(false);
            }
        }

        var status = Describe(result);
        if (result == ApplyResult.NoPoster && config.RepairShowPosters && item is Series)
        {
            var (_, requested) = await _repair.RepairIfNeededAsync(item, cancellationToken).ConfigureAwait(false);
            if (requested)
            {
                status = "No poster; fetching one, marked when it arrives";
            }
        }

        return status;
    }

    /// <summary>
    /// Makes sure a season has a poster of its own: asks Jellyfin to fetch one (at most once a week),
    /// and if there still is none, gives it an unmarked copy of the show poster.
    /// </summary>
    /// <param name="season">The season.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the season uses a stand-in copy of the show poster.</returns>
    private async Task<bool> EnsureSeasonPosterAsync(Season season, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var current = season;
        var existing = _posters.GetEntry(season.Id);
        var image = season.GetImageInfo(ImageType.Primary, 0);
        var isOurs = existing is not null && image is not null && string.Equals(image.Path, existing.MarkedPath, StringComparison.OrdinalIgnoreCase);
        if (image is not null && !isOurs)
        {
            await _posters.EnsureStandInAsync(season, string.Empty, cancellationToken).ConfigureAwait(false); // forgets an old stand-in
            return false; // Has a real poster.
        }

        if (image is null && config.RepairShowPosters)
        {
            var (_, requested) = await _repair.RepairIfNeededAsync(season, cancellationToken).ConfigureAwait(false);
            if (requested)
            {
                current = _libraryManager.GetItemById(season.Id) as Season ?? season;
                if (current.GetImageInfo(ImageType.Primary, 0) is not null)
                {
                    _logger.LogInformation("Fetched a poster for {Series} {Season}", current.SeriesName, current.Name);
                    return false;
                }
            }
        }

        var series = _libraryManager.GetItemById(current.SeriesId);
        var seriesPoster = series is null ? null : _posters.GetUnmarkedPosterPath(series);
        if (seriesPoster is null)
        {
            return false;
        }

        return await _posters.EnsureStandInAsync(current, seriesPoster, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RepairPassAsync(
        PluginConfiguration config,
        List<BaseItem> jellyfinSeries,
        Dictionary<Guid, (BaseItem Item, MarkerSpec Spec)> desired,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var repaired = 0;
        if (config.RepairShowPosters)
        {
            foreach (var jf in jellyfinSeries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (desired.ContainsKey(jf.Id))
                {
                    continue; // Already handled when marking.
                }

                var (_, requested) = await _repair.RepairIfNeededAsync(jf, cancellationToken).ConfigureAwait(false);
                if (requested)
                {
                    repaired++;
                }
            }

            foreach (var season in GetItems(_libraryManager, BaseItemKind.Season).OfType<Season>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (season.GetImageInfo(ImageType.Primary, 0) is not null || desired.ContainsKey(season.Id))
                {
                    continue; // Has a poster (or was handled when marking).
                }

                var (_, requested) = await _repair.RepairIfNeededAsync(season, cancellationToken).ConfigureAwait(false);
                if (requested)
                {
                    repaired++;
                }
            }
        }

        if (config.RepairMoviePosters)
        {
            var movies = GetItems(_libraryManager, BaseItemKind.Movie);
            var checkedMovies = 0;
            foreach (var movie in movies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (_, requested) = await _repair.RepairIfNeededAsync(movie, cancellationToken).ConfigureAwait(false);
                if (requested)
                {
                    repaired++;
                }

                checkedMovies++;
                progress.Report(90 + (9.0 * checkedMovies / Math.Max(1, movies.Count)));
            }
        }

        return repaired;
    }

    private static SeriesReport BuildSeriesReport(SonarrSeries series, SeriesState result, FillerInfo? filler, PluginConfiguration config)
    {
        var report = new SeriesReport
        {
            Title = series.Title,
            TvdbId = series.TvdbId,
            Status = series.Status,
            IsAnime = series.IsAnime,
            State = result.State.ToString(),
            Missing = result.Episodes.Count(e => e.State == MarkerState.Missing),
            Stalled = result.Episodes.Count(e => e.State == MarkerState.Stalled),
            Downloading = result.Episodes.Count(e => e.State == MarkerState.Downloading),
            Incoming = result.Episodes.Count(e => e.State == MarkerState.Incoming),
            WithinGrace = result.WithinGraceCount,
            FillerSource = filler?.Source,
            PosterStatus = config.MarkSeries ? string.Empty : "Show posters are not marked (setting)",
        };

        foreach (var season in result.Seasons.Values.OrderBy(s => s.Season))
        {
            report.Seasons.Add(new SeasonReport { Season = season.Season, State = season.State.ToString(), Count = season.Count });
        }

        // Most important first, then in episode order.
        foreach (var ep in result.Episodes
                     .OrderByDescending(e => e.State)
                     .ThenBy(e => e.Season)
                     .ThenBy(e => e.Episode)
                     .Take(MaxEpisodesInReport))
        {
            report.Episodes.Add(new EpisodeReport
            {
                Season = ep.Season,
                Episode = ep.Episode,
                Absolute = ep.Absolute,
                Title = ep.Title,
                AirDateUtc = ep.AirDateUtc,
                State = ep.State.ToString(),
            });
        }

        return report;
    }

    private static Dictionary<string, List<BaseItem>> BuildProviderIndex(IEnumerable<BaseItem> jellyfinSeries)
    {
        var index = new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in jellyfinSeries)
        {
            Add(index, "tvdb", item.GetProviderId(MetadataProvider.Tvdb), item);
            Add(index, "imdb", item.GetProviderId(MetadataProvider.Imdb), item);
            Add(index, "tmdb", item.GetProviderId(MetadataProvider.Tmdb), item);
        }

        return index;

        static void Add(Dictionary<string, List<BaseItem>> index, string kind, string? id, BaseItem item)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            var key = kind + ":" + id.Trim();
            if (!index.TryGetValue(key, out var list))
            {
                list = new List<BaseItem>();
                index[key] = list;
            }

            if (!list.Contains(item))
            {
                list.Add(item);
            }
        }
    }

    private static List<BaseItem> FindJellyfinSeries(Dictionary<string, List<BaseItem>> index, SonarrSeries series)
    {
        if (series.TvdbId > 0
            && index.TryGetValue("tvdb:" + series.TvdbId.ToString(CultureInfo.InvariantCulture), out var byTvdb))
        {
            return byTvdb;
        }

        if (!string.IsNullOrWhiteSpace(series.ImdbId) && index.TryGetValue("imdb:" + series.ImdbId, out var byImdb))
        {
            return byImdb;
        }

        if (series.TmdbId > 0
            && index.TryGetValue("tmdb:" + series.TmdbId.ToString(CultureInfo.InvariantCulture), out var byTmdb))
        {
            return byTmdb;
        }

        return new List<BaseItem>();
    }

    /// <summary>Reports progress synchronously through a callback.</summary>
    private sealed class ActionProgress : IProgress<double>
    {
        private readonly Action<double> _report;

        public ActionProgress(Action<double> report) => _report = report;

        public void Report(double value) => _report(value);
    }
}
