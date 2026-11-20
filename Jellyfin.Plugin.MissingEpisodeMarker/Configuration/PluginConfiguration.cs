using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Configuration;

/// <summary>Where special episodes (season 0) are selected from.</summary>
public enum SpecialsSource
{
    /// <summary>A special counts when Sonarr has it monitored.</summary>
    SonarrMonitoring = 0,

    /// <summary>A special counts when it is selected in the plugin (per-series list, empty = all).</summary>
    PluginSelection = 1,
}

/// <summary>Which series have their specials considered by default.</summary>
public enum SpecialsMode
{
    /// <summary>Specials never count (series rules for specials are ignored).</summary>
    Off = 0,

    /// <summary>Specials count for every series, unless a series rule says Exclude.</summary>
    AllSeries = 1,

    /// <summary>Specials only count for series that have an Include rule.</summary>
    OnlyIncludedSeries = 2,
}

/// <summary>Per-series override for specials.</summary>
public enum SpecialsOverride
{
    /// <summary>Follow the global setting.</summary>
    Default = 0,

    /// <summary>Always consider this series' specials.</summary>
    Include = 1,

    /// <summary>Never consider this series' specials.</summary>
    Exclude = 2,
}

/// <summary>Per-series override for anime filler.</summary>
public enum FillerOverride
{
    /// <summary>Follow the global setting.</summary>
    Default = 0,

    /// <summary>Missing filler episodes count as missing for this series.</summary>
    Count = 1,

    /// <summary>Missing filler episodes are ignored for this series.</summary>
    Ignore = 2,
}

/// <summary>Where the banner is drawn on the poster.</summary>
public enum BannerPosition
{
    /// <summary>A strip across the top.</summary>
    Top = 0,

    /// <summary>A strip across the bottom.</summary>
    Bottom = 1,

    /// <summary>A strip running diagonally across the middle.</summary>
    Diagonal = 2,

    /// <summary>A ribbon across the top-right corner.</summary>
    CornerRibbon = 3,
}

/// <summary>
/// A rule that changes how one particular series is handled.
/// </summary>
public class SeriesRule
{
    /// <summary>Gets or sets the TVDB id of the series (how the rule is matched).</summary>
    public int TvdbId { get; set; }

    /// <summary>Gets or sets the series title (for display only).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this series is never marked.</summary>
    public bool IgnoreSeries { get; set; }

    /// <summary>Gets or sets the specials override.</summary>
    public SpecialsOverride Specials { get; set; } = SpecialsOverride.Default;

    /// <summary>
    /// Gets or sets which special episode numbers count, e.g. "1,3,5-7".
    /// Only used when <see cref="PluginConfiguration.SpecialsSource"/> is PluginSelection. Empty means all.
    /// </summary>
    public string SpecialEpisodes { get; set; } = string.Empty;

    /// <summary>Gets or sets the filler override (anime only).</summary>
    public FillerOverride Filler { get; set; } = FillerOverride.Default;

    /// <summary>Gets or sets the animefillerlist slug, e.g. "naruto-shippuden". Empty = detect automatically.</summary>
    public string FillerSlug { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a manual filler list by absolute episode number, e.g. "26,97-106".
    /// When set, it is used instead of the website.
    /// </summary>
    public string ManualFiller { get; set; } = string.Empty;
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ---------------- Sonarr ----------------

    /// <summary>Gets or sets the Sonarr URL as the Jellyfin server reaches it, e.g. http://sonarr:8989.</summary>
    public string SonarrUrl { get; set; } = "http://sonarr:8989";

    /// <summary>Gets or sets the Sonarr API key.</summary>
    public string SonarrApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether series Sonarr does not monitor are skipped.</summary>
    public bool OnlyMonitoredSeries { get; set; } = true;

    // ---------------- Detection ----------------

    /// <summary>Gets or sets the grace period in weeks before a missing episode of an ongoing show counts.</summary>
    public int GracePeriodWeeks { get; set; } = 4;

    /// <summary>Gets or sets a value indicating whether the grace period also applies to ended shows.</summary>
    public bool ApplyGraceToEndedShows { get; set; }

    /// <summary>Gets or sets which series consider specials.</summary>
    public SpecialsMode SpecialsMode { get; set; } = SpecialsMode.Off;

    /// <summary>Gets or sets where specials are selected from.</summary>
    public SpecialsSource SpecialsSource { get; set; } = SpecialsSource.SonarrMonitoring;

    // ---------------- Anime ----------------

    /// <summary>Gets or sets a value indicating whether anime series (Sonarr type "anime") are checked at all.</summary>
    public bool AnimeEnabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether missing filler episodes count as missing.</summary>
    public bool CountMissingFiller { get; set; }

    /// <summary>Gets or sets a value indicating whether "mixed canon/filler" episodes are treated as filler.</summary>
    public bool TreatMixedAsFiller { get; set; }

    /// <summary>Gets or sets a value indicating whether filler data is looked up online.</summary>
    public bool FillerLookupEnabled { get; set; } = true;

    /// <summary>Gets or sets the filler website base URL.</summary>
    public string FillerSourceUrl { get; set; } = "https://www.animefillerlist.com";

    /// <summary>Gets or sets how many days fetched filler data is reused before it is fetched again.</summary>
    public int FillerRefreshDays { get; set; } = 7;

    // ---------------- Marking ----------------

    /// <summary>Gets or sets a value indicating whether marking is enabled. When off, all posters are restored.</summary>
    public bool MarkingEnabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a check also runs after every library scan.</summary>
    public bool RunAfterLibraryScan { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether series posters are marked.</summary>
    public bool MarkSeries { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether season posters are marked.</summary>
    public bool MarkSeasons { get; set; } = true;

    // ---------------- Appearance ----------------

    /// <summary>Gets or sets a value indicating whether a colour filter is laid over the whole poster.</summary>
    public bool FilterEnabled { get; set; } = true;

    /// <summary>Gets or sets the filter colour (hex).</summary>
    public string FilterColor { get; set; } = "#000000";

    /// <summary>Gets or sets the filter opacity, 0-100.</summary>
    public int FilterOpacity { get; set; } = 35;

    /// <summary>Gets or sets a value indicating whether the poster is turned grey (desaturated).</summary>
    public bool Grayscale { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the banner is drawn.</summary>
    public bool BannerEnabled { get; set; } = true;

    /// <summary>Gets or sets the banner position.</summary>
    public BannerPosition BannerPosition { get; set; } = BannerPosition.Bottom;

    /// <summary>Gets or sets the banner colour (hex).</summary>
    public string BannerColor { get; set; } = "#C62828";

    /// <summary>Gets or sets the banner opacity, 0-100.</summary>
    public int BannerOpacity { get; set; } = 90;

    /// <summary>Gets or sets the banner height as a percentage of the poster height.</summary>
    public int BannerSizePercent { get; set; } = 11;

    /// <summary>Gets or sets the text colour (hex).</summary>
    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>Gets or sets the series banner text. {count} is replaced by the number of missing episodes.</summary>
    public string SeriesBannerText { get; set; } = "MISSING EPISODES";

    /// <summary>Gets or sets the season banner text. {count} is replaced by the number of missing episodes.</summary>
    public string SeasonBannerText { get; set; } = "MISSING {count}";

    // ---------------- Rules ----------------

    /// <summary>Gets or sets the per-series rules.</summary>
    public List<SeriesRule> SeriesRules { get; set; } = new List<SeriesRule>();
}
