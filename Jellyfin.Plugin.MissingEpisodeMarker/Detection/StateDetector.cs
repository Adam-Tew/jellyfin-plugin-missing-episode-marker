using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MissingEpisodeMarker.Configuration;
using Jellyfin.Plugin.MissingEpisodeMarker.Filler;
using Jellyfin.Plugin.MissingEpisodeMarker.Sonarr;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Detection;

/// <summary>One episode that contributes to a marker.</summary>
public class EpisodeState
{
    /// <summary>Gets or sets the season number.</summary>
    public int Season { get; set; }

    /// <summary>Gets or sets the episode number.</summary>
    public int Episode { get; set; }

    /// <summary>Gets or sets the absolute number, if known.</summary>
    public int? Absolute { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the air date (UTC).</summary>
    public DateTime? AirDateUtc { get; set; }

    /// <summary>Gets or sets the marker this episode causes.</summary>
    public MarkerState State { get; set; }

    /// <summary>Gets or sets a value indicating whether the episode is filler.</summary>
    public bool IsFiller { get; set; }
}

/// <summary>The marker for one season.</summary>
public class SeasonState
{
    /// <summary>Gets or sets the season number (0 = specials).</summary>
    public int Season { get; set; }

    /// <summary>Gets or sets the marker (the most important among its episodes).</summary>
    public MarkerState State { get; set; }

    /// <summary>Gets or sets how many episodes have that marker state.</summary>
    public int Count { get; set; }
}

/// <summary>Result for one series.</summary>
public class SeriesState
{
    /// <summary>Gets the episodes that cause a marker.</summary>
    public List<EpisodeState> Episodes { get; } = new List<EpisodeState>();

    /// <summary>Gets the marker per season (only seasons with a marker).</summary>
    public Dictionary<int, SeasonState> Seasons { get; } = new Dictionary<int, SeasonState>();

    /// <summary>Gets or sets the marker for the whole show (the most important season marker).</summary>
    public MarkerState State { get; set; }

    /// <summary>Gets or sets how many episodes have the show's marker state.</summary>
    public int Count { get; set; }

    /// <summary>Gets or sets how many missing episodes are still within the grace period (not marked).</summary>
    public int WithinGraceCount { get; set; }
}

/// <summary>
/// Works out the marker for a series from Sonarr's missing list and download queue.
/// Pure logic: no I/O, no Jellyfin types.
/// </summary>
/// <remarks>
/// Per episode:
///   in the queue and stalled                          → Stalled (yellow)
///   in the queue, missing past the grace period       → Downloading (purple)
///   in the queue, within the grace period / not aired → Incoming (blue)
///   not in the queue, missing past the grace period   → Missing (red)
///   not in the queue, within the grace period         → nothing.
/// A season gets its most important episode state, the show its most important season state
/// (red &gt; yellow &gt; purple &gt; blue). Unmonitored episodes never reach this code:
/// Sonarr's missing list only contains monitored episodes, and queue items for unmonitored ones are skipped.
/// </remarks>
public static class StateDetector
{
    /// <summary>Runs detection for one series.</summary>
    /// <param name="series">The Sonarr series.</param>
    /// <param name="missing">Its monitored, aired episodes without a file (from Sonarr's Wanted → Missing).</param>
    /// <param name="queue">Its queue items.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="rule">The anime rule, if any.</param>
    /// <param name="filler">Filler data (anime), if any.</param>
    /// <param name="nowUtc">Current time (UTC).</param>
    /// <returns>The result.</returns>
    public static SeriesState Detect(
        SonarrSeries series,
        IEnumerable<SonarrEpisode> missing,
        IEnumerable<SonarrQueueItem> queue,
        PluginConfiguration config,
        SeriesRule? rule,
        FillerInfo? filler,
        DateTime nowUtc)
    {
        var result = new SeriesState();

        var countFiller = (rule?.Filler ?? FillerOverride.Default) switch
        {
            FillerOverride.Count => true,
            FillerOverride.Ignore => false,
            _ => config.CountMissingFiller,
        };

        var applyGrace = !series.IsEnded || config.ApplyGraceToEndedShows;
        var cutoff = GraceCutoff(nowUtc, config.GracePeriodAmount, config.GracePeriodUnit);

        // Queue state per episode. Several queue records for one episode: a stalled one wins.
        var queued = new Dictionary<int, (SonarrQueueItem Item, SonarrEpisode Episode)>();
        foreach (var q in queue)
        {
            var ep = q.Episode;
            if (ep is null || q.EpisodeId is null)
            {
                continue;
            }

            // Skip unmonitored episodes (manual grabs) and upgrades of episodes we already have.
            if (!ep.Monitored || ep.HasFile)
            {
                continue;
            }

            if (!queued.TryGetValue(q.EpisodeId.Value, out var existing) || (q.IsStalled && !existing.Item.IsStalled))
            {
                queued[q.EpisodeId.Value] = (q, ep);
            }
        }

        // Every relevant episode: everything missing, plus queued episodes not in the missing list (e.g. not aired yet).
        var episodes = new Dictionary<int, SonarrEpisode>();
        foreach (var ep in missing)
        {
            if (ep.Monitored && !ep.HasFile)
            {
                episodes[ep.Id] = ep;
            }
        }

        foreach (var (id, value) in queued)
        {
            episodes.TryAdd(id, value.Episode);
        }

        foreach (var ep in episodes.Values.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber))
        {
            var isFiller = series.IsAnime
                && filler is not null
                && ep.AbsoluteEpisodeNumber.HasValue
                && filler.IsFiller(ep.AbsoluteEpisodeNumber.Value, config.TreatMixedAsFiller);
            if (isFiller && !countFiller)
            {
                continue;
            }

            var aired = ep.AirDateUtc is DateTime air && air <= nowUtc;
            var pastGrace = aired && (!applyGrace || ep.AirDateUtc!.Value <= cutoff);

            MarkerState raw;
            if (queued.TryGetValue(ep.Id, out var q))
            {
                raw = q.Item.IsStalled ? MarkerState.Stalled
                    : pastGrace ? MarkerState.Downloading
                    : MarkerState.Incoming;
            }
            else
            {
                if (!pastGrace)
                {
                    if (aired)
                    {
                        result.WithinGraceCount++;
                    }

                    continue;
                }

                raw = MarkerState.Missing;
            }

            var state = Resolve(raw, pastGrace, config);
            if (state == MarkerState.None)
            {
                continue;
            }

            result.Episodes.Add(new EpisodeState
            {
                Season = ep.SeasonNumber,
                Episode = ep.EpisodeNumber,
                Absolute = ep.AbsoluteEpisodeNumber,
                Title = ep.Title,
                AirDateUtc = ep.AirDateUtc,
                State = state,
                IsFiller = isFiller,
            });
        }

        // Seasons: most important episode state; count = episodes with that state.
        foreach (var group in result.Episodes.GroupBy(e => e.Season))
        {
            var top = group.Max(e => e.State);
            result.Seasons[group.Key] = new SeasonState
            {
                Season = group.Key,
                State = top,
                Count = group.Count(e => e.State == top),
            };
        }

        // Show: most important season state.
        if (result.Seasons.Count > 0)
        {
            result.State = result.Seasons.Values.Max(s => s.State);
            result.Count = result.Episodes.Count(e => e.State == result.State);
        }

        return result;
    }

    /// <summary>
    /// Applies the on/off switches: a disabled marker falls back to the next sensible one.
    /// Stalled → red if past the grace period, otherwise blue (or nothing). Downloading → red. Incoming → nothing.
    /// </summary>
    /// <param name="raw">The state before switches.</param>
    /// <param name="pastGrace">Whether the episode is past the grace period.</param>
    /// <param name="config">Configuration.</param>
    /// <returns>The state to use.</returns>
    public static MarkerState Resolve(MarkerState raw, bool pastGrace, PluginConfiguration config)
    {
        switch (raw)
        {
            case MarkerState.Stalled when !config.StalledEnabled:
                return pastGrace ? MarkerState.Missing : (config.IncomingEnabled ? MarkerState.Incoming : MarkerState.None);
            case MarkerState.Downloading when !config.DownloadingEnabled:
                return MarkerState.Missing;
            case MarkerState.Incoming when !config.IncomingEnabled:
                return MarkerState.None;
            default:
                return raw;
        }
    }

    /// <summary>Works out the latest air date that is past the grace period.</summary>
    /// <param name="nowUtc">Current time.</param>
    /// <param name="amount">Grace amount.</param>
    /// <param name="unit">Grace unit.</param>
    /// <returns>Episodes that aired after this moment are still within the grace period.</returns>
    public static DateTime GraceCutoff(DateTime nowUtc, int amount, TimeUnit unit)
    {
        var n = Math.Clamp(amount, 0, 1000);
        return unit switch
        {
            TimeUnit.Days => nowUtc.AddDays(-n),
            TimeUnit.Months => nowUtc.AddMonths(-n),
            TimeUnit.Years => nowUtc.AddYears(-Math.Min(n, 100)),
            _ => nowUtc.AddDays(-7.0 * n),
        };
    }
}
