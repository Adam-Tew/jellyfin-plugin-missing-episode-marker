using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Posters;

/// <summary>What is wrong with a poster, if anything.</summary>
public enum PosterProblem
{
    /// <summary>The poster looks fine.</summary>
    None = 0,

    /// <summary>There is no poster at all.</summary>
    Missing = 1,

    /// <summary>The poster is landscape, which usually means a frame grabbed from the video.</summary>
    Landscape = 2,
}

/// <summary>
/// Finds missing or wrong posters and asks Jellyfin to fetch a new poster (image only, no other metadata).
/// Each item is retried at most once every few days, so items without any poster online
/// don't cause endless refreshes.
/// </summary>
public class PosterRepairService
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly PosterMarkerService _posters;
    private readonly ILogger<PosterRepairService> _logger;
    private readonly string _attemptsFile;
    private readonly object _lock = new();
    private Dictionary<Guid, DateTime>? _attempts;

    /// <summary>
    /// Initializes a new instance of the <see cref="PosterRepairService"/> class.
    /// </summary>
    /// <param name="providerManager">Provider manager.</param>
    /// <param name="fileSystem">File system.</param>
    /// <param name="posters">Poster marker service.</param>
    /// <param name="paths">Plugin paths.</param>
    /// <param name="logger">Logger.</param>
    public PosterRepairService(
        IProviderManager providerManager,
        IFileSystem fileSystem,
        PosterMarkerService posters,
        PluginPaths paths,
        ILogger<PosterRepairService> logger)
    {
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _posters = posters;
        _logger = logger;
        _attemptsFile = Path.Combine(paths.DataDirectory, "poster-repairs.json");
    }

    /// <summary>Checks an item's poster.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The problem, if any.</returns>
    public PosterProblem Inspect(BaseItem item)
    {
        var image = item.GetImageInfo(ImageType.Primary, 0);
        if (image is null)
        {
            return PosterProblem.Missing;
        }

        // Our own marked copies keep the original's shape, but never "repair" them.
        if (_posters.GetEntry(item.Id) is not null)
        {
            return PosterProblem.None;
        }

        // Width/height are filled in by Jellyfin once it has read the image; 0 means unknown.
        if (image.Width > 0 && image.Height > 0 && image.Width > image.Height * 1.05)
        {
            return PosterProblem.Landscape;
        }

        return PosterProblem.None;
    }

    /// <summary>
    /// Asks Jellyfin to fetch a new poster for an item if its poster is missing or looks wrong.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The problem found and whether a refresh was done.</returns>
    public async Task<(PosterProblem Problem, bool Requested)> RepairIfNeededAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var problem = Inspect(item);
        if (problem == PosterProblem.None)
        {
            return (problem, false);
        }

        lock (_lock)
        {
            var attempts = LoadAttempts();
            if (attempts.TryGetValue(item.Id, out var last) && DateTime.UtcNow - last < RetryAfter)
            {
                return (problem, false);
            }

            attempts[item.Id] = DateTime.UtcNow;
            SaveAttempts();
        }

        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.None,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllImages = false,
            ReplaceAllMetadata = false,

            // Missing: just fill the gap. Landscape: replace only the poster, nothing else.
            ReplaceImages = problem == PosterProblem.Landscape ? new[] { ImageType.Primary } : Array.Empty<ImageType>(),
        };

        _logger.LogInformation(
            "Poster of {Name} is {Problem}; asking Jellyfin to fetch a new one",
            item.Name,
            problem == PosterProblem.Missing ? "missing" : "landscape (probably a video frame)");
        try
        {
            // RefreshSingleItem touches only this item. (A queued refresh of a series would also
            // refresh every season and episode with the same options, replacing episode thumbnails.)
            await _providerManager.RefreshSingleItem(item, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not refresh the poster of {Name}", item.Name);
        }

        return (problem, true);
    }

    private Dictionary<Guid, DateTime> LoadAttempts()
    {
        if (_attempts is not null)
        {
            return _attempts;
        }

        try
        {
            if (File.Exists(_attemptsFile))
            {
                _attempts = JsonSerializer.Deserialize<Dictionary<Guid, DateTime>>(File.ReadAllText(_attemptsFile), JsonOptions);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Poster repair history unreadable; starting fresh");
        }

        _attempts ??= new Dictionary<Guid, DateTime>();

        // Forget old entries so the file doesn't grow forever.
        var cutoff = DateTime.UtcNow - RetryAfter;
        foreach (var key in new List<Guid>(_attempts.Keys))
        {
            if (_attempts[key] < cutoff)
            {
                _attempts.Remove(key);
            }
        }

        return _attempts;
    }

    private void SaveAttempts()
    {
        try
        {
            File.WriteAllText(_attemptsFile, JsonSerializer.Serialize(_attempts, JsonOptions));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not save poster repair history");
        }
    }
}
