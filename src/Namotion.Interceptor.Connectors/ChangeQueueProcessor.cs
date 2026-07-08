using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Processes property changes from a queue, buffering and deduplicating them before writing.
/// Used by both client sources and server background services.
/// </summary>
public class ChangeQueueProcessor : IDisposable
{
    private const int FlushDedupedBufferMinSize = 256;
    private const int FlushDedupedBufferMaxSize = 1024;

    private readonly Func<PropertyReference, bool> _propertyFilter;
    private readonly Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> _writeHandler;
    private readonly object? _source;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;

    // Use a concurrent, lock-free queue for collecting changes from the subscription thread.
    private readonly ConcurrentQueue<SubjectPropertyChange> _changes = new();
    private readonly int? _maxQueueDepth;
    private long _dropCount;
    private int _flushGate; // 0 = free, 1 = flushing
    private int _disposed; // 0 = not disposed, 1 = disposed (use Interlocked for thread-safe check)

    /// <summary>
    /// Number of buffered changes dropped due to bounded-queue overflow.
    /// Always zero when <c>maxQueueDepth</c> is null (unbounded).
    /// </summary>
    public long DropCount => Interlocked.Read(ref _dropCount);

    // Scratch buffers used only while holding the flush gate (single-threaded access)
    private readonly List<SubjectPropertyChange> _flushChanges = [];
    private readonly Dictionary<PropertyReference, int> _flushPropertyIndices = new(PropertyReference.Comparer);

    // Reusable buffer for deduped changes (rented from ArrayPool to avoid allocations on resize)
    private SubjectPropertyChange[] _flushDedupedBuffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(FlushDedupedBufferMinSize);
    private int _flushDedupedCount;

    // Reusable single-item buffer for the no-buffer (immediate) path
    private readonly SubjectPropertyChange[] _immediateBuffer = new SubjectPropertyChange[1];

    private readonly PropertyChangeQueueSubscription _subscription;
    private readonly bool _ownsSubscription;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeQueueProcessor"/> class.
    /// The subscription is created immediately so that changes are captured from this point,
    /// even before <see cref="ProcessAsync"/> is called. This prevents change loss during
    /// initialization gaps (e.g., between OPC UA node creation and processing start).
    /// </summary>
    /// <param name="source">Source to ignore (to prevent update loops).</param>
    /// <param name="context">The interceptor subject context.</param>
    /// <param name="propertyFilter">Filter to determine if a property change should be included.
    /// The <see cref="PropertyReference"/> may not have a registered property (e.g., when the subject
    /// is momentarily unregistered due to a concurrent structural mutation). Callers should handle
    /// this case explicitly — typically by resolving via <c>TryGetRegisteredProperty()</c> and
    /// returning <c>false</c> when null.</param>
    /// <param name="writeHandler">Handler to write batched changes.</param>
    /// <param name="bufferTime">Time to buffer changes before flushing.</param>
    /// <param name="maxQueueDepth">Bound on the buffered change queue, or null for unbounded (existing
    /// connector behavior). When set, enqueuing past the bound drops the oldest unprocessed change and
    /// increments <see cref="DropCount"/>, so the newest change is retained.</param>
    /// <param name="logger">The logger.</param>
    public ChangeQueueProcessor(
        object? source,
        IInterceptorSubjectContext context,
        Func<PropertyReference, bool> propertyFilter,
        Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> writeHandler,
        TimeSpan? bufferTime,
        int? maxQueueDepth,
        ILogger logger)
    {
        _source = source;
        _propertyFilter = propertyFilter;
        _writeHandler = writeHandler;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _maxQueueDepth = maxQueueDepth;

        try
        {
            _subscription = context.CreatePropertyChangeQueueSubscription();
            _ownsSubscription = true;
        }
        catch
        {
            ArrayPool<SubjectPropertyChange>.Shared.Return(_flushDedupedBuffer);
            _flushDedupedBuffer = null!;
            throw;
        }
    }

    /// <summary>
    /// Initializes the processor with an externally owned subscription. The caller keeps ownership:
    /// <see cref="Dispose"/> does not dispose the subscription. Use this when the subscription must
    /// outlive the processor, for example a source-lifetime subscription reused across reconnects.
    /// </summary>
    internal ChangeQueueProcessor(
        object? source,
        PropertyChangeQueueSubscription subscription,
        Func<PropertyReference, bool> propertyFilter,
        Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> writeHandler,
        TimeSpan? bufferTime,
        int? maxQueueDepth,
        ILogger logger)
    {
        _source = source;
        _propertyFilter = propertyFilter;
        _writeHandler = writeHandler;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _maxQueueDepth = maxQueueDepth;
        _subscription = subscription;
        _ownsSubscription = false;
    }

    /// <summary>
    /// Processes changes from the queue until cancellation is requested.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task.</returns>
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        // Snapshot of changes already queued at drain start. Any the model has moved past are
        // dropped at dequeue (see IsSuperseded). This is inert for source connectors: SubjectSourceBase
        // already drains and reconciles connect/reconnect-window writes into the retry queue before
        // ProcessAsync runs, so only current values remain here. It stays live for servers, which create
        // the processor before publishing. Removing this now-source-inert path is a deferred cleanup.
        var queuedBeforeStart = _subscription.Count;

        using var periodicTimer = _bufferTime > TimeSpan.Zero ? new PeriodicTimer(_bufferTime) : null;
        using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var flushTask = periodicTimer is not null
            ? Task.Run(async () =>
            {
                try
                {
                    // ReSharper disable AccessToDisposedClosure
                    while (await periodicTimer.WaitForNextTickAsync(linkedTokenSource.Token).ConfigureAwait(false))
                    {
                        await TryFlushAsync(linkedTokenSource.Token).ConfigureAwait(false);
                    }
                    // ReSharper restore AccessToDisposedClosure
                }
                catch (Exception ex)
                {
                    if (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Failed to flush changes.");
                    }
                }
            }, linkedTokenSource.Token)
            : Task.CompletedTask;

        if (periodicTimer is null)
        {
            _logger.LogWarning(
                "Change queue processor is running without buffering (bufferTime <= 0). " +
                "Each property change will be processed individually without deduplication, " +
                "which can cause high CPU usage under load. " +
                "Consider setting a bufferTime (e.g., 8-50ms) to enable batching and deduplication.");
        }

        try
        {
            await Task.Yield();

            while (_subscription.TryDequeue(out var change, linkedTokenSource.Token))
            {
                var wasQueuedBeforeStart = queuedBeforeStart > 0;
                if (wasQueuedBeforeStart)
                {
                    queuedBeforeStart--;
                }

                if (change.Source == _source)
                {
                    continue;
                }

                if (!_propertyFilter(change.Property))
                {
                    continue;
                }

                if (wasQueuedBeforeStart && IsSuperseded(in change))
                {
                    continue;
                }

                if (periodicTimer is null)
                {
                    // Immediate path: send a single change without buffering (zero allocation)
                    _immediateBuffer[0] = change;
                    try
                    {
                        await _writeHandler(_immediateBuffer, linkedTokenSource.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to write changes.");
                    }
                }
                else
                {
                    // Buffered path: enqueue lock-free; periodic timer handles flushing
                    _changes.Enqueue(change);

                    // Optional bounded-queue backpressure: drop oldest changes on overflow
                    if (_maxQueueDepth is int maxQueueDepth && _changes.Count > maxQueueDepth)
                    {
                        DropOverflow(maxQueueDepth);
                    }
                }
            }
        }
        finally
        {
            try { await linkedTokenSource.CancelAsync().ConfigureAwait(false); } catch { /* ignore */ }
            await flushTask.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True when a change captured before processing started no longer matches the
    /// property's current model value because the initial state load or a later write
    /// overwrote it; sending it would push a stale value back to the source. Fails
    /// open: when the current value cannot be read, the change is sent rather than lost.
    /// </summary>
    private static bool IsSuperseded(in SubjectPropertyChange change)
    {
        var getValue = change.Property.Metadata.GetValue;
        if (getValue is null)
        {
            return false;
        }

        try
        {
            return !Equals(getValue(change.Property.Subject), change.GetNewValue<object?>());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Drops the oldest buffered changes until the queue is back within <paramref name="maxQueueDepth"/>,
    /// incrementing <see cref="DropCount"/> for each. Best-effort: a concurrent flush may drain the queue
    /// below the bound first, in which case fewer drops occur.
    /// </summary>
    private void DropOverflow(int maxQueueDepth)
    {
        while (_changes.Count > maxQueueDepth && _changes.TryDequeue(out _))
        {
            Interlocked.Increment(ref _dropCount);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask TryFlushAsync(CancellationToken cancellationToken)
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

            _flushPropertyIndices.Clear();
            _flushDedupedCount = 0;

            // Pre-size to avoid resizes under bursts
            _flushPropertyIndices.EnsureCapacity(_flushChanges.Count);

            // Ensure the buffer is large enough (rent from pool to avoid allocations)
            if (_flushDedupedBuffer.Length < _flushChanges.Count)
            {
                ArrayPool<SubjectPropertyChange>.Shared.Return(_flushDedupedBuffer);
                _flushDedupedBuffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(_flushChanges.Count);
            }

            // Deduplicate by Property: keep oldest old value, use newest new value.
            // Backward iteration finds last occurrences first, preserving last-occurrence order.
            for (var i = _flushChanges.Count - 1; i >= 0; i--)
            {
                var change = _flushChanges[i];
                if (!_flushPropertyIndices.TryGetValue(change.Property, out var existingIndex))
                {
                    _flushPropertyIndices[change.Property] = _flushDedupedCount;
                    _flushDedupedBuffer[_flushDedupedCount++] = change;
                }
                else
                {
                    // Earlier occurrence: merge its old value into the kept (later) change
                    _flushDedupedBuffer[existingIndex] = change.MergeWithNewer(_flushDedupedBuffer[existingIndex]);
                }
            }

            // Reverse to restore chronological order of last occurrences
            if (_flushDedupedCount > 1)
            {
                Array.Reverse(_flushDedupedBuffer, 0, _flushDedupedCount);
            }

            if (_flushDedupedCount > 0)
            {
                try
                {
                    await _writeHandler(new ReadOnlyMemory<SubjectPropertyChange>(_flushDedupedBuffer, 0, _flushDedupedCount), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to write changes.");
                }
            }
        }
        finally
        {
            // Clear buffers to allow GC of SubjectPropertyChange objects
            _flushChanges.Clear();
            _flushPropertyIndices.Clear();

            // Clear entire rented array before potential return to pool.
            // SubjectPropertyChange contains object references (Source, boxed values) that must be released.
            Array.Clear(_flushDedupedBuffer, 0, _flushDedupedBuffer.Length);

            if (Volatile.Read(ref _disposed) == 1)
            {
                // Disposed while flushing - return buffer to pool now
                ArrayPool<SubjectPropertyChange>.Shared.Return(_flushDedupedBuffer);
                _flushDedupedBuffer = null!;
            }
            else if (_flushDedupedBuffer.Length >= FlushDedupedBufferMaxSize &&
                     _flushDedupedCount < _flushDedupedBuffer.Length / 4)
            {
                // Shrink buffer if it grew too large (return to pool and rent smaller)
                ArrayPool<SubjectPropertyChange>.Shared.Return(_flushDedupedBuffer);
                _flushDedupedBuffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(FlushDedupedBufferMinSize);
            }

            Volatile.Write(ref _flushGate, 0);
        }
    }

    /// <summary>
    /// Disposes the processor and returns the rented buffer to the pool.
    /// </summary>
    public void Dispose()
    {
        // Atomic check-and-set to prevent double-dispose race condition
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_ownsSubscription)
        {
            _subscription.Dispose();
        }

        // Try to acquire gate once - if flush is in progress, it will handle cleanup when it sees _disposed
        if (Interlocked.CompareExchange(ref _flushGate, 1, 0) == 0)
        {
            try
            {
                // Clear and return the buffer to the pool
                Array.Clear(_flushDedupedBuffer, 0, _flushDedupedBuffer.Length);
                ArrayPool<SubjectPropertyChange>.Shared.Return(_flushDedupedBuffer);
                _flushDedupedBuffer = null!;
            }
            finally
            {
                Volatile.Write(ref _flushGate, 0);
            }
        }
    }
}
