using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Timer-based periodic resynchronization trigger. Fires a callback on a fixed interval
/// to allow the caller to reconcile the local subject graph with the remote OPC UA address space.
/// </summary>
internal sealed class OpcUaPeriodicResyncHandler : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly Func<CancellationToken, Task> _onResyncRequested;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    private Timer? _timer;
    private int _disposed;
    private int _callbackRunning; // 1 = running, prevents overlapping callbacks

    public OpcUaPeriodicResyncHandler(
        TimeSpan interval,
        Func<CancellationToken, Task> onResyncRequested,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(onResyncRequested);
        ArgumentNullException.ThrowIfNull(logger);

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Interval must be positive.");
        }

        _interval = interval;
        _onResyncRequested = onResyncRequested;
        _logger = logger;
    }

    /// <summary>
    /// Starts the periodic resync timer. Must be called explicitly after construction.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        _timer = new Timer(OnTimerElapsed, null, _interval, _interval);
        _logger.LogDebug("Periodic resync started with interval {Interval}.", _interval);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        _timer?.Dispose();
        _cts.Dispose();
    }

    private async void OnTimerElapsed(object? state)
    {
        if (_disposed != 0)
        {
            return;
        }

        // Prevent overlapping callbacks: if a previous resync is still running, skip this tick.
        if (Interlocked.CompareExchange(ref _callbackRunning, 1, 0) != 0)
        {
            _logger.LogDebug("Periodic resync skipped because a previous resync is still running.");
            return;
        }

        try
        {
            await _onResyncRequested(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Expected during disposal, no action needed.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Periodic resync callback failed.");
        }
        finally
        {
            Volatile.Write(ref _callbackRunning, 0);
        }
    }
}
