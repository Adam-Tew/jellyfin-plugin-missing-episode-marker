using System.Globalization;
using Jellyfin.Plugin.MissingEpisodeMarker.Configuration;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Posters;

/// <summary>How one marker looks. Built from the configuration for a given marker state.</summary>
public class MarkerStyle
{
    /// <summary>Gets or sets a value indicating whether the poster is turned grey (missing only).</summary>
    public bool Grayscale { get; set; }

    /// <summary>Gets or sets a value indicating whether the colour filter is drawn (missing only).</summary>
    public bool FilterEnabled { get; set; }

    /// <summary>Gets or sets the filter colour.</summary>
    public string FilterColor { get; set; } = "#000000";

    /// <summary>Gets or sets the filter opacity (0-100).</summary>
    public int FilterOpacity { get; set; }

    /// <summary>Gets or sets a value indicating whether the banner is drawn.</summary>
    public bool BannerEnabled { get; set; } = true;

    /// <summary>Gets or sets the banner position.</summary>
    public BannerPosition BannerPosition { get; set; } = BannerPosition.Bottom;

    /// <summary>Gets or sets the banner opacity (0-100).</summary>
    public int BannerOpacity { get; set; } = 90;

    /// <summary>Gets or sets the banner thickness in percent of the poster height.</summary>
    public int BannerSizePercent { get; set; } = 11;

    /// <summary>Gets or sets the banner colour.</summary>
    public string BannerColor { get; set; } = "#C62828";

    /// <summary>Gets or sets the text colour.</summary>
    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>Gets or sets the banner text template ({count} is replaced).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Builds the style for a marker state.</summary>
    /// <param name="config">Configuration.</param>
    /// <param name="state">Marker state.</param>
    /// <returns>The style.</returns>
    public static MarkerStyle For(PluginConfiguration config, MarkerState state)
    {
        var style = new MarkerStyle
        {
            BannerEnabled = config.BannerEnabled,
            BannerPosition = config.BannerPosition,
            BannerOpacity = config.BannerOpacity,
            BannerSizePercent = config.BannerSizePercent,
        };

        switch (state)
        {
            case MarkerState.Stalled:
                style.BannerColor = config.StalledColor;
                style.TextColor = config.StalledTextColor;
                style.Text = config.StalledText;
                break;
            case MarkerState.Downloading:
                style.BannerColor = config.DownloadingColor;
                style.TextColor = config.DownloadingTextColor;
                style.Text = config.DownloadingText;
                break;
            case MarkerState.Incoming:
                style.BannerColor = config.IncomingColor;
                style.TextColor = config.IncomingTextColor;
                style.Text = config.IncomingText;
                break;
            default:
                style.Grayscale = config.Grayscale;
                style.FilterEnabled = config.FilterEnabled;
                style.FilterColor = config.FilterColor;
                style.FilterOpacity = config.FilterOpacity;
                style.BannerColor = config.MissingColor;
                style.TextColor = config.MissingTextColor;
                style.Text = config.MissingText;
                break;
        }

        return style;
    }

    /// <summary>Fills in {count} in the banner text.</summary>
    /// <param name="count">Episode count.</param>
    /// <returns>The text to draw.</returns>
    public string FormatText(int count)
        => (Text ?? string.Empty).Replace("{count}", count.ToString(CultureInfo.InvariantCulture), System.StringComparison.OrdinalIgnoreCase).Trim();

    /// <summary>A short string describing everything that affects the drawing, used to spot changes.</summary>
    /// <returns>The signature source.</returns>
    public string Describe()
        => string.Join(
            '|',
            Grayscale,
            FilterEnabled,
            FilterColor,
            FilterOpacity,
            BannerEnabled,
            BannerPosition,
            BannerOpacity,
            BannerSizePercent,
            BannerColor,
            TextColor);
}
