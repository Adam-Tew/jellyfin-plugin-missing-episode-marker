using System;
using System.Linq;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MissingEpisodeMarker.Configuration;
using Jellyfin.Plugin.MissingEpisodeMarker.Filler;
using Jellyfin.Plugin.MissingEpisodeMarker.Posters;
using Jellyfin.Plugin.MissingEpisodeMarker.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Api;

/// <summary>
/// Endpoints used by the settings page (admin only) and Sonarr's webhook (secret token).
/// </summary>
[ApiController]
[Route("MissingEpisodeMarker")]
[Authorize(Policy = Policies.RequiresElevation)]
public class MissingEpisodeMarkerController : ControllerBase
{
    private readonly MarkerRunner _runner;
    private readonly FillerService _filler;
    private readonly SyncScheduler _scheduler;
    private readonly ILibraryManager _libraryManager;
    private readonly PosterMarkerService _posters;

    /// <summary>
    /// Initializes a new instance of the <see cref="MissingEpisodeMarkerController"/> class.
    /// </summary>
    /// <param name="runner">Runner.</param>
    /// <param name="filler">Filler service.</param>
    /// <param name="scheduler">Check scheduler.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="posters">Poster service.</param>
    public MissingEpisodeMarkerController(
        MarkerRunner runner,
        FillerService filler,
        SyncScheduler scheduler,
        ILibraryManager libraryManager,
        PosterMarkerService posters)
    {
        _runner = runner;
        _filler = filler;
        _scheduler = scheduler;
        _libraryManager = libraryManager;
        _posters = posters;
    }

    /// <summary>Gets the last run report.</summary>
    /// <returns>The report.</returns>
    [HttpGet("Report")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetReport()
        => Ok(new
        {
            Version = Plugin.Instance?.Version?.ToString(),
            IsRunning = _runner.IsRunning,
            Progress = _runner.Progress,
            EtaSeconds = _runner.EtaSeconds,
            NextCheckUtc = _scheduler.NextDueUtc,
            Report = _runner.LastReport,
        });

    /// <summary>Starts a check now (including the poster repair pass).</summary>
    /// <returns>No content.</returns>
    [HttpPost("Run")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Run()
    {
        _scheduler.RequestSync(TimeSpan.Zero, "Check now", fullRepair: true);
        return NoContent();
    }

    /// <summary>
    /// Sonarr's webhook ("Connect" → Webhook). Any event (grab, import, delete, ...) starts a check a few seconds later.
    /// Not behind a Jellyfin login, so it is protected by the secret token in the URL instead.
    /// </summary>
    /// <param name="token">The webhook token from the settings page.</param>
    /// <returns>OK, or 401 when the token is wrong.</returns>
    [AllowAnonymous]
    [HttpPost("Webhook")]
    [HttpPut("Webhook")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult Webhook([FromQuery] string? token)
    {
        var expected = Plugin.Instance?.Configuration.WebhookToken;
        if (string.IsNullOrEmpty(expected)
            || string.IsNullOrEmpty(token)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected)))
        {
            return Unauthorized();
        }

        _scheduler.RequestSync(TimeSpan.FromSeconds(5), "Sonarr webhook");
        return Ok(new { Status = "Check scheduled" });
    }

    /// <summary>Creates a new webhook token (the old webhook URL stops working).</summary>
    /// <returns>The new token.</returns>
    [HttpPost("RegenerateWebhookToken")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> RegenerateWebhookToken()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        plugin.Configuration.WebhookToken = Plugin.NewWebhookToken();
        plugin.SaveConfiguration();
        return Ok(new { Token = plugin.Configuration.WebhookToken });
    }

    /// <summary>Restores every original poster.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("RemoveAll")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> RemoveAll(CancellationToken cancellationToken)
    {
        await _runner.RemoveAllMarkersAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Lists Sonarr series (also works as a connection test).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The series.</returns>
    [HttpGet("SonarrSeries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<object>> GetSonarrSeries(CancellationToken cancellationToken)
    {
        try
        {
            var series = await _runner.GetSonarrSeriesAsync(cancellationToken).ConfigureAwait(false);
            return Ok(series.Select(s => new
            {
                s.Title,
                s.TvdbId,
                s.SeriesType,
                s.Status,
                s.Monitored,
            }));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { Error = ex.Message });
        }
    }

    /// <summary>Clears cached filler data so it is fetched again on the next run.</summary>
    /// <returns>No content.</returns>
    [HttpPost("ClearFillerCache")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearFillerCache()
    {
        _filler.ClearCache();
        return NoContent();
    }

    /// <summary>Renders a preview of the marker with the given (unsaved) settings.</summary>
    /// <param name="config">The settings to preview.</param>
    /// <param name="state">Which marker to preview.</param>
    /// <returns>A JPEG image.</returns>
    [HttpPost("Preview")]
    [Produces(MediaTypeNames.Image.Jpeg)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Preview([FromBody] PluginConfiguration config, [FromQuery] MarkerState state = MarkerState.Missing)
    {
        if (state == MarkerState.None)
        {
            state = MarkerState.Missing;
        }

        var style = MarkerStyle.For(config, state);
        var bytes = PosterRenderer.RenderPreview(FindSamplePoster(), style, style.FormatText(3));
        return File(bytes, MediaTypeNames.Image.Jpeg);
    }

    private string? FindSamplePoster()
    {
        // Use a real, unmarked series poster so the preview looks realistic.
        var marked = _posters.GetMarkedItemIds().ToHashSet();
        var series = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = true,
            IsVirtualItem = false,
            Limit = 25,
        });

        foreach (var item in series)
        {
            if (marked.Contains(item.Id))
            {
                continue;
            }

            var image = item.GetImageInfo(ImageType.Primary, 0);
            if (image is not null && image.IsLocalFile && System.IO.File.Exists(image.Path))
            {
                return image.Path;
            }
        }

        return null;
    }
}
