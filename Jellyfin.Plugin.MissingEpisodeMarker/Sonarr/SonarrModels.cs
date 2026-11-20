using System;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Sonarr;

/// <summary>A series as returned by Sonarr's /api/v3/series.</summary>
public class SonarrSeries
{
    /// <summary>Gets or sets the Sonarr id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the TVDB id.</summary>
    public int TvdbId { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the IMDb id.</summary>
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets the status: continuing, ended, upcoming or deleted.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the series type: standard, daily or anime.</summary>
    public string SeriesType { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the series is monitored.</summary>
    public bool Monitored { get; set; }

    /// <summary>Gets a value indicating whether the series has finished airing.</summary>
    public bool IsEnded => string.Equals(Status, "ended", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets a value indicating whether this is an anime series.</summary>
    public bool IsAnime => string.Equals(SeriesType, "anime", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An episode as returned by Sonarr's /api/v3/episode.</summary>
public class SonarrEpisode
{
    /// <summary>Gets or sets the Sonarr id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the series id.</summary>
    public int SeriesId { get; set; }

    /// <summary>Gets or sets the season number (0 = specials).</summary>
    public int SeasonNumber { get; set; }

    /// <summary>Gets or sets the episode number within the season.</summary>
    public int EpisodeNumber { get; set; }

    /// <summary>Gets or sets the absolute episode number (mainly anime).</summary>
    public int? AbsoluteEpisodeNumber { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the air date in UTC.</summary>
    public DateTime? AirDateUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether a file exists.</summary>
    public bool HasFile { get; set; }

    /// <summary>Gets or sets a value indicating whether the episode is monitored.</summary>
    public bool Monitored { get; set; }
}
