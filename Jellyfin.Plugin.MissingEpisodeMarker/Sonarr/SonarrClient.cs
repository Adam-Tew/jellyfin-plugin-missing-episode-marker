using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Sonarr;

/// <summary>
/// Minimal Sonarr v3 API client.
/// </summary>
public class SonarrClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SonarrClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    public SonarrClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Gets all series.</summary>
    /// <param name="baseUrl">Sonarr base URL.</param>
    /// <param name="apiKey">API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The series.</returns>
    public Task<List<SonarrSeries>> GetSeriesAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
        => GetAsync<List<SonarrSeries>>(baseUrl, apiKey, "api/v3/series", cancellationToken);

    /// <summary>Gets all episodes of one series.</summary>
    /// <param name="baseUrl">Sonarr base URL.</param>
    /// <param name="apiKey">API key.</param>
    /// <param name="seriesId">Sonarr series id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The episodes.</returns>
    public Task<List<SonarrEpisode>> GetEpisodesAsync(string baseUrl, string apiKey, int seriesId, CancellationToken cancellationToken)
        => GetAsync<List<SonarrEpisode>>(
            baseUrl,
            apiKey,
            "api/v3/episode?seriesId=" + seriesId.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

    /// <summary>
    /// Gets every monitored, aired episode without a file (Sonarr's Wanted → Missing), across all series.
    /// </summary>
    /// <param name="baseUrl">Sonarr base URL.</param>
    /// <param name="apiKey">API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The episodes.</returns>
    public Task<List<SonarrEpisode>> GetMissingAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
        => GetAllPagesAsync<SonarrEpisode>(baseUrl, apiKey, "api/v3/wanted/missing?monitored=true&includeSeries=false&sortKey=airDateUtc&sortDirection=ascending", cancellationToken);

    /// <summary>Gets Sonarr's download queue, one record per episode.</summary>
    /// <param name="baseUrl">Sonarr base URL.</param>
    /// <param name="apiKey">API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The queue.</returns>
    public Task<List<SonarrQueueItem>> GetQueueAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
        => GetAllPagesAsync<SonarrQueueItem>(baseUrl, apiKey, "api/v3/queue?includeEpisode=true&includeUnknownSeriesItems=false", cancellationToken);

    private async Task<List<T>> GetAllPagesAsync<T>(string baseUrl, string apiKey, string path, CancellationToken cancellationToken)
    {
        const int PageSize = 1000;
        const int MaxPages = 100;
        var all = new List<T>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var url = path + "&page=" + page.ToString(CultureInfo.InvariantCulture) + "&pageSize=" + PageSize.ToString(CultureInfo.InvariantCulture);
            var result = await GetAsync<SonarrPage<T>>(baseUrl, apiKey, url, cancellationToken).ConfigureAwait(false);
            all.AddRange(result.Records);
            if (result.Records.Count < PageSize || all.Count >= result.TotalRecords)
            {
                break;
            }
        }

        return all;
    }

    private async Task<T> GetAsync<T>(string baseUrl, string apiKey, string path, CancellationToken cancellationToken)
        where T : new()
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Sonarr URL and API key must be set in the plugin settings.");
        }

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/" + path);
        request.Headers.Add("X-Api-Key", apiKey.Trim());

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(string.Format(
                CultureInfo.InvariantCulture,
                "Sonarr returned {0} ({1}) for {2}",
                (int)response.StatusCode,
                response.ReasonPhrase,
                path));
        }

        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return result ?? new T();
    }
}
