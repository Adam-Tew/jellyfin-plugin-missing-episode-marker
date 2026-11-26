using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Services;

/// <summary>
/// Starts checks on request (webhook, library change, settings change, timer), merging requests that
/// arrive close together so a burst of events only causes one check.
/// </summary>
public sealed class SyncScheduler : IDisposable
{
    private readonly MarkerRunner _runner;
    private readonly ILogger<SyncScheduler> _logger;
    private readonly object _lock = new();
    private CancellationTokenSource? _pending;
    private DateTime _pendingDueUtc;
    private string _pendingReason = string.Empty;
    private bool _pendingFullRepair;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncScheduler"/> class.
    /// </summary>
    /// <param name="runner">The runner.</param>
    /// <param name="logger">Logger.</param>
    public SyncScheduler(MarkerRunner runner, ILogger<SyncScheduler> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    /// <summary>Gets when the next requested check is due (UTC), if one is waiting.</summary>
    public DateTime? NextDueUtc
    {
        get
        {
            lock (_lock)
            {
                return _pending is null ? null : _pendingDueUtc;
            }
        }
    }

    /// <summary>
    /// Asks for a check after a delay. If a check is already waiting and due sooner, that one covers this request.
    /// </summary>
    /// <param name="delay">How long to wait (lets related events settle).</param>
    /// <param name="reason">What asked for the check.</param>
    /// <param name="fullRepair">Whether to also check every poster for problems.</param>
    public void RequestSync(TimeSpan delay, string reason, bool fullRepair = false)
    {
        CancellationToken token;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var due = DateTime.UtcNow + delay;
            if (_pending is not null && _pendingDueUtc <= due)
            {
                _pendingFullRepair |= fullRepair;
                return;
            }

            if (_pending is not null)
            {
                fullRepair |= _pendingFullRepair;
                _pending.Cancel();
                _pending.Dispose();
            }

            _pending = new CancellationTokenSource();
            _pendingDueUtc = due;
            _pendingReason = reason;
            _pendingFullRepair = fullRepair;
            token = _pending.Token;
        }

        _ = Task.Run(() => WaitAndRunAsync(delay, token), CancellationToken.None);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }

    private async Task WaitAndRunAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }

            string reason;
            bool fullRepair;
            lock (_lock)
            {
                if (token.IsCancellationRequested || _pending is null || _pending.Token != token)
                {
                    return;
                }

                reason = _pendingReason;
                fullRepair = _pendingFullRepair;
                _pending.Dispose();
                _pending = null;
            }

            await _runner.RunAsync(new Progress<double>(), reason, fullRepair, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Replaced by a sooner request.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Missing Episode Marker check failed");
        }
    }
}
