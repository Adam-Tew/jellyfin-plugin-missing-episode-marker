using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MissingEpisodeMarker.Configuration;
using Jellyfin.Plugin.MissingEpisodeMarker.Filler;
using Jellyfin.Plugin.MissingEpisodeMarker.Sonarr;
using Jellyfin.Plugin.MissingEpisodeMarker.Util;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Detection;

/// <summary>One missing episode that counts towards a marker.</summary>
public class MissingEpisode
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

    /// <summary>Gets or sets a value indicating whether this is a special.</summary>
    public bool IsSpecial { get; set; }

    /// <summary>Gets or sets a value indicating whether this is filler.</summary>
    public bool IsFiller { get; set; }
}

/// <summary>Detection result for one series.</summary>
public class DetectionResult
{
    /// <summary>Gets the counted missing episodes.</summary>
    public List<MissingEpisode> Missing { get; } = new List<MissingEpisode>();

    /// <summary>Gets the number of missing episodes per season number.</summary>
    public Dictionary<int, int> MissingPerSeason { get; } = new Dictionary<int, int>();

    /// <summary>Gets or sets how many missing episodes were ignored (grace period, filler, specials...).</summary>
    public int IgnoredCount { get; set; }

    /// <summary>Gets a value indicating whether the series is incomplete.</summary>
    public bool IsIncomplete => Missing.Count > 0;
}

/// <summary>
/// Decides which missing episodes count. Pure logic: no I/O, no Jellyfin types.
/// </summary>
public static class MissingEpisodeDetector
{
    /// <summary>Runs detection for one series.</summary>
    /// <param name="series">The Sonarr series.</param>
    /// <param name="episodes">Its episodes.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="rule">The series rule, if any.</param>
    /// <param name="filler">Filler data (anime), if any.</param>
    /// <param name="nowUtc">Current time (UTC).</param>
    /// <returns>The result.</returns>
    public static DetectionResult Detect(
        SonarrSeries series,
        IEnumerable<SonarrEpisode> episodes,
        PluginConfiguration config,
        SeriesRule? rule,
        FillerInfo? filler,
        DateTime nowUtc)
    {
        var result = new DetectionResult();

        var countSpecials = ShouldCountSpecials(config.SpecialsMode, rule?.Specials ?? SpecialsOverride.Default);
        var selectedSpecials = config.SpecialsSource == SpecialsSource.PluginSelection
            ? NumberRangeParser.Parse(rule?.SpecialEpisodes)
            : new HashSet<int>();

        var countFiller = (rule?.Filler ?? FillerOverride.Default) switch
        {
            FillerOverride.Count => true,
            FillerOverride.Ignore => false,
            _ => config.CountMissingFiller,
        };

        var applyGrace = !series.IsEnded || config.ApplyGraceToEndedShows;
        var graceCutoff = GraceCutoff(nowUtc, config.GracePeriodAmount, config.GracePeriodUnit);

        foreach (var ep in episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber))
        {
            if (ep.HasFile)
            {
                continue;
            }

            var isSpecial = ep.SeasonNumber == 0;

            // Which episodes are "wanted" at all.
            if (isSpecial)
            {
                if (!countSpecials)
                {
                    if (ep.Monitored)
                    {
                        result.IgnoredCount++;
                    }

                    continue;
                }

                if (config.SpecialsSource == SpecialsSource.PluginSelection)
                {
                    // Plugin decides; an empty list means every special of the series.
                    if (selectedSpecials.Count > 0 && !selectedSpecials.Contains(ep.EpisodeNumber))
                    {
                        continue;
                    }
                }
                else if (!ep.Monitored)
                {
                    continue;
                }
            }
            else if (!ep.Monitored)
            {
                continue;
            }

            // Not aired yet, or no date at all (TBA): never counts.
            if (ep.AirDateUtc is null || ep.AirDateUtc.Value > nowUtc)
            {
                continue;
            }

            // Grace period for ongoing shows.
            if (applyGrace && ep.AirDateUtc.Value > graceCutoff)
            {
                result.IgnoredCount++;
                continue;
            }

            // Anime filler.
            var isFiller = series.IsAnime
                && filler is not null
                && ep.AbsoluteEpisodeNumber.HasValue
                && filler.IsFiller(ep.AbsoluteEpisodeNumber.Value, config.TreatMixedAsFiller);
            if (isFiller && !countFiller)
            {
                result.IgnoredCount++;
                continue;
            }

            result.Missing.Add(new MissingEpisode
            {
                Season = ep.SeasonNumber,
                Episode = ep.EpisodeNumber,
                Absolute = ep.AbsoluteEpisodeNumber,
                Title = ep.Title,
                AirDateUtc = ep.AirDateUtc,
                IsSpecial = isSpecial,
                IsFiller = isFiller,
            });

            result.MissingPerSeason.TryGetValue(ep.SeasonNumber, out var count);
            result.MissingPerSeason[ep.SeasonNumber] = count + 1;
        }

        return result;
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

    /// <summary>Resolves whether specials count for a series.</summary>
    /// <param name="mode">Global mode.</param>
    /// <param name="seriesOverride">Series override.</param>
    /// <returns>True when specials count.</returns>
    public static bool ShouldCountSpecials(SpecialsMode mode, SpecialsOverride seriesOverride)
    {
        // The global toggle wins: with specials off, nothing about specials counts.
        if (mode == SpecialsMode.Off)
        {
            return false;
        }

        return seriesOverride switch
        {
            SpecialsOverride.Include => true,
            SpecialsOverride.Exclude => false,
            _ => mode == SpecialsMode.AllSeries,
        };
    }
}
