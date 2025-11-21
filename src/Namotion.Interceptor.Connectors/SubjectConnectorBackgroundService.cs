using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

public class SubjectConnectorBackgroundService : BackgroundService
{
    private readonly ISubjectConnector _connector;
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;
    private readonly TimeSpan _retryTime;

    protected ConnectorUpdateBuffer UpdateBuffer { get; init; }

    // Use a concurrent, lock-free queue for collecting changes from the subscription thread.
    private readonly ConcurrentQueue<SubjectPropertyChange> _changes = new();
    private int _flushGate; // 0 = free, 1 = flushing

    // Scratch buffers used only while holding the write semaphore (single-threaded access)
    private readonly List<SubjectPropertyChange> _flushChanges = [];
    private readonly HashSet<PropertyReference> _flushTouchedChanges = new(PropertyReference.Comparer);

    // Reusable buffer for deduped changes (avoids allocation on each flush)
    private SubjectPropertyChange[] _flushDedupedBuffer = new SubjectPropertyChange[64];
    private int _flushDedupedCount;

    // Reusable single-item buffer for the no-buffer (immediate) path
    private readonly SubjectPropertyChange[] _immediateBuffer = new SubjectPropertyChange[1];

    // Use ticks to avoid torn reads of DateTimeOffset across threads
    private long _flushLastTicks;

#pragma warning disable CS8618
    internal SubjectConnectorBackgroundService(
        ISubjectConnector connector,
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime,
        TimeSpan? retryTime)
#pragma warning restore CS8618
    {
        _connector = connector;
        _context = context;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _retryTime = retryTime ?? TimeSpan.FromSeconds(10);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                UpdateBuffer.StartBuffering();
                var disposable = await _connector.StartListeningAsync(UpdateBuffer, stoppingToken).ConfigureAwait(false);
                try
                {
                    await UpdateBuffer.CompleteInitializationAsync(stoppingToken);

                    using var subscription = _context.CreatePropertyChangeQueueSubscription();
                    await ProcessPropertyChangesAsync(subscription, stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    if (disposable is IAsyncDisposable asyncDisposable)
                    {
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        disposable?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException or OperationCanceledException)
                {
                    return;
                }

                _logger.LogError(ex, "Failed to listen for changes in connector.");
                // ResetState is called AFTER disposal in the finally block above,
                // so all resources from the previous attempt are already cleaned up.
                ResetState();

                await Task.Delay(_retryTime, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessPropertyChangesAsync(PropertyChangeQueueSubscription subscription, CancellationToken stoppingToken)
    {
        ResetState();

        using var periodicTimer = _bufferTime > TimeSpan.Zero ? new PeriodicTimer(_bufferTime) : null;
        using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        var flushTask = periodicTimer is not null
            // ReSharper disable AccessToDisposedClosure
            ? Task.Run(async () => await RunPeriodicFlushAsync(
                periodicTimer, linkedTokenSource.Token).ConfigureAwait(false), linkedTokenSource.Token)
            : Task.CompletedTask;

        try
        {
            // Ensure we don't block the startup process
            await Task.Yield();

            while (subscription.TryDequeue(out var item, linkedTokenSource.Token))
            {
                if (item.Source == _connector)
                {
                    continue; // Ignore changes originating from this connector (avoid update loops)
                }

                var registeredProperty = item.Property.TryGetRegisteredProperty();
                if (registeredProperty is null || !_connector.IsPropertyIncluded(registeredProperty))
                {
                    // Property is null when subject has already been attached (ignore change)
                    continue;
                }

                if (periodicTimer is null)
                {
                    // Immediate path: send the single change without buffering (zero allocation)
                    _immediateBuffer[0] = item;
                    await WriteToSourceAsync(_immediateBuffer, linkedTokenSource.Token).ConfigureAwait(false);
                }
                else
                {
                    // Buffered path: enqueue lock-free; periodic timer handles flushing
                    _changes.Enqueue(item);

                    // Flush directly when needed (currently disabled in favor of periodic flush only)
                    // var lastTicks = Volatile.Read(ref _flushLastTicks);
                    // if (item.ChangedTimestamp.UtcTicks - lastTicks >= _bufferTime.Ticks)
                    // {
                    //     await TryFlushBufferAsync(item.ChangedTimestamp.UtcTicks, linkedTokenSource.Token).ConfigureAwait(false);
                    // }
                }
            }
        }
        finally
        {
            try { await linkedTokenSource.CancelAsync().ConfigureAwait(false); } catch { /* ignore */ }
            await flushTask.ConfigureAwait(false);
        }
    }

    private async Task RunPeriodicFlushAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                var lastTicks = Volatile.Read(ref _flushLastTicks);
                if (nowTicks - lastTicks >= _bufferTime.Ticks)
                {
                    await TryFlushBufferAsync(nowTicks, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
        }
    }

    private async ValueTask TryFlushBufferAsync(long newFlushTicks, CancellationToken cancellationToken)
    {
        // Fast, allocation-free try-enter
        if (Interlocked.Exchange(ref _flushGate, 1) == 1)
        {
            return;
        }

        try
        {
            // Drain the concurrent queue into the scratch buffer under exclusive flush
            _flushChanges.Clear();
            while (_changes.TryDequeue(out var change))
            {
                _flushChanges.Add(change);
            }

            if (_flushChanges.Count == 0)
            {
                return;
            }

            Volatile.Write(ref _flushLastTicks, newFlushTicks);

            _flushTouchedChanges.Clear();
            _flushDedupedCount = 0;

            // Pre-size to avoid resizes under bursts
            _flushTouchedChanges.EnsureCapacity(_flushChanges.Count);

            // Ensure buffer is large enough
            if (_flushDedupedBuffer.Length < _flushChanges.Count)
            {
                _flushDedupedBuffer = new SubjectPropertyChange[Math.Max(_flushChanges.Count, _flushDedupedBuffer.Length * 2)];
            }

            // Deduplicate by Property, keeping the last write, and preserve order of last occurrences
            for (var i = _flushChanges.Count - 1; i >= 0; i--)
            {
                var change = _flushChanges[i];
                if (_flushTouchedChanges.Add(change.Property))
                {
                    _flushDedupedBuffer[_flushDedupedCount++] = change;
                }
            }

            // Reverse in place to keep ascending order of last occurrences without allocations
            if (_flushDedupedCount > 1)
            {
                Array.Reverse(_flushDedupedBuffer, 0, _flushDedupedCount);
            }

            if (_flushDedupedCount > 0)
            {
                await WriteToSourceAsync(new ReadOnlyMemory<SubjectPropertyChange>(_flushDedupedBuffer, 0, _flushDedupedCount), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Clear buffers to allow GC of SubjectPropertyChange objects
            _flushChanges.Clear();
            _flushTouchedChanges.Clear();
            if (_flushDedupedCount > 0)
            {
                Array.Clear(_flushDedupedBuffer, 0, _flushDedupedCount);
            }
            Volatile.Write(ref _flushGate, 0);
        }
    }

    protected virtual async ValueTask WriteToSourceAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        try
        {
            await _connector.WriteToSourceInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // Don't swallow cancellation - allows flush task to exit
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to write changes to connector.");
        }
    }

    private void ResetState()
    {
        _changes.Clear();
        _flushChanges.Clear();
        _flushTouchedChanges.Clear();
        _flushDedupedCount = 0;
        Volatile.Write(ref _flushLastTicks, 0L);
        Volatile.Write(ref _flushGate, 0);
    }
}
