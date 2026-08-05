using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Processes property changes from a queue, buffering and merging them before writing.
/// Used by both client sources and server background services.
/// </summary>
public class ChangeQueueProcessor : IDisposable
{
    private readonly Func<PropertyReference, bool> _propertyFilter;
    private readonly Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> _writeHandler;
    private readonly object? _source;
    private readonly IInterceptorSubjectContext _context;
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

    // Scratch state used only while holding the flush gate (single-threaded access)
    private readonly List<SubjectPropertyChange> _flushChanges = [];
    private readonly ChangeMerger _flushMerger = new();

    // Reusable single-item buffer for the no-buffer (immediate) path
    private readonly SubjectPropertyChange[] _immediateBuffer = new SubjectPropertyChange[1];

    private readonly PropertyChangeQueueSubscription _subscription;

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
        _context = context;
        _propertyFilter = propertyFilter;
        _writeHandler = writeHandler;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _maxQueueDepth = maxQueueDepth;

        try
        {
            _subscription = context.CreatePropertyChangeQueueSubscription();
        }
        catch
        {
            _flushMerger.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether a change this processor would normally skip has to be sent out after all.
    ///
    /// A transaction writes its value to the source itself and then applies it locally, and that local
    /// apply arrives here as a confirmation. Normally there is nothing to send: the source already has
    /// it. But a write of ours can land on the source between those two steps, leaving the source
    /// holding an older commit while the subject holds the confirmed one, and nothing would ever
    /// correct it. Sending the confirmation out repairs that.
    ///
    /// Only when a connector actually wrote the property since, so a property that is only ever written
    /// through transactions never pays for it.
    /// </summary>
    private static bool NeedsWriteBack(in SubjectPropertyChange change)
    {
        return change.Origin.Kind == ChangeOriginKind.Confirmed
               && CurrentValueFilter.WasWrittenOut(change.Property);
    }

    /// <summary>
    /// Processes changes from the queue until cancellation is requested.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task.</returns>
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
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
                "Each property change will be processed individually without merging, " +
                "which can cause high CPU usage under load. " +
                "Consider setting a bufferTime (e.g., 8-50ms) to enable batching and merging.");
        }

        try
        {
            await Task.Yield();

            while (_subscription.TryDequeue(out var change, linkedTokenSource.Token))
            {
                if (ReferenceEquals(change.Origin.Source, _source) && !NeedsWriteBack(in change))
                {
                    continue;
                }

                if (!_propertyFilter(change.Property))
                {
                    continue;
                }

                if (periodicTimer is null)
                {
                    // The buffered path applies this inside the merger, where it can compact the
                    // batch in place; here there is no batch, so it gates the single write.
                    if (!CurrentValueFilter.IsCurrent(in change))
                    {
                        continue;
                    }

                    CurrentValueFilter.MarkWrittenOut(in change);

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

            var mergedChanges = _flushMerger.Merge(CollectionsMarshal.AsSpan(_flushChanges), suppressSupersededChanges: true);

            if (mergedChanges.Length > 0)
            {
                try
                {
                    await _writeHandler(mergedChanges, cancellationToken).ConfigureAwait(false);
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

            if (Volatile.Read(ref _disposed) == 1)
            {
                // Disposed while flushing - return buffer to pool now
                _flushMerger.Dispose();
            }
            else
            {
                _flushMerger.Reset();
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

        _subscription.Dispose();

        // Try to acquire gate once - if flush is in progress, it will handle cleanup when it sees _disposed
        if (Interlocked.CompareExchange(ref _flushGate, 1, 0) == 0)
        {
            try
            {
                // Clear and return the buffer to the pool
                _flushMerger.Dispose();
            }
            finally
            {
                Volatile.Write(ref _flushGate, 0);
            }
        }
    }
}
