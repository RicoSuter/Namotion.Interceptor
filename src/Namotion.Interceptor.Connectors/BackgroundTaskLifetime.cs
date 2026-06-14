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
    private readonly Func<ValueTask>? _disposeAsyncFunc;
    private readonly ILogger _logger;
    private int _disposed;

    private BackgroundTaskLifetime(
        CancellationTokenSource monitorCts,
        Task monitorTask,
        ILogger logger,
        Func<ValueTask>? disposeAsyncFunc)
    {
        _monitorCts = monitorCts;
        _monitorTask = monitorTask;
        _logger = logger;
        _disposeAsyncFunc = disposeAsyncFunc;
    }

    /// <summary>
    /// Creates a linked CTS, spawns the body as a background task, and returns
    /// the lifetime that owns both. On disposal the CTS is cancelled, the task awaited,
    /// and the optional cleanup callback invoked.
    /// </summary>
    public static BackgroundTaskLifetime Start(
        CancellationToken parentToken,
        ILogger logger,
        Func<CancellationToken, Task> executeAsyncFunc,
        Func<ValueTask>? disposeAsyncFunc = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        var task = Task.Run(() => executeAsyncFunc(cts.Token), CancellationToken.None);
        return new BackgroundTaskLifetime(cts, task, logger, disposeAsyncFunc);
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

        if (_disposeAsyncFunc is not null)
        {
            try { await _disposeAsyncFunc().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Cleanup callback threw during disposal."); }
        }
    }
}
