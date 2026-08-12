using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;
    private readonly ChangeDeliveryRule _deliveryRule;

    // Use a concurrent, lock-free queue for collecting changes from the subscription thread.
    private readonly ConcurrentQueue<SubjectPropertyChange> _changes = new();
    private readonly int? _maxQueueDepth;
    private long _dropCount;
    private int _flushGate; // 0 = free, 1 = flushing
    private int _disposed; // 0 = not disposed, 1 = disposed (use Interlocked for thread-safe check)

    /// <summary>
    /// The rule this processor decides supersession with. Exposed so a connector can pin which rule it
    /// wired up: choosing wrongly is silent, so "it compiles" is not evidence that it chose correctly.
    /// </summary>
    internal ChangeDeliveryRule DeliveryRule => _deliveryRule;

    /// <summary>
    /// Number of buffered changes dropped due to bounded-queue overflow.
    /// Always zero when <c>maxQueueDepth</c> is null (unbounded).
    /// </summary>
    public long DropCount => Interlocked.Read(ref _dropCount);

    /// <summary>
    /// Gets the number of changes currently buffered. Approximate: read without a lock while the
    /// pump is running. Always 0 when the processor is on its immediate path (no buffer time).
    /// </summary>
    public int QueueDepth => _changes.Count;

    // Scratch state used only while holding the flush gate (single-threaded access)
    private readonly List<SubjectPropertyChange> _flushChanges = [];
    private readonly ChangeMerger _changeMerger = new();

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
    /// <param name="deliveryRule">Which commits may supersede a change this processor is about to
    /// write; see <see cref="ChangeDeliveryRule"/> for the condition that decides it. Deliberately
    /// has no default: picking the wrong one is silent and its damage is permanent, so every connector
    /// states which it is.</param>
    /// <param name="bufferTime">Time to buffer changes before flushing.</param>
    /// <param name="maxQueueDepth">Bound on the buffered change queue, or null for unbounded (existing
    /// connector behavior). When set, enqueuing past the bound drops the oldest unprocessed change and
    /// increments <see cref="DropCount"/>, so the newest change is retained.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="deliveryRule"/> is
    /// <see cref="ChangeDeliveryRule.Unspecified"/> or not a defined value. Rejected here rather than at
    /// the first flush, where it would end delivery for this processor's lifetime. Also thrown when
    /// <paramref name="maxQueueDepth"/> is zero or negative, since that would drop every change
    /// immediately; pass null for an unbounded queue instead.</exception>
    public ChangeQueueProcessor(
        object? source,
        IInterceptorSubjectContext context,
        Func<PropertyReference, bool> propertyFilter,
        Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> writeHandler,
        ChangeDeliveryRule deliveryRule,
        TimeSpan? bufferTime,
        int? maxQueueDepth,
        ILogger logger)
    {
        _source = source;
        _propertyFilter = propertyFilter;
        _writeHandler = writeHandler;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);

        try
        {
            if (maxQueueDepth is <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxQueueDepth),
                    "A bound of zero or less would drop every change immediately. Pass null for an unbounded queue.");
            }

            _maxQueueDepth = maxQueueDepth;
            _deliveryRule = ValidateRule(deliveryRule);

            _subscription = context.CreatePropertyChangeQueueSubscription();
            _ownsSubscription = true;
        }
        catch
        {
            _changeMerger.Dispose();
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
        ChangeDeliveryRule deliveryRule,
        TimeSpan? bufferTime,
        int? maxQueueDepth,
        ILogger logger)
    {
        _source = source;
        _propertyFilter = propertyFilter;
        _writeHandler = writeHandler;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);

        try
        {
            if (maxQueueDepth is <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxQueueDepth),
                    "A bound of zero or less would drop every change immediately. Pass null for an unbounded queue.");
            }

            _maxQueueDepth = maxQueueDepth;
            _subscription = subscription;
            _ownsSubscription = false;
            _deliveryRule = ValidateRule(deliveryRule);
        }
        catch
        {
            _changeMerger.Dispose();
            throw;
        }
    }

    // Rejects every unnamed value, not just zero: the delivery decision throws on an unknown rule from
    // inside the flush, outside the try that wraps the write handler. The periodic loop's catch does
    // catch it, but that catch sits outside the loop, so the loop never resumes and delivery ends for
    // this processor's lifetime while the queue keeps filling.
    private static ChangeDeliveryRule ValidateRule(ChangeDeliveryRule rule)
    {
        if (rule is not (ChangeDeliveryRule.SourceValuesMayBeStale or ChangeDeliveryRule.SourceValuesAreSettled))
        {
            throw new ArgumentOutOfRangeException(nameof(rule), rule,
                "A delivery rule must be chosen explicitly; see ChangeDeliveryRule for the condition that decides it.");
        }

        return rule;
    }

    /// <summary>
    /// Processes changes from the queue until cancellation is requested.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task.</returns>
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        // Snapshot of changes already queued at drain start: these were captured while the source was
        // still connecting, so one whose value the model has moved past is stale state and is dropped.
        // Changes arriving after it are steady state, where an intermediate value is data rather than
        // staleness, so this check does not apply to them. On the immediate path they are therefore
        // delivered even once the model has moved on (WhenSteadyStateChangesCarryOldTimestamps_...);
        // the buffered path still collapses them at flush time, which is the documented contract.
        //
        // Sources reach this with most window writes already handled: SubjectSourceBase drains and
        // reconciles them into the retry queue before ProcessAsync runs. Servers create the processor
        // before publishing, so their whole startup window arrives here.
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
                "Each property change will be processed individually without merging, " +
                "which can cause high CPU usage under load. " +
                "Consider setting a bufferTime (e.g., 8-50ms) to enable batching and merging.");
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

                if (ReferenceEquals(change.Origin.Source, _source) && !ChangeDeliveryFilter.NeedsWriteBack(in change))
                {
                    continue;
                }

                if (!_propertyFilter(change.Property))
                {
                    continue;
                }

                if (wasQueuedBeforeStart && !ChangeDeliveryFilter.IsCurrent(in change, _deliveryRule))
                {
                    continue;
                }

                if (periodicTimer is null)
                {
                    // A zero buffer time is the no-coalescing mode: every change reaches the source,
                    // including ones the model has since moved past. Suppressing under the client rule
                    // would break that, since a busy property has committed again by the time the
                    // previous write returns. A server has no such contract and must not serve a value
                    // it has moved past, so there the same rule applies as on the flush path.
                    if (_deliveryRule == ChangeDeliveryRule.SourceValuesAreSettled)
                    {
                        if (!ChangeDeliveryFilter.TryAcceptForDelivery(in change, _deliveryRule))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        ChangeDeliveryFilter.MarkPropertyAsPublishedToSource(in change);
                    }

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

        // Whether the merger was handed a batch, which decides whether it has one to release below. Set
        // before the call rather than after, so a throw part-way through a merge still releases it.
        var merged = false;

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

            merged = true;
            var mergedChanges = _changeMerger.Merge(CollectionsMarshal.AsSpan(_flushChanges), _deliveryRule);

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
            try
            {
                // Clear buffers to allow GC of SubjectPropertyChange objects
                _flushChanges.Clear();

                if (Volatile.Read(ref _disposed) == 1)
                {
                    // Disposed while flushing - return buffer to pool now
                    _changeMerger.Dispose();
                }
                else if (merged)
                {
                    // Only when there was a batch. An idle tick has nothing to release, and resetting
                    // anyway would feed the merger a zero-width batch: at the default buffer time that is
                    // roughly 125 of them a second, which drives its trim and shrink policies off how long
                    // the source has been quiet rather than off how wide its flushes actually are.
                    _changeMerger.Reset();
                }
            }
            finally
            {
                // Unconditionally, and after the cleanup rather than with it: a gate left at 1 makes every
                // later flush return at the try-enter while the dequeue loop keeps filling the queue, so
                // cleanup throwing would stop delivery permanently and grow the queue without bound.
                Volatile.Write(ref _flushGate, 0);
            }
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
                _changeMerger.Dispose();
            }
            finally
            {
                Volatile.Write(ref _flushGate, 0);
            }
        }
    }
}
