using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Configuration;

/// <summary>Per-anime override for filler.</summary>
public enum FillerOverride
{
    /// <summary>Follow the global setting.</summary>
    Default = 0,

    /// <summary>Missing filler episodes count for this anime.</summary>
    Count = 1,

    /// <summary>Missing filler episodes are ignored for this anime.</summary>
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

/// <summary>Unit for the grace period.</summary>
public enum TimeUnit
{
    /// <summary>Days.</summary>
    Days = 0,

    /// <summary>Weeks.</summary>
    Weeks = 1,

    /// <summary>Months.</summary>
    Months = 2,

    /// <summary>Years.</summary>
    Years = 3,
}

/// <summary>
/// The kind of marker, ordered by importance (a higher value wins when combining seasons into a show).
/// </summary>
public enum MarkerState
{
    /// <summary>Nothing to show.</summary>
    None = 0,

    /// <summary>Blue: the show is complete and a new episode (within the grace period) is downloading.</summary>
    Incoming = 1,

    /// <summary>Purple: missing episodes (past the grace period) are all being downloaded.</summary>
    Downloading = 2,

    /// <summary>Yellow: a download is stalled or needs attention.</summary>
    Stalled = 3,

    /// <summary>Red: episodes are missing and nothing is fetching them.</summary>
    Missing = 4,
}

/// <summary>
/// A rule for one anime: how its filler is handled.
/// (Everything else follows Sonarr: unmonitored episodes and series are ignored.)
/// </summary>
public class SeriesRule
{
    /// <summary>Gets or sets the TVDB id of the series (how the rule is matched).</summary>
    public int TvdbId { get; set; }

    /// <summary>Gets or sets the series title (for display only).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the series is anime.</summary>
    public bool IsAnime { get; set; } = true;

    /// <summary>Gets or sets the filler override.</summary>
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

    /// <summary>Gets or sets the secret key Sonarr's webhook must send. Generated on first start.</summary>
    public string WebhookToken { get; set; } = string.Empty;

    // ---------------- When to check ----------------

    /// <summary>Gets or sets how often (minutes) the plugin asks Sonarr for changes. 0 = never on a timer.</summary>
    public int CheckIntervalMinutes { get; set; } = 2;

    /// <summary>Gets or sets a value indicating whether the plugin reacts to library changes in Jellyfin.</summary>
    public bool ReactToLibraryChanges { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a check also runs after every library scan.</summary>
    public bool RunAfterLibraryScan { get; set; } = true;

    // ---------------- Detection ----------------

    /// <summary>Gets or sets the grace period amount before a missing episode counts.</summary>
    public int GracePeriodAmount { get; set; } = 4;

    /// <summary>Gets or sets the grace period unit.</summary>
    public TimeUnit GracePeriodUnit { get; set; } = TimeUnit.Weeks;

    /// <summary>Gets or sets a value indicating whether the grace period also applies to shows that have ended.</summary>
    public bool ApplyGraceToEndedShows { get; set; }

    // ---------------- Anime ----------------

    /// <summary>Gets or sets a value indicating whether anime series (Sonarr type "anime") are checked at all.</summary>
    public bool AnimeEnabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether missing filler episodes count.</summary>
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

    /// <summary>Gets or sets a value indicating whether series posters are marked.</summary>
    public bool MarkSeries { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether season posters are marked.</summary>
    public bool MarkSeasons { get; set; } = true;

    // ---------------- Poster repair ----------------

    /// <summary>Gets or sets a value indicating whether missing or landscape show posters are fetched again (image only).</summary>
    public bool RepairShowPosters { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether missing or landscape movie posters are fetched again.</summary>
    public bool RepairMoviePosters { get; set; }

    /// <summary>Gets or sets a value indicating whether the plugin has an entry in the dashboard menu.</summary>
    public bool ShowInMenu { get; set; }

    // ---------------- Appearance (shared) ----------------

    /// <summary>Gets or sets a value indicating whether a banner is drawn.</summary>
    public bool BannerEnabled { get; set; } = true;

    /// <summary>Gets or sets the banner position.</summary>
    public BannerPosition BannerPosition { get; set; } = BannerPosition.Bottom;

    /// <summary>Gets or sets the banner opacity, 0-100.</summary>
    public int BannerOpacity { get; set; } = 90;

    /// <summary>Gets or sets the banner height as a percentage of the poster height.</summary>
    public int BannerSizePercent { get; set; } = 11;

    // ---------------- Red: missing ----------------

    /// <summary>Gets or sets a value indicating whether "missing" posters are turned grey.</summary>
    public bool Grayscale { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a colour filter is laid over "missing" posters.</summary>
    public bool FilterEnabled { get; set; } = true;

    /// <summary>Gets or sets the filter colour (hex).</summary>
    public string FilterColor { get; set; } = "#000000";

    /// <summary>Gets or sets the filter opacity, 0-100.</summary>
    public int FilterOpacity { get; set; } = 35;

    /// <summary>Gets or sets the "missing" banner colour.</summary>
    public string MissingColor { get; set; } = "#C62828";

    /// <summary>Gets or sets the "missing" text colour.</summary>
    public string MissingTextColor { get; set; } = "#FFFFFF";

    /// <summary>Gets or sets the "missing" text. {count} = number of episodes.</summary>
    public string MissingText { get; set; } = "MISSING EPISODES";

    // ---------------- Yellow: stalled ----------------

    /// <summary>Gets or sets a value indicating whether the "stalled" marker is used.</summary>
    public bool StalledEnabled { get; set; } = true;

    /// <summary>Gets or sets the "stalled" banner colour.</summary>
    public string StalledColor { get; set; } = "#F9A825";

    /// <summary>Gets or sets the "stalled" text colour.</summary>
    public string StalledTextColor { get; set; } = "#1A1A1A";

    /// <summary>Gets or sets the "stalled" text.</summary>
    public string StalledText { get; set; } = "DOWNLOAD STALLED";

    // ---------------- Purple: downloading ----------------

    /// <summary>Gets or sets a value indicating whether the "downloading" marker is used.</summary>
    public bool DownloadingEnabled { get; set; } = true;

    /// <summary>Gets or sets the "downloading" banner colour.</summary>
    public string DownloadingColor { get; set; } = "#7B1FA2";

    /// <summary>Gets or sets the "downloading" text colour.</summary>
    public string DownloadingTextColor { get; set; } = "#FFFFFF";

    /// <summary>Gets or sets the "downloading" text.</summary>
    public string DownloadingText { get; set; } = "DOWNLOADING";

    // ---------------- Blue: new episode on the way ----------------

    /// <summary>Gets or sets a value indicating whether the "new episode on the way" marker is used.</summary>
    public bool IncomingEnabled { get; set; } = true;

    /// <summary>Gets or sets the "new episode on the way" banner colour.</summary>
    public string IncomingColor { get; set; } = "#1E88E5";

    /// <summary>Gets or sets the "new episode on the way" text colour.</summary>
    public string IncomingTextColor { get; set; } = "#FFFFFF";

    /// <summary>Gets or sets the "new episode on the way" text.</summary>
    public string IncomingText { get; set; } = "NEW EPISODE ON THE WAY";

    // ---------------- Rules ----------------

    /// <summary>Gets or sets the per-anime filler rules.</summary>
    public List<SeriesRule> SeriesRules { get; set; } = new List<SeriesRule>();
}
