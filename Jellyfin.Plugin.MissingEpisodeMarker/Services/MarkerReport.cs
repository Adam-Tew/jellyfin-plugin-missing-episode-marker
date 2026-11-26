using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Services;

/// <summary>Result of the last check, shown on the settings page.</summary>
public class MarkerReport
{
    /// <summary>Gets or sets when the check finished (UTC).</summary>
    public DateTime? FinishedUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether the check succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets a status or error message.</summary>
    public string Message { get; set; } = "Not checked yet.";

    /// <summary>Gets or sets what started the check (interval, webhook, library change, ...).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets how many monitored series were checked.</summary>
    public int SeriesChecked { get; set; }

    /// <summary>Gets or sets how many Sonarr series have no matching series in Jellyfin.</summary>
    public int SeriesNotInJellyfin { get; set; }

    /// <summary>Gets or sets how many unmonitored series were skipped.</summary>
    public int SeriesUnmonitored { get; set; }

    /// <summary>Gets or sets how many posters the plugin asked Jellyfin to fetch again.</summary>
    public int PostersRepaired { get; set; }

    /// <summary>Gets or sets how many posters are currently marked.</summary>
    public int PostersMarked { get; set; }

    /// <summary>Gets the series that have a marker.</summary>
    public List<SeriesReport> Series { get; } = new List<SeriesReport>();
}

/// <summary>One series with a marker.</summary>
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

    /// <summary>Gets or sets the show's marker (Missing, Stalled, Downloading, Incoming).</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets how many episodes are missing (red).</summary>
    public int Missing { get; set; }

    /// <summary>Gets or sets how many episodes are stalled (yellow).</summary>
    public int Stalled { get; set; }

    /// <summary>Gets or sets how many episodes are downloading (purple).</summary>
    public int Downloading { get; set; }

    /// <summary>Gets or sets how many episodes are on the way (blue).</summary>
    public int Incoming { get; set; }

    /// <summary>Gets or sets how many missing episodes are still within the grace period.</summary>
    public int WithinGrace { get; set; }

    /// <summary>Gets or sets where filler data came from.</summary>
    public string? FillerSource { get; set; }

    /// <summary>Gets or sets what happened to the show poster.</summary>
    public string PosterStatus { get; set; } = string.Empty;

    /// <summary>Gets the seasons with a marker.</summary>
    public List<SeasonReport> Seasons { get; } = new List<SeasonReport>();

    /// <summary>Gets the episodes behind the markers (capped).</summary>
    public List<EpisodeReport> Episodes { get; } = new List<EpisodeReport>();
}

/// <summary>One season with a marker.</summary>
public class SeasonReport
{
    /// <summary>Gets or sets the season number.</summary>
    public int Season { get; set; }

    /// <summary>Gets or sets the marker.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets how many episodes have that marker.</summary>
    public int Count { get; set; }
}

/// <summary>One episode behind a marker.</summary>
public class EpisodeReport
{
    /// <summary>Gets or sets the season number.</summary>
    public int Season { get; set; }

    /// <summary>Gets or sets the episode number.</summary>
    public int Episode { get; set; }

    /// <summary>Gets or sets the absolute number.</summary>
    public int? Absolute { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the air date (UTC).</summary>
    public DateTime? AirDateUtc { get; set; }

    /// <summary>Gets or sets the marker this episode causes.</summary>
    public string State { get; set; } = string.Empty;
}
