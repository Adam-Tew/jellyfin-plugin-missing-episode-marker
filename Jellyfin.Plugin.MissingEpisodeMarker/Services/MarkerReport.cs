using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MissingEpisodeMarker.Detection;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Services;

/// <summary>Result of the last run, shown on the settings page.</summary>
public class MarkerReport
{
    /// <summary>Gets or sets when the run finished (UTC).</summary>
    public DateTime? FinishedUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether the run succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets a status or error message.</summary>
    public string Message { get; set; } = "Not run yet.";

    /// <summary>Gets or sets how many series were checked.</summary>
    public int SeriesChecked { get; set; }

    /// <summary>Gets or sets how many Sonarr series have no matching series in Jellyfin.</summary>
    public int SeriesNotInJellyfin { get; set; }

    /// <summary>Gets or sets how many posters the plugin asked Jellyfin to fetch again during the run.</summary>
    public int PostersRepaired { get; set; }

    /// <summary>Gets or sets how many posters are currently marked.</summary>
    public int PostersMarked { get; set; }

    /// <summary>Gets the incomplete series.</summary>
    public List<SeriesReport> IncompleteSeries { get; } = new List<SeriesReport>();
}

/// <summary>One incomplete series in the report.</summary>
public class SeriesReport
{
    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the TVDB id.</summary>
    public int TvdbId { get; set; }

    /// <summary>Gets or sets the Sonarr status.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether it is anime.</summary>
    public bool IsAnime { get; set; }

    /// <summary>Gets or sets the number of counted missing episodes.</summary>
    public int MissingCount { get; set; }

    /// <summary>Gets or sets the number of ignored missing episodes.</summary>
    public int IgnoredCount { get; set; }

    /// <summary>Gets or sets where filler data came from.</summary>
    public string? FillerSource { get; set; }

    /// <summary>Gets or sets what happened to the series poster (Marked, NoPoster, NotLocal, ...).</summary>
    public string PosterStatus { get; set; } = string.Empty;

    /// <summary>Gets the missing count per season.</summary>
    public Dictionary<int, int> MissingPerSeason { get; } = new Dictionary<int, int>();

    /// <summary>Gets the first missing episodes (capped).</summary>
    public List<MissingEpisode> Missing { get; } = new List<MissingEpisode>();
}
