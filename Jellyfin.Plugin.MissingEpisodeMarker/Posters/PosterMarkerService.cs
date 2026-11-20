using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MissingEpisodeMarker.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Posters;

/// <summary>
/// Swaps an item's primary image for a marked copy, and back again.
/// The original image file is never modified or deleted; only the path Jellyfin points to changes.
/// </summary>
public class PosterMarkerService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILibraryManager _libraryManager;
    private readonly PluginPaths _paths;
    private readonly ILogger<PosterMarkerService> _logger;
    private readonly string _stateFile;
    private readonly object _stateLock = new();
    private Dictionary<Guid, MarkerEntry>? _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="PosterMarkerService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="paths">Plugin paths.</param>
    /// <param name="logger">Logger.</param>
    public PosterMarkerService(ILibraryManager libraryManager, PluginPaths paths, ILogger<PosterMarkerService> logger)
    {
        _libraryManager = libraryManager;
        _paths = paths;
        _logger = logger;
        _stateFile = Path.Combine(paths.DataDirectory, "marked-items.json");
        Current = this;
    }

    /// <summary>Gets the running instance (used when the plugin is uninstalled).</summary>
    public static PosterMarkerService? Current { get; private set; }

    /// <summary>Gets the ids of all currently marked items.</summary>
    /// <returns>The ids.</returns>
    public IReadOnlyCollection<Guid> GetMarkedItemIds()
    {
        lock (_stateLock)
        {
            return LoadState().Keys.ToList();
        }
    }

    /// <summary>
    /// Marks an item's poster (or updates the marker if settings/text changed).
    /// </summary>
    /// <param name="item">The series or season.</param>
    /// <param name="text">Banner text.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the item is marked afterwards.</returns>
    public async Task<bool> ApplyAsync(BaseItem item, string text, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var image = item.GetImageInfo(ImageType.Primary, 0);
        if (image is null)
        {
            // No poster at all (e.g. a season using the series poster). Nothing to mark.
            return false;
        }

        MarkerEntry? entry;
        lock (_stateLock)
        {
            LoadState().TryGetValue(item.Id, out entry);
        }

        string originalPath;
        if (entry is not null && PathEquals(image.Path, entry.MarkedPath))
        {
            originalPath = entry.OriginalPath;
        }
        else
        {
            // First time, or Jellyfin replaced the poster since we marked it (e.g. a metadata refresh).
            if (!image.IsLocalFile)
            {
                _logger.LogDebug("Poster of {Name} is not a local file, skipping", item.Name);
                return false;
            }

            if (IsInsidePosterDirectory(image.Path))
            {
                // Points at one of our files but we have no record of the original (state lost).
                // Never mark a marked poster; leave it for a metadata refresh to fix.
                _logger.LogWarning(
                    "Poster of {Name} is a marked copy with no known original; refresh its metadata to get the original back",
                    item.Name);
                return false;
            }

            originalPath = image.Path;
            if (entry is not null)
            {
                DeleteFileQuietly(entry.MarkedPath);
            }
        }

        if (!File.Exists(originalPath))
        {
            _logger.LogWarning("Original poster of {Name} not found at {Path}", item.Name, originalPath);
            return false;
        }

        var signature = ComputeSignature(originalPath, config, text);
        if (entry is not null
            && entry.Signature == signature
            && PathEquals(image.Path, entry.MarkedPath)
            && File.Exists(entry.MarkedPath))
        {
            return true; // Already up to date.
        }

        var markedPath = Path.Combine(
            _paths.PosterDirectory,
            item.Id.ToString("N", CultureInfo.InvariantCulture) + "-" + signature[..12] + ".jpg");

        try
        {
            PosterRenderer.Render(originalPath, markedPath, config, text);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not render marked poster for {Name}", item.Name);
            return false;
        }

        image.Path = markedPath;
        await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);

        var previousMarked = entry?.MarkedPath;
        lock (_stateLock)
        {
            LoadState()[item.Id] = new MarkerEntry
            {
                OriginalPath = originalPath,
                MarkedPath = markedPath,
                Signature = signature,
                ItemName = item.Name ?? string.Empty,
            };
            SaveState();
        }

        if (previousMarked is not null && !PathEquals(previousMarked, markedPath))
        {
            DeleteFileQuietly(previousMarked);
        }

        _logger.LogInformation("Marked poster of {Name}", item.Name);
        return true;
    }

    /// <summary>
    /// Restores the original poster of an item, if it is marked.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RestoreAsync(Guid itemId, CancellationToken cancellationToken)
    {
        MarkerEntry? entry;
        lock (_stateLock)
        {
            if (!LoadState().TryGetValue(itemId, out entry))
            {
                return;
            }
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is not null)
        {
            var image = item.GetImageInfo(ImageType.Primary, 0);

            // Only switch back if Jellyfin still points at our marked copy.
            if (image is not null && PathEquals(image.Path, entry.MarkedPath))
            {
                if (File.Exists(entry.OriginalPath))
                {
                    image.Path = entry.OriginalPath;
                }
                else
                {
                    // Original gone: drop the image so Jellyfin fetches a fresh one on the next refresh.
                    item.RemoveImage(image);
                    _logger.LogWarning("Original poster of {Name} no longer exists; image removed", item.Name);
                }

                await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Restored poster of {Name}", item.Name);
        }

        DeleteFileQuietly(entry.MarkedPath);
        lock (_stateLock)
        {
            LoadState().Remove(itemId);
            SaveState();
        }
    }

    /// <summary>Restores every marked poster.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RestoreAllAsync(CancellationToken cancellationToken)
    {
        foreach (var id in GetMarkedItemIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RestoreAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not restore poster for item {Id}", id);
            }
        }
    }

    private static string ComputeSignature(string originalPath, PluginConfiguration c, string text)
    {
        long originalTicks = 0;
        try
        {
            originalTicks = File.GetLastWriteTimeUtc(originalPath).Ticks;
        }
        catch (IOException)
        {
        }

        var raw = string.Join(
            "|",
            "v1",
            originalPath,
            originalTicks.ToString(CultureInfo.InvariantCulture),
            text,
            c.Grayscale,
            c.FilterEnabled,
            c.FilterColor,
            c.FilterOpacity,
            c.BannerEnabled,
            c.BannerPosition,
            c.BannerColor,
            c.BannerOpacity,
            c.BannerSizePercent,
            c.TextColor);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool PathEquals(string? a, string? b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            StringComparison.Ordinal);

    private bool IsInsidePosterDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(_paths.PosterDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.Ordinal);
    }

    private void DeleteFileQuietly(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        // Safety: only ever delete files inside our own poster folder.
        if (!IsInsidePosterDirectory(path))
        {
            return;
        }

        var full = Path.GetFullPath(path);

        try
        {
            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", full);
        }
    }

    private Dictionary<Guid, MarkerEntry> LoadState()
    {
        if (_state is not null)
        {
            return _state;
        }

        try
        {
            if (File.Exists(_stateFile))
            {
                _state = JsonSerializer.Deserialize<Dictionary<Guid, MarkerEntry>>(File.ReadAllText(_stateFile), JsonOptions);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogError(ex, "Marker state file is unreadable; starting with an empty state");
        }

        _state ??= new Dictionary<Guid, MarkerEntry>();
        return _state;
    }

    private void SaveState()
    {
        if (_state is null)
        {
            return;
        }

        try
        {
            var temp = _stateFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, JsonOptions));
            File.Move(temp, _stateFile, true);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Could not save marker state");
        }
    }

    /// <summary>What the plugin remembers about one marked item.</summary>
    public sealed class MarkerEntry
    {
        /// <summary>Gets or sets the original poster path.</summary>
        public string OriginalPath { get; set; } = string.Empty;

        /// <summary>Gets or sets the marked poster path.</summary>
        public string MarkedPath { get; set; } = string.Empty;

        /// <summary>Gets or sets the settings signature the marked poster was rendered with.</summary>
        public string Signature { get; set; } = string.Empty;

        /// <summary>Gets or sets the item name (for troubleshooting).</summary>
        public string ItemName { get; set; } = string.Empty;
    }
}
