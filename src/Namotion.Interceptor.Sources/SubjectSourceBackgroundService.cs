using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public class SubjectSourceBackgroundService : BackgroundService
{
    private readonly ISubjectSource _source;
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;
    private readonly TimeSpan _retryTime;
    private readonly WriteRetryQueue? _writeRetryQueue;
    private readonly SubjectPropertyWriter _propertyWriter;

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

    public SubjectSourceBackgroundService(
        ISubjectSource source,
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime = null,
        TimeSpan? retryTime = null,
        int writeRetryQueueSize = 1000)
    {
        _source = source;
        _context = context;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _retryTime = retryTime ?? TimeSpan.FromSeconds(10);

        if (writeRetryQueueSize > 0)
        {
            _writeRetryQueue = new WriteRetryQueue(writeRetryQueueSize, logger);
        }

        _propertyWriter = new SubjectPropertyWriter(
            source,
            _writeRetryQueue is not null ? ct => _writeRetryQueue.FlushAsync(source, ct) : null,
            logger);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _propertyWriter.StartBuffering();
                var disposable = await _source.StartListeningAsync(_propertyWriter, stoppingToken).ConfigureAwait(false);
                try
                {
                    await _propertyWriter.CompleteInitializationAsync(stoppingToken);

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

                _logger.LogError(ex, "Failed to listen for changes in source.");
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
                if (item.Source == _source)
                {
                    continue; // Ignore changes originating from this connector (avoid update loops)
                }

                var registeredProperty = item.Property.TryGetRegisteredProperty();
                if (registeredProperty is null || !_source.IsPropertyIncluded(registeredProperty))
                {
                    // Property is null when subject has already been attached (ignore change)
                    continue;
                }

                if (periodicTimer is null)
                {
                    // Immediate path: send the single change without buffering (zero allocation)
                    _immediateBuffer[0] = item;
                    await WriteChangesAsync(_immediateBuffer, linkedTokenSource.Token).ConfigureAwait(false);
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
                await WriteChangesAsync(new ReadOnlyMemory<SubjectPropertyChange>(_flushDedupedBuffer, 0, _flushDedupedCount), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Clear buffers to allow GC of SubjectPropertyChange objects
            _flushChanges.Clear();
            _flushTouchedChanges.Clear();

            // Null out references to allow GC (SubjectPropertyChange contains object refs)
            for (var i = 0; i < _flushDedupedCount; i++)
            {
                _flushDedupedBuffer[i] = default;
            }

            // Shrink buffer if it grew too large (avoid holding memory after burst)
            if (_flushDedupedBuffer.Length > 1024 && _flushDedupedCount < _flushDedupedBuffer.Length / 4)
            {
                _flushDedupedBuffer = new SubjectPropertyChange[Math.Max(64, _flushDedupedBuffer.Length / 2)];
            }

            Volatile.Write(ref _flushGate, 0);
        }
    }

    protected async ValueTask WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (_writeRetryQueue is null)
        {
            // No retry queue - write directly
            try
            {
                await _source.WriteChangesInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // Don't swallow cancellation
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to write changes to source.");
            }
            return;
        }

        // First flush any queued changes
        var succeeded = await _writeRetryQueue.FlushAsync(_source, cancellationToken).ConfigureAwait(false);
        if (!succeeded)
        {
            _writeRetryQueue.Enqueue(changes);
            return;
        }

        // Write current changes
        try
        {
            await _source.WriteChangesInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // Don't swallow cancellation
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to write {Count} changes to source, queuing for retry.", changes.Length);
            _writeRetryQueue.Enqueue(changes);
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
