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
/// Runs a full check: asks Sonarr, decides what is incomplete, and marks/restores posters.
/// </summary>
public class MarkerRunner
{
    private const int MaxEpisodesInReport = 25;

    private readonly ILibraryManager _libraryManager;
    private readonly SonarrClient _sonarr;
    private readonly FillerService _filler;
    private readonly PosterMarkerService _posters;
    private readonly ILogger<MarkerRunner> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkerRunner"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="sonarr">Sonarr client.</param>
    /// <param name="filler">Filler service.</param>
    /// <param name="posters">Poster marker service.</param>
    /// <param name="logger">Logger.</param>
    public MarkerRunner(
        ILibraryManager libraryManager,
        SonarrClient sonarr,
        FillerService filler,
        PosterMarkerService posters,
        ILogger<MarkerRunner> logger)
    {
        _libraryManager = libraryManager;
        _sonarr = sonarr;
        _filler = filler;
        _posters = posters;
        _logger = logger;
    }

    /// <summary>Gets the report of the last run.</summary>
    public MarkerReport LastReport { get; private set; } = new MarkerReport();

    /// <summary>Gets a value indicating whether a run is in progress.</summary>
    public bool IsRunning => _runLock.CurrentCount == 0;

    /// <summary>Runs a full check. If a run is already in progress, this waits for it.</summary>
    /// <param name="progress">Progress (0-100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastReport = await RunInternalAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Missing Episode Marker run failed");
            LastReport = new MarkerReport
            {
                FinishedUtc = DateTime.UtcNow,
                Success = false,
                Message = ex.Message,
                PostersMarked = _posters.GetMarkedItemIds().Count,
            };
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
            LastReport = new MarkerReport
            {
                FinishedUtc = DateTime.UtcNow,
                Success = true,
                Message = "All markers removed. They come back on the next run unless marking is turned off.",
            };
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

    private static PluginConfiguration GetConfig()
        => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static string FormatText(string template, int count)
        => (template ?? string.Empty).Replace("{count}", count.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

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

        var seasonsBySeries = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Season],
                Recursive = true,
                IsVirtualItem = false,
            })
            .OfType<Season>()
            .GroupBy(s => s.SeriesId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var index = BuildProviderIndex(jellyfinSeries);
        var rules = config.SeriesRules
            .Where(r => r.TvdbId > 0)
            .GroupBy(r => r.TvdbId)
            .ToDictionary(g => g.Key, g => g.First());

        // itemId -> banner text, for everything that should be marked after this run.
        var desired = new Dictionary<Guid, (BaseItem Item, string Text)>();
        var now = DateTime.UtcNow;
        var done = 0;

        foreach (var series in sonarrSeries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            done++;
            progress.Report(1 + (80.0 * done / Math.Max(1, sonarrSeries.Count)));

            if (config.OnlyMonitoredSeries && !series.Monitored)
            {
                continue;
            }

            if (series.IsAnime && !config.AnimeEnabled)
            {
                continue;
            }

            rules.TryGetValue(series.TvdbId, out var rule);
            if (rule?.IgnoreSeries == true)
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
                var episodes = await _sonarr.GetEpisodesAsync(config.SonarrUrl, config.SonarrApiKey, series.Id, cancellationToken).ConfigureAwait(false);

                FillerInfo? filler = null;
                if (series.IsAnime && NeedsFillerData(config, rule))
                {
                    filler = await _filler.GetFillerAsync(config, series.Title, rule, cancellationToken).ConfigureAwait(false);
                }

                var result = MissingEpisodeDetector.Detect(series, episodes, config, rule, filler, now);
                if (!result.IsIncomplete)
                {
                    continue;
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
                };
                foreach (var (season, count) in result.MissingPerSeason)
                {
                    seriesReport.MissingPerSeason[season] = count;
                }

                seriesReport.Missing.AddRange(result.Missing.Take(MaxEpisodesInReport));
                report.IncompleteSeries.Add(seriesReport);

                foreach (var jfSeries in matches)
                {
                    if (config.MarkSeries)
                    {
                        desired[jfSeries.Id] = (jfSeries, FormatText(config.SeriesBannerText, result.Missing.Count));
                    }

                    if (config.MarkSeasons && seasonsBySeries.TryGetValue(jfSeries.Id, out var seasons))
                    {
                        foreach (var season in seasons)
                        {
                            if (season.IndexNumber is int number
                                && result.MissingPerSeason.TryGetValue(number, out var seasonCount))
                            {
                                desired[season.Id] = (season, FormatText(config.SeasonBannerText, seasonCount));
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

        // Apply markers.
        var applied = 0;
        foreach (var (_, (item, text)) in desired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _posters.ApplyAsync(item, text, config, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not mark poster for {Name}", item.Name);
            }

            applied++;
            progress.Report(81 + (14.0 * applied / Math.Max(1, desired.Count)));
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
            "Checked {0} series: {1} incomplete, {2} posters marked.",
            report.SeriesChecked,
            report.IncompleteSeries.Count,
            report.PostersMarked);
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
}
