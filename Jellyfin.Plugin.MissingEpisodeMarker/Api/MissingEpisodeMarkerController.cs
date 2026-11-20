using System;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MissingEpisodeMarker.Configuration;
using Jellyfin.Plugin.MissingEpisodeMarker.Filler;
using Jellyfin.Plugin.MissingEpisodeMarker.Posters;
using Jellyfin.Plugin.MissingEpisodeMarker.Services;
using Jellyfin.Plugin.MissingEpisodeMarker.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Api;

/// <summary>
/// Admin-only endpoints used by the settings page.
/// </summary>
[ApiController]
[Route("MissingEpisodeMarker")]
[Authorize(Policy = Policies.RequiresElevation)]
public class MissingEpisodeMarkerController : ControllerBase
{
    private readonly MarkerRunner _runner;
    private readonly FillerService _filler;
    private readonly ITaskManager _taskManager;
    private readonly ILibraryManager _libraryManager;
    private readonly PosterMarkerService _posters;

    /// <summary>
    /// Initializes a new instance of the <see cref="MissingEpisodeMarkerController"/> class.
    /// </summary>
    /// <param name="runner">Runner.</param>
    /// <param name="filler">Filler service.</param>
    /// <param name="taskManager">Task manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="posters">Poster service.</param>
    public MissingEpisodeMarkerController(
        MarkerRunner runner,
        FillerService filler,
        ITaskManager taskManager,
        ILibraryManager libraryManager,
        PosterMarkerService posters)
    {
        _runner = runner;
        _filler = filler;
        _taskManager = taskManager;
        _libraryManager = libraryManager;
        _posters = posters;
    }

    /// <summary>Gets the last run report.</summary>
    /// <returns>The report.</returns>
    [HttpGet("Report")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetReport()
        => Ok(new { IsRunning = _runner.IsRunning, Report = _runner.LastReport });

    /// <summary>Starts a run now (as the scheduled task, so progress shows in Scheduled Tasks).</summary>
    /// <returns>No content.</returns>
    [HttpPost("Run")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Run()
    {
        _taskManager.QueueScheduledTask<MarkMissingEpisodesTask>();
        return NoContent();
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
    /// <param name="season">True to preview the season text instead of the series text.</param>
    /// <returns>A JPEG image.</returns>
    [HttpPost("Preview")]
    [Produces(MediaTypeNames.Image.Jpeg)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Preview([FromBody] PluginConfiguration config, [FromQuery] bool season = false)
    {
        var template = season ? config.SeasonBannerText : config.SeriesBannerText;
        var text = (template ?? string.Empty).Replace("{count}", "3", StringComparison.OrdinalIgnoreCase);
        var bytes = PosterRenderer.RenderPreview(FindSamplePoster(), config, text);
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
