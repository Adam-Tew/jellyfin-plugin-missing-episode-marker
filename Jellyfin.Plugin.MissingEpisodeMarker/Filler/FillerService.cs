using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MissingEpisodeMarker.Configuration;
using Jellyfin.Plugin.MissingEpisodeMarker.Util;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Filler;

/// <summary>Filler information for one show, by absolute episode number.</summary>
public class FillerInfo
{
    /// <summary>Gets or sets the pure filler episodes.</summary>
    public HashSet<int> Filler { get; set; } = new HashSet<int>();

    /// <summary>Gets or sets the mixed canon/filler episodes.</summary>
    public HashSet<int> Mixed { get; set; } = new HashSet<int>();

    /// <summary>Gets or sets when the data was fetched (UTC).</summary>
    public DateTime FetchedUtc { get; set; }

    /// <summary>Gets or sets where the data came from ("manual", a slug, or "none").</summary>
    public string Source { get; set; } = "none";

    /// <summary>Checks whether an episode counts as filler.</summary>
    /// <param name="absoluteNumber">Absolute episode number.</param>
    /// <param name="treatMixedAsFiller">Whether mixed episodes count as filler.</param>
    /// <returns>True when the episode is filler.</returns>
    public bool IsFiller(int absoluteNumber, bool treatMixedAsFiller)
        => Filler.Contains(absoluteNumber) || (treatMixedAsFiller && Mixed.Contains(absoluteNumber));
}

/// <summary>
/// Looks up anime filler episodes from a filler list website (animefillerlist.com layout),
/// with an on-disk cache and a manual fallback per series.
/// </summary>
public class FillerService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    // <tr class="filler even"> ... <td class="Number">26</td>
    private static readonly Regex RowRegex = new(
        "<tr[^>]*class=\"(?<cls>[^\"]*)\"[^>]*>(?<body>.*?)</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex FirstCellRegex = new(
        "<td[^>]*>(?<cell>.*?)</td>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    // <a href="/shows/naruto-shippuden">Naruto Shippuden</a>
    private static readonly Regex ShowLinkRegex = new(
        "<a[^>]*href=\"(?:https?://[^\"/]+)?/shows/(?<slug>[a-z0-9\\-]+)\"[^>]*>(?<name>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FillerService> _logger;
    private readonly string _cacheFile;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private FillerCache? _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="FillerService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="paths">Plugin data paths.</param>
    /// <param name="logger">Logger.</param>
    public FillerService(IHttpClientFactory httpClientFactory, PluginPaths paths, ILogger<FillerService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cacheFile = Path.Combine(paths.DataDirectory, "filler-cache.json");
    }

    /// <summary>
    /// Gets filler data for a series. Manual lists win; otherwise the website is used (cached).
    /// Never throws: on failure the last known data (or nothing) is returned.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="seriesTitle">Series title, used for automatic slug detection.</param>
    /// <param name="rule">The series rule, if any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Filler info.</returns>
    public async Task<FillerInfo> GetFillerAsync(
        PluginConfiguration config,
        string seriesTitle,
        SeriesRule? rule,
        CancellationToken cancellationToken)
    {
        if (rule is not null && !string.IsNullOrWhiteSpace(rule.ManualFiller))
        {
            return new FillerInfo
            {
                Filler = NumberRangeParser.Parse(rule.ManualFiller),
                FetchedUtc = DateTime.UtcNow,
                Source = "manual",
            };
        }

        if (!config.FillerLookupEnabled)
        {
            return new FillerInfo();
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = LoadCache();
            var slug = rule is not null && !string.IsNullOrWhiteSpace(rule.FillerSlug)
                ? rule.FillerSlug.Trim().ToLowerInvariant()
                : await FindSlugAsync(config, cache, seriesTitle, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(slug))
            {
                return new FillerInfo();
            }

            var maxAge = TimeSpan.FromDays(Math.Max(1, config.FillerRefreshDays));
            if (cache.Shows.TryGetValue(slug, out var cached) && DateTime.UtcNow - cached.FetchedUtc < maxAge)
            {
                return cached;
            }

            var fresh = await FetchShowAsync(config, slug, cancellationToken).ConfigureAwait(false);
            if (fresh is not null)
            {
                cache.Shows[slug] = fresh;
                SaveCache(cache);
                return fresh;
            }

            // Fetch failed: keep using old data if we have any.
            return cached ?? new FillerInfo();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Removes all cached filler data.</summary>
    public void ClearCache()
    {
        _cache = new FillerCache();
        try
        {
            if (File.Exists(_cacheFile))
            {
                File.Delete(_cacheFile);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete filler cache");
        }
    }

    /// <summary>Normalizes a title for matching.</summary>
    /// <param name="title">The title.</param>
    /// <returns>Lowercase letters and digits separated by single spaces.</returns>
    public static string NormalizeTitle(string title)
    {
        var sb = new StringBuilder(title.Length);
        var lastWasSpace = true;
        foreach (var ch in WebUtility.HtmlDecode(title).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>Parses a show page into filler info. Public for testing.</summary>
    /// <param name="html">Page HTML.</param>
    /// <returns>The parsed info.</returns>
    public static FillerInfo ParseShowPage(string html)
    {
        var info = new FillerInfo { FetchedUtc = DateTime.UtcNow };
        foreach (Match row in RowRegex.Matches(html))
        {
            var cls = row.Groups["cls"].Value.ToLowerInvariant();
            var isMixed = cls.Contains("mixed", StringComparison.Ordinal);
            var isFiller = !isMixed && cls.Contains("filler", StringComparison.Ordinal);
            if (!isMixed && !isFiller)
            {
                continue;
            }

            var cell = FirstCellRegex.Match(row.Groups["body"].Value);
            if (!cell.Success)
            {
                continue;
            }

            var text = WebUtility.HtmlDecode(TagRegex.Replace(cell.Groups["cell"].Value, string.Empty)).Trim();
            NumberRangeParser.AddPart(text, isMixed ? info.Mixed : info.Filler);
        }

        return info;
    }

    private async Task<string?> FindSlugAsync(
        PluginConfiguration config,
        FillerCache cache,
        string seriesTitle,
        CancellationToken cancellationToken)
    {
        var maxAge = TimeSpan.FromDays(Math.Max(1, config.FillerRefreshDays));
        if (cache.ShowIndex.Count == 0 || DateTime.UtcNow - cache.ShowIndexFetchedUtc > maxAge)
        {
            var index = await FetchShowIndexAsync(config, cancellationToken).ConfigureAwait(false);
            if (index is not null && index.Count > 0)
            {
                cache.ShowIndex = index;
                cache.ShowIndexFetchedUtc = DateTime.UtcNow;
                SaveCache(cache);
            }
        }

        var wanted = NormalizeTitle(seriesTitle);
        if (wanted.Length == 0)
        {
            return null;
        }

        // 1. Exact normalized title.
        if (cache.ShowIndex.TryGetValue(wanted, out var exact))
        {
            return exact;
        }

        // 2. Same title without spaces ("Dragonball Z" vs "Dragon Ball Z").
        var wantedNoSpace = wanted.Replace(" ", string.Empty, StringComparison.Ordinal);
        foreach (var (name, slug) in cache.ShowIndex)
        {
            if (name.Replace(" ", string.Empty, StringComparison.Ordinal) == wantedNoSpace)
            {
                return slug;
            }
        }

        // 3. Slug built from the title.
        var guess = wanted.Replace(' ', '-');
        foreach (var slug in cache.ShowIndex.Values)
        {
            if (slug == guess)
            {
                return slug;
            }
        }

        return null;
    }

    private async Task<Dictionary<string, string>?> FetchShowIndexAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var html = await DownloadAsync(config.FillerSourceUrl.TrimEnd('/') + "/shows", cancellationToken).ConfigureAwait(false);
        if (html is null)
        {
            return null;
        }

        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in ShowLinkRegex.Matches(html))
        {
            var name = NormalizeTitle(TagRegex.Replace(m.Groups["name"].Value, string.Empty));
            var slug = m.Groups["slug"].Value.ToLowerInvariant();
            if (name.Length > 0)
            {
                index.TryAdd(name, slug);
            }
        }

        _logger.LogInformation("Filler show index loaded: {Count} shows", index.Count);
        return index;
    }

    private async Task<FillerInfo?> FetchShowAsync(PluginConfiguration config, string slug, CancellationToken cancellationToken)
    {
        // Be polite to the site: small pause between page loads.
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

        var html = await DownloadAsync(
            config.FillerSourceUrl.TrimEnd('/') + "/shows/" + Uri.EscapeDataString(slug),
            cancellationToken).ConfigureAwait(false);
        if (html is null)
        {
            return null;
        }

        var info = ParseShowPage(html);
        info.Source = slug;
        _logger.LogInformation(
            "Filler data for {Slug}: {Filler} filler, {Mixed} mixed",
            slug,
            info.Filler.Count,
            info.Mixed.Count);
        return info;
    }

    private async Task<string?> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Filler source returned {Status} for {Url}", (int)response.StatusCode, url);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning(ex, "Could not load filler data from {Url}", url);
            return null;
        }
    }

    private FillerCache LoadCache()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            if (File.Exists(_cacheFile))
            {
                _cache = JsonSerializer.Deserialize<FillerCache>(File.ReadAllText(_cacheFile), JsonOptions);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Filler cache unreadable, starting fresh");
        }

        _cache ??= new FillerCache();
        return _cache;
    }

    private void SaveCache(FillerCache cache)
    {
        try
        {
            File.WriteAllText(_cacheFile, JsonSerializer.Serialize(cache, JsonOptions));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not save filler cache");
        }
    }

    private sealed class FillerCache
    {
        public Dictionary<string, FillerInfo> Shows { get; set; } = new Dictionary<string, FillerInfo>(StringComparer.Ordinal);

        public Dictionary<string, string> ShowIndex { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public DateTime ShowIndexFetchedUtc { get; set; }
    }
}
