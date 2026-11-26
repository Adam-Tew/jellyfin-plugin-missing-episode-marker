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
    private readonly string _wantedFile;
    private readonly object _wantedLock = new();
    private Dictionary<Guid, MarkerSpec>? _wanted;

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
        _wantedFile = Path.Combine(paths.DataDirectory, "wanted-markers.json");

        // Left over from 0.3 (text only, no marker state); rebuilt on the next check.
        DeleteLegacyFile(Path.Combine(paths.DataDirectory, "wanted-items.json"));
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
    /// Replaces the full set of items that should be marked (item id -> marker).
    /// Items in this set get marked as soon as they have a usable poster, even if it arrives later.
    /// </summary>
    /// <param name="wanted">The items.</param>
    public void SetWanted(IDictionary<Guid, MarkerSpec> wanted)
    {
        lock (_wantedLock)
        {
            _wanted = new Dictionary<Guid, MarkerSpec>(wanted);
            SaveWanted();
        }
    }

    /// <summary>Updates the wanted set for some items only (e.g. one series and its seasons).</summary>
    /// <param name="scope">The items this update is about.</param>
    /// <param name="wanted">Which of them should be marked, with their marker.</param>
    public void UpdateWanted(IEnumerable<Guid> scope, IDictionary<Guid, MarkerSpec> wanted)
    {
        lock (_wantedLock)
        {
            var set = LoadWanted();
            foreach (var id in scope)
            {
                set.Remove(id);
            }

            foreach (var (id, spec) in wanted)
            {
                set[id] = spec;
            }

            SaveWanted();
        }
    }

    /// <summary>Gets the marker for an item that should be marked, or null if it should not.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The marker, or null.</returns>
    public MarkerSpec? GetWanted(Guid itemId)
    {
        lock (_wantedLock)
        {
            return LoadWanted().TryGetValue(itemId, out var spec) ? spec : null;
        }
    }

    /// <summary>
    /// Checks whether an item should be marked but does not show its marked copy
    /// (not marked yet because it had no poster, or Jellyfin replaced the poster).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>True when a marker should be (re)applied.</returns>
    public bool NeedsMarker(BaseItem item)
    {
        if (GetWanted(item.Id) is null)
        {
            return false;
        }

        var image = item.GetImageInfo(ImageType.Primary, 0);
        if (image is null)
        {
            return false; // Still no poster; wait for one.
        }

        var entry = GetEntry(item.Id);
        return entry is null || !PathEquals(image.Path, entry.MarkedPath);
    }

    /// <summary>Gets what the plugin remembers about a marked item.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The entry, or null when the item is not marked.</returns>
    public MarkerEntry? GetEntry(Guid itemId)
    {
        lock (_stateLock)
        {
            return LoadState().TryGetValue(itemId, out var entry) ? entry : null;
        }
    }

    /// <summary>
    /// Checks whether Jellyfin has swapped a marked poster for another image (e.g. after a metadata refresh).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>True when the item should be marked but no longer shows the marked copy.</returns>
    public bool WasReplaced(BaseItem item)
    {
        var entry = GetEntry(item.Id);
        if (entry is null)
        {
            return false;
        }

        var image = item.GetImageInfo(ImageType.Primary, 0);
        return image is null || !PathEquals(image.Path, entry.MarkedPath);
    }

    /// <summary>
    /// Marks an item's poster (or updates the marker if settings/text changed).
    /// </summary>
    /// <param name="item">The series or season.</param>
    /// <param name="spec">Which marker, and its banner text.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    public async Task<ApplyResult> ApplyAsync(BaseItem item, MarkerSpec spec, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var style = MarkerStyle.For(config, spec.State);
        var text = spec.Text;
        var image = item.GetImageInfo(ImageType.Primary, 0);
        if (image is null)
        {
            // No poster at all (e.g. a season using the series poster). Nothing to mark.
            _logger.LogDebug("{Name} has no poster image of its own yet; it is marked once one arrives", item.Name);
            return ApplyResult.NoPoster;
        }

        MarkerEntry? entry;
        lock (_stateLock)
        {
            LoadState().TryGetValue(item.Id, out entry);
        }

        string originalPath;
        var isStandIn = false;
        if (entry is not null && PathEquals(image.Path, entry.MarkedPath))
        {
            originalPath = entry.OriginalPath;
            isStandIn = entry.IsStandIn;
        }
        else
        {
            // First time, or Jellyfin replaced the poster since we marked it (e.g. a metadata refresh).
            if (!image.IsLocalFile)
            {
                _logger.LogInformation("Poster of {Name} is not a local file ({Path}); cannot mark it", item.Name, image.Path);
                return ApplyResult.NotLocal;
            }

            if (IsInsidePosterDirectory(image.Path))
            {
                // Points at one of our files but we have no record of the original (state lost).
                // Never mark a marked poster; leave it for a metadata refresh to fix.
                _logger.LogWarning(
                    "Poster of {Name} is a marked copy with no known original; refresh its metadata to get the original back",
                    item.Name);
                return ApplyResult.OriginalUnknown;
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
            return ApplyResult.OriginalMissing;
        }

        var signature = ComputeSignature(originalPath, style, text);
        if (entry is not null
            && entry.State != MarkerState.None
            && entry.Signature == signature
            && PathEquals(image.Path, entry.MarkedPath)
            && File.Exists(entry.MarkedPath))
        {
            if (entry.Text != text || entry.State != spec.State)
            {
                lock (_stateLock)
                {
                    entry.Text = text;
                    entry.State = spec.State;
                    SaveState();
                }
            }

            return ApplyResult.Marked; // Already up to date.
        }

        var markedPath = Path.Combine(
            _paths.PosterDirectory,
            item.Id.ToString("N", CultureInfo.InvariantCulture) + "-" + signature[..12] + ".jpg");

        try
        {
            PosterRenderer.Render(originalPath, markedPath, style, text);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not render marked poster for {Name}", item.Name);
            return ApplyResult.RenderFailed;
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
                Text = text,
                State = spec.State,
                IsStandIn = isStandIn,
            };
            SaveState();
        }

        if (previousMarked is not null && !PathEquals(previousMarked, markedPath))
        {
            DeleteFileQuietly(previousMarked);
        }

        _logger.LogInformation("Marked poster of {Name} ({State})", item.Name, spec.State);
        return ApplyResult.Marked;
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
                if (entry.IsStandIn)
                {
                    // The season had no poster of its own: go back to that.
                    item.RemoveImage(image);
                }
                else if (File.Exists(entry.OriginalPath))
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

            if (entry.State != MarkerState.None)
            {
                _logger.LogInformation("Restored poster of {Name}", item.Name);
            }
            else
            {
                _logger.LogDebug("Removed stand-in poster of {Name}", item.Name);
            }
        }

        DeleteFileQuietly(entry.MarkedPath);
        lock (_stateLock)
        {
            LoadState().Remove(itemId);
            SaveState();
        }
    }

    /// <summary>Counts items that currently show a marker (stand-in posters without a marker are not counted).</summary>
    /// <returns>The number of marked posters.</returns>
    public int CountMarked()
    {
        lock (_stateLock)
        {
            return LoadState().Values.Count(e => e.State != MarkerState.None);
        }
    }

    /// <summary>
    /// Gets the unmarked poster file of an item: the original behind our marked copy, or its own poster.
    /// </summary>
    /// <param name="item">The item (normally a series).</param>
    /// <returns>A local file path, or null when there is no usable poster.</returns>
    public string? GetUnmarkedPosterPath(BaseItem item)
    {
        var image = item.GetImageInfo(ImageType.Primary, 0);
        if (image is null)
        {
            return null;
        }

        var entry = GetEntry(item.Id);
        if (entry is not null && PathEquals(image.Path, entry.MarkedPath))
        {
            return File.Exists(entry.OriginalPath) ? entry.OriginalPath : null;
        }

        return image.IsLocalFile && !IsInsidePosterDirectory(image.Path) && File.Exists(image.Path) ? image.Path : null;
    }

    /// <summary>
    /// Gives a season without a poster of its own an unmarked copy of the show poster.
    /// Otherwise apps show the show poster in its place, including the show's marker.
    /// Removed again like a marker (restore, "Remove all markers", uninstall).
    /// </summary>
    /// <param name="season">The season.</param>
    /// <param name="seriesPosterPath">The unmarked show poster.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the season now has a stand-in (new or existing, possibly marked); false when it has a real poster.</returns>
    public async Task<bool> EnsureStandInAsync(BaseItem season, string seriesPosterPath, CancellationToken cancellationToken)
    {
        var image = season.GetImageInfo(ImageType.Primary, 0);
        var entry = GetEntry(season.Id);
        var ours = image is not null && entry is not null && PathEquals(image.Path, entry.MarkedPath);

        if (image is not null && !ours)
        {
            // A real poster arrived (e.g. poster repair or a manual pick): forget any old stand-in.
            if (entry is not null)
            {
                Forget(season.Id, entry);
            }

            return false;
        }

        if (ours && entry!.IsStandIn && entry.State == MarkerState.None && PathEquals(entry.OriginalPath, seriesPosterPath) && File.Exists(entry.MarkedPath))
        {
            return true; // Already in place.
        }

        if (ours && entry!.State != MarkerState.None && !entry.IsStandIn)
        {
            return false; // A marked real poster; handled by restore.
        }

        var standInPath = Path.Combine(
            _paths.PosterDirectory,
            season.Id.ToString("N", CultureInfo.InvariantCulture) + "-standin" + Path.GetExtension(seriesPosterPath));
        try
        {
            File.Copy(seriesPosterPath, standInPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not copy the show poster for {Name}", season.Name);
            return false;
        }

        if (image is not null)
        {
            image.Path = standInPath;
            image.DateModified = File.GetLastWriteTimeUtc(standInPath);
        }
        else
        {
            season.SetImage(
                new ItemImageInfo
                {
                    Path = standInPath,
                    Type = ImageType.Primary,
                    DateModified = File.GetLastWriteTimeUtc(standInPath),
                },
                0);
        }

        await season.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);

        var previous = entry?.MarkedPath;
        lock (_stateLock)
        {
            LoadState()[season.Id] = new MarkerEntry
            {
                OriginalPath = seriesPosterPath,
                MarkedPath = standInPath,
                Signature = string.Empty,
                ItemName = season.Name ?? string.Empty,
                Text = string.Empty,
                State = MarkerState.None,
                IsStandIn = true,
            };
            SaveState();
        }

        if (previous is not null && !PathEquals(previous, standInPath))
        {
            DeleteFileQuietly(previous);
        }

        _logger.LogInformation("{Name} has no poster of its own; gave it the unmarked show poster", season.Name);
        return true;
    }

    /// <summary>Restores every marked poster.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RestoreAllAsync(CancellationToken cancellationToken)
    {
        SetWanted(new Dictionary<Guid, MarkerSpec>());

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

    private void Forget(Guid itemId, MarkerEntry entry)
    {
        DeleteFileQuietly(entry.MarkedPath);
        lock (_stateLock)
        {
            LoadState().Remove(itemId);
            SaveState();
        }
    }

    private static string ComputeSignature(string originalPath, MarkerStyle style, string text)
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
            "v2",
            originalPath,
            originalTicks.ToString(CultureInfo.InvariantCulture),
            text,
            style.Describe());

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

    private Dictionary<Guid, MarkerSpec> LoadWanted()
    {
        if (_wanted is not null)
        {
            return _wanted;
        }

        try
        {
            if (File.Exists(_wantedFile))
            {
                _wanted = JsonSerializer.Deserialize<Dictionary<Guid, MarkerSpec>>(File.ReadAllText(_wantedFile), JsonOptions);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Wanted-items file is unreadable; it is rebuilt on the next check");
        }

        _wanted ??= new Dictionary<Guid, MarkerSpec>();
        return _wanted;
    }

    private void DeleteLegacyFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }

    private void SaveWanted()
    {
        if (_wanted is null)
        {
            return;
        }

        try
        {
            var temp = _wantedFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_wanted, JsonOptions));
            File.Move(temp, _wantedFile, true);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Could not save the wanted-items file");
        }
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

        /// <summary>Gets or sets the banner text the poster was marked with.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Gets or sets which marker the poster shows (None = an unmarked stand-in).</summary>
        public MarkerState State { get; set; } = MarkerState.Missing;

        /// <summary>
        /// Gets or sets a value indicating whether the season had no poster of its own and got a copy of the show poster.
        /// Restoring removes the image again instead of switching back.
        /// </summary>
        public bool IsStandIn { get; set; }
    }
}

/// <summary>Which marker an item should get.</summary>
public sealed class MarkerSpec
{
    /// <summary>Gets or sets the marker state.</summary>
    public MarkerState State { get; set; }

    /// <summary>Gets or sets the banner text (already formatted).</summary>
    public string Text { get; set; } = string.Empty;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MarkerSpec other && other.State == State && other.Text == Text;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(State, Text);
}

/// <summary>Outcome of trying to mark a poster.</summary>
public enum ApplyResult
{
    /// <summary>The poster is marked.</summary>
    Marked = 0,

    /// <summary>The item has no poster of its own.</summary>
    NoPoster = 1,

    /// <summary>The poster is a remote URL, not a file on the server.</summary>
    NotLocal = 2,

    /// <summary>The original poster file no longer exists.</summary>
    OriginalMissing = 3,

    /// <summary>The poster points at a marked copy whose original is unknown.</summary>
    OriginalUnknown = 4,

    /// <summary>Drawing the marker failed.</summary>
    RenderFailed = 5,
}
