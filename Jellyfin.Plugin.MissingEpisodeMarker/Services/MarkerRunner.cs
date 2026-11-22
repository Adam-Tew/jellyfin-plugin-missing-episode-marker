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
/// Runs checks: asks Sonarr, decides what is incomplete, and marks/restores posters.
/// </summary>
public class MarkerRunner
{
    private const int MaxEpisodesInReport = 25;

    private readonly ILibraryManager _libraryManager;
    private readonly SonarrClient _sonarr;
    private readonly FillerService _filler;
    private readonly PosterMarkerService _posters;
    private readonly PosterRepairService _repair;
    private readonly ILogger<MarkerRunner> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly object _reportLock = new();

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

    /// <summary>Gets the report of the last full run (kept up to date by single-series rechecks).</summary>
    public MarkerReport LastReport { get; private set; } = new MarkerReport();

    /// <summary>Gets a value indicating whether a full run is in progress.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Gets the progress of the current full run, 0-100.</summary>
    public double Progress { get; private set; }

    /// <summary>Gets when the current full run started (UTC).</summary>
    public DateTime? RunStartedUtc { get; private set; }

    /// <summary>Gets the estimated seconds left of the current run, if it can be estimated.</summary>
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

    /// <summary>Runs a full check. If another check is in progress, this waits for it.</summary>
    /// <param name="progress">Progress (0-100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
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
            var report = await RunInternalAsync(tracking, cancellationToken).ConfigureAwait(false);
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
            _logger.LogError(ex, "Missing Episode Marker run failed");
            lock (_reportLock)
            {
                LastReport = new MarkerReport
                {
                    FinishedUtc = DateTime.UtcNow,
                    Success = false,
                    Message = ex.Message,
                    PostersMarked = _posters.GetMarkedItemIds().Count,
                };
            }
        }
        finally
        {
            IsRunning = false;
            Progress = 0;
            RunStartedUtc = null;
            _runLock.Release();
        }
    }

    /// <summary>
    /// Rechecks a single series (e.g. after episodes were added or removed) and updates its markers.
    /// </summary>
    /// <param name="jellyfinSeriesId">The Jellyfin series id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RecheckSeriesAsync(Guid jellyfinSeriesId, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        if (!config.MarkingEnabled || string.IsNullOrWhiteSpace(config.SonarrApiKey))
        {
            return;
        }

        if (_libraryManager.GetItemById(jellyfinSeriesId) is not Series jfSeries)
        {
            return;
        }

        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sonarrSeries = await _sonarr.GetSeriesAsync(config.SonarrUrl, config.SonarrApiKey, cancellationToken).ConfigureAwait(false);
            var index = BuildProviderIndex(new BaseItem[] { jfSeries });
            var match = sonarrSeries.FirstOrDefault(s => FindJellyfinSeries(index, s).Count > 0);

            var seasons = GetSeasons(jfSeries.Id);
            var desired = new Dictionary<Guid, (BaseItem Item, string Text)>();
            SeriesReport? seriesReport = null;

            if (match is not null && IsChecked(config, match, GetRules(config)))
            {
                (seriesReport, var seriesDesired) = await ProcessSeriesAsync(
                    match, new List<BaseItem> { jfSeries }, seasons, config, GetRules(config), DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
                foreach (var (id, value) in seriesDesired)
                {
                    desired[id] = value;
                }
            }

            // Remember what should be marked first, so markers follow posters that arrive later.
            var ownItems = new HashSet<Guid>(seasons.Select(s => s.Id)) { jfSeries.Id };
            _posters.UpdateWanted(ownItems, desired.ToDictionary(d => d.Key, d => d.Value.Text));

            if (seriesReport is not null)
            {
                await ApplyDesiredAsync(desired, config, seriesReport, jfSeries.Id, cancellationToken).ConfigureAwait(false);
            }

            if (config.RepairShowPosters)
            {
                await _repair.RepairIfNeededAsync(jfSeries, cancellationToken).ConfigureAwait(false);
            }

            // Restore this series' items that should no longer be marked.
            foreach (var id in _posters.GetMarkedItemIds())
            {
                if (ownItems.Contains(id) && !desired.ContainsKey(id))
                {
                    await _posters.RestoreAsync(id, cancellationToken).ConfigureAwait(false);
                }
            }

            // Keep the report in step.
            lock (_reportLock)
            {
                var report = LastReport;
                if (match is not null)
                {
                    report.IncompleteSeries.RemoveAll(r => r.TvdbId == match.TvdbId);
                }

                if (seriesReport is not null)
                {
                    report.IncompleteSeries.Add(seriesReport);
                    report.IncompleteSeries.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
                }

                report.PostersMarked = _posters.GetMarkedItemIds().Count;
            }

            _logger.LogInformation(
                "Rechecked {Name}: {State}",
                jfSeries.Name,
                seriesReport is null ? "complete" : seriesReport.MissingCount + " missing");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not recheck series {Name}", jfSeries.Name);
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>
    /// Puts a marker back on an item whose poster Jellyfin replaced (e.g. after a metadata refresh).
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ReapplyAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        var text = _posters.GetWantedText(itemId);
        if (!config.MarkingEnabled || text is null)
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
            await _posters.ApplyAsync(item, text, config, cancellationToken).ConfigureAwait(false);
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
                    Message = "All markers removed. They come back on the next check unless marking is turned off.",
                };
            }
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Lists Sonarr series, for building rules in the settings page.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The series, sorted by title.</returns>
    public async Task<List<SonarrSeries>> GetSonarrSeriesAsync(CancellationToken cancellationToken)
    {
        var config = GetConfig();
        var series = await _sonarr.GetSeriesAsync(config.SonarrUrl, config.SonarrApiKey, cancellationToken).ConfigureAwait(false);
        return series.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Lists the specials (season 0) of a series in Sonarr.</summary>
    /// <param name="tvdbId">TVDB id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The specials, sorted by episode number.</returns>
    public async Task<List<SonarrEpisode>> GetSpecialsAsync(int tvdbId, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        var series = await _sonarr.GetSeriesAsync(config.SonarrUrl, config.SonarrApiKey, cancellationToken).ConfigureAwait(false);
        var match = series.FirstOrDefault(s => s.TvdbId == tvdbId);
        if (match is null)
        {
            return new List<SonarrEpisode>();
        }

        var episodes = await _sonarr.GetEpisodesAsync(config.SonarrUrl, config.SonarrApiKey, match.Id, cancellationToken).ConfigureAwait(false);
        return episodes.Where(e => e.SeasonNumber == 0).OrderBy(e => e.EpisodeNumber).ToList();
    }

    private static PluginConfiguration GetConfig()
        => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static Dictionary<int, SeriesRule> GetRules(PluginConfiguration config)
        => config.SeriesRules
            .Where(r => r.TvdbId > 0)
            .GroupBy(r => r.TvdbId)
            .ToDictionary(g => g.Key, g => g.First());

    private static bool IsChecked(PluginConfiguration config, SonarrSeries series, Dictionary<int, SeriesRule> rules)
    {
        if (config.OnlyMonitoredSeries && !series.Monitored)
        {
            return false;
        }

        if (series.IsAnime && !config.AnimeEnabled)
        {
            return false;
        }

        return !(rules.TryGetValue(series.TvdbId, out var rule) && rule.IgnoreSeries);
    }

    private static string FormatText(string template, int count)
        => (template ?? string.Empty).Replace("{count}", count.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

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

    private List<Season> GetSeasons(Guid seriesId)
        => _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Season],
                Recursive = true,
                IsVirtualItem = false,
            })
            .OfType<Season>()
            .Where(s => s.SeriesId == seriesId)
            .ToList();

    private async Task<(SeriesReport? Report, Dictionary<Guid, (BaseItem Item, string Text)> Desired)> ProcessSeriesAsync(
        SonarrSeries series,
        List<BaseItem> matches,
        List<Season> seasonsOfMatches,
        PluginConfiguration config,
        Dictionary<int, SeriesRule> rules,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var desired = new Dictionary<Guid, (BaseItem Item, string Text)>();
        rules.TryGetValue(series.TvdbId, out var rule);

        var episodes = await _sonarr.GetEpisodesAsync(config.SonarrUrl, config.SonarrApiKey, series.Id, cancellationToken).ConfigureAwait(false);

        FillerInfo? filler = null;
        if (series.IsAnime && NeedsFillerData(config, rule))
        {
            filler = await _filler.GetFillerAsync(config, series.Title, rule, cancellationToken).ConfigureAwait(false);
        }

        var result = MissingEpisodeDetector.Detect(series, episodes, config, rule, filler, now);
        if (!result.IsIncomplete)
        {
            return (null, desired);
        }

        var seriesReport = new SeriesReport
        {
            Title = series.Title,
            TvdbId = series.TvdbId,
            Status = series.Status,
            IsAnime = series.IsAnime,
            MissingCount = result.Missing.Count,
            IgnoredCount = result.IgnoredCount,
            FillerSource = filler?.Source,
            PosterStatus = config.MarkSeries ? string.Empty : "Series posters are not marked (setting)",
        };
        foreach (var (season, count) in result.MissingPerSeason)
        {
            seriesReport.MissingPerSeason[season] = count;
        }

        seriesReport.Missing.AddRange(result.Missing.Take(MaxEpisodesInReport));

        foreach (var jfSeries in matches)
        {
            if (config.MarkSeries)
            {
                desired[jfSeries.Id] = (jfSeries, FormatText(config.SeriesBannerText, result.Missing.Count));
            }

            if (config.MarkSeasons)
            {
                foreach (var season in seasonsOfMatches.Where(s => s.SeriesId == jfSeries.Id))
                {
                    if (season.IndexNumber is int number
                        && result.MissingPerSeason.TryGetValue(number, out var seasonCount))
                    {
                        desired[season.Id] = (season, FormatText(config.SeasonBannerText, seasonCount));
                    }
                    else if (config.MarkAllSeasonsOfIncompleteShows)
                    {
                        // Complete season of an incomplete show (e.g. the one "Recently added" shows).
                        desired[season.Id] = (season, FormatText(config.CompleteSeasonBannerText, result.Missing.Count));
                    }
                }
            }
        }

        return (seriesReport, desired);
    }

    private async Task ApplyDesiredAsync(
        Dictionary<Guid, (BaseItem Item, string Text)> desired,
        PluginConfiguration config,
        SeriesReport? seriesReport,
        Guid? seriesItemId,
        CancellationToken cancellationToken)
    {
        foreach (var (id, (item, text)) in desired)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A landscape "poster" is usually a video frame: fetch a real poster before marking it.
            if (config.RepairShowPosters && item is Series && _repair.Inspect(item) == PosterProblem.Landscape)
            {
                await _repair.RepairIfNeededAsync(item, cancellationToken).ConfigureAwait(false);
            }

            ApplyResult result;
            try
            {
                result = await _posters.ApplyAsync(item, text, config, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not mark poster for {Name}", item.Name);
                result = ApplyResult.RenderFailed;
            }

            if (seriesReport is not null && seriesItemId == id)
            {
                seriesReport.PosterStatus = Describe(result);
            }

            if (result == ApplyResult.NoPoster && config.RepairShowPosters && item is Series)
            {
                var (_, requested) = await _repair.RepairIfNeededAsync(item, cancellationToken).ConfigureAwait(false);
                if (requested && seriesReport is not null && seriesItemId == id)
                {
                    seriesReport.PosterStatus = "No poster; fetching one, marked when it arrives";
                }
            }
        }
    }

    private async Task<MarkerReport> RunInternalAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        var report = new MarkerReport();

        if (!config.MarkingEnabled)
        {
            await _posters.RestoreAllAsync(cancellationToken).ConfigureAwait(false);
            report.FinishedUtc = DateTime.UtcNow;
            report.Success = true;
            report.Message = "Marking is turned off; all posters restored.";
            return report;
        }

        progress.Report(1);
        var sonarrSeries = await _sonarr.GetSeriesAsync(config.SonarrUrl, config.SonarrApiKey, cancellationToken).ConfigureAwait(false);

        var jellyfinSeries = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = true,
            IsVirtualItem = false,
        });

        var allSeasons = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Season],
                Recursive = true,
                IsVirtualItem = false,
            })
            .OfType<Season>()
            .ToList();

        var index = BuildProviderIndex(jellyfinSeries);
        var rules = GetRules(config);

        var desired = new Dictionary<Guid, (BaseItem Item, string Text)>();
        var seriesItemToReport = new Dictionary<Guid, SeriesReport>();
        var now = DateTime.UtcNow;
        var done = 0;

        foreach (var series in sonarrSeries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            done++;
            progress.Report(1 + (80.0 * done / Math.Max(1, sonarrSeries.Count)));

            if (!IsChecked(config, series, rules))
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

            try
            {
                var (seriesReport, seriesDesired) = await ProcessSeriesAsync(
                    series, matches, allSeasons, config, rules, now, cancellationToken).ConfigureAwait(false);
                if (seriesReport is null)
                {
                    continue;
                }

                report.IncompleteSeries.Add(seriesReport);
                foreach (var (id, value) in seriesDesired)
                {
                    desired[id] = value;
                }

                foreach (var m in matches)
                {
                    seriesItemToReport[m.Id] = seriesReport;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not check series {Title}", series.Title);
            }
        }

        // Remember what should be marked, so markers also follow posters that arrive later.
        _posters.SetWanted(desired.ToDictionary(d => d.Key, d => d.Value.Text));

        // Apply markers.
        var applied = 0;
        foreach (var (id, value) in desired)
        {
            seriesItemToReport.TryGetValue(id, out var seriesReport);
            await ApplyDesiredAsync(
                new Dictionary<Guid, (BaseItem Item, string Text)> { [id] = value },
                config,
                seriesReport,
                seriesReport is null ? null : id,
                cancellationToken).ConfigureAwait(false);
            applied++;
            progress.Report(81 + (9.0 * applied / Math.Max(1, desired.Count)));
        }

        // Fix missing or wrong posters (image only).
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
                    report.PostersRepaired++;
                }
            }
        }

        if (config.RepairMoviePosters)
        {
            var movies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie],
                Recursive = true,
                IsVirtualItem = false,
            });
            var checkedMovies = 0;
            foreach (var movie in movies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (_, requested) = await _repair.RepairIfNeededAsync(movie, cancellationToken).ConfigureAwait(false);
                if (requested)
                {
                    report.PostersRepaired++;
                }

                checkedMovies++;
                progress.Report(90 + (5.0 * checkedMovies / Math.Max(1, movies.Count)));
            }
        }

        // Restore everything that should no longer be marked (now complete, rule changed, deleted...).
        foreach (var id in _posters.GetMarkedItemIds())
        {
            if (!desired.ContainsKey(id))
            {
                await _posters.RestoreAsync(id, cancellationToken).ConfigureAwait(false);
            }
        }

        progress.Report(100);
        report.IncompleteSeries.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        report.PostersMarked = _posters.GetMarkedItemIds().Count;
        report.FinishedUtc = DateTime.UtcNow;
        report.Success = true;
        report.Message = string.Format(
            CultureInfo.InvariantCulture,
            "Checked {0} series: {1} incomplete, {2} posters marked{3}.",
            report.SeriesChecked,
            report.IncompleteSeries.Count,
            report.PostersMarked,
            report.PostersRepaired > 0 ? ", " + report.PostersRepaired + " posters fetched again" : string.Empty);
        _logger.LogInformation("Missing Episode Marker: {Message}", report.Message);
        return report;
    }

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
