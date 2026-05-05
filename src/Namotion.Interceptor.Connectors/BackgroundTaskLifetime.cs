using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Owns a background task and its linked cancellation source, plus an optional
/// cleanup callback. On disposal the task is cancelled, awaited, and then the
/// cleanup callback is invoked.
/// </summary>
public sealed class BackgroundTaskLifetime : IAsyncDisposable
{
    private readonly CancellationTokenSource _monitorCts;
    private readonly Task _monitorTask;
    private readonly Func<ValueTask>? _disposeConnectionAsync;
    private readonly ILogger _logger;
    private int _disposed;

    private BackgroundTaskLifetime(
        CancellationTokenSource monitorCts,
        Task monitorTask,
        ILogger logger,
        Func<ValueTask>? disposeConnectionAsync)
    {
        _monitorCts = monitorCts;
        _monitorTask = monitorTask;
        _logger = logger;
        _disposeConnectionAsync = disposeConnectionAsync;
    }

    /// <summary>
    /// Creates a linked CTS, spawns the monitor body as a background task, and returns
    /// the lifetime that owns both. On disposal the CTS is cancelled, the task awaited,
    /// and the optional connection cleanup invoked.
    /// </summary>
    public static BackgroundTaskLifetime Start(
        CancellationToken parentToken,
        Func<CancellationToken, Task> monitorBody,
        ILogger logger,
        Func<ValueTask>? disposeConnectionAsync = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        var task = Task.Run(() => monitorBody(cts.Token), CancellationToken.None);
        return new BackgroundTaskLifetime(cts, task, logger, disposeConnectionAsync);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try { await _monitorCts.CancelAsync().ConfigureAwait(false); } catch { /* ignore */ }
        try { await _monitorTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex) { _logger.LogWarning(ex, "Monitor task threw during disposal."); }
        try { _monitorCts.Dispose(); } catch { /* ignore */ }

        if (_disposeConnectionAsync is not null)
        {
            try { await _disposeConnectionAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Connection cleanup threw during disposal."); }
        }
    }
}
