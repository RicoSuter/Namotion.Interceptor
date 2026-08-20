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
    // How long a stop waits for the teardown drain, and the binding constraint on every path, being
    // tighter than the host's 30 second default shutdown budget.
    //
    // Not configurable, because the two cases pull in opposite directions and neither wants a knob.
    // When the host is stopping, BackgroundService.StopAsync already abandons its wait at
    // HostOptions.ShutdownTimeout, so a per-connector value would only subdivide a budget the host
    // enforces anyway. Every other teardown, such as a subject detached from the graph, which is
    // stopped without ever being disposed, has no outer deadline at all, so something must bound it,
    // and that something is a safety net against a dead transport rather than a tuning parameter.
    internal static readonly TimeSpan TeardownFlushBound = TimeSpan.FromSeconds(5);

    private readonly Func<PropertyReference, bool> _propertyFilter;
    private readonly Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> _writeHandler;
    private readonly object? _source;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;
    private readonly ChangeDeliveryRule _deliveryRule;
    private readonly Action<long>? _dropHandler;

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
    /// Number of changes this processor accepted but never delivered: dropped on bounded-queue overflow,
    /// discarded because the write handler failed, discarded because it was cancelled on a path that
    /// buffers nothing, or still buffered when the teardown drain ended.
    /// </summary>
    public long DropCount => Interlocked.Read(ref _dropCount);

    /// <summary>
    /// Gets the number of changes currently buffered. Approximate: read without a lock while the
    /// pump is running. Always 0 when the processor is on its immediate path (no buffer time).
    /// </summary>
    public int QueueDepth => _changes.Count;

    // An abandoned teardown flush holds the gate until it unwinds, and Dispose is single-shot, so once
    // Dispose has lost the gate nothing it does later can return the merger's buffer: the flush's own
    // cleanup does that. The gate going free is therefore the only in-process evidence that an
    // abandoned flush was cancelled and cleaned up rather than left running forever.
    internal bool GateIsFreeForTest => Volatile.Read(ref _flushGate) == 0;

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
    /// this case explicitly, typically by resolving via <c>TryGetRegisteredProperty()</c> and
    /// returning <c>false</c> when null.</param>
    /// <param name="writeHandler">Handler to write batched changes.</param>
    /// <param name="deliveryRule">Which commits may supersede a change this processor is about to
    /// write; see <see cref="ChangeDeliveryRule"/> for the condition that decides it. Deliberately
    /// has no default: picking the wrong one is silent and its damage is permanent, so every connector
    /// states which it is.</param>
    /// <param name="bufferTime">Time to buffer changes before flushing.</param>
    /// <param name="maxQueueDepth">Bound on the buffered change queue, or null for unbounded (existing
    /// connector behavior). When set, enqueuing past the bound drops the oldest unprocessed change and
    /// increments <see cref="DropCount"/>, so the newest change is retained. Read only on the buffered
    /// path, so a processor with a buffer time of zero never touches the queue this bounds.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="dropHandler">Optional handler invoked with the number of changes that were accepted
    /// but never delivered: dropped on bounded-queue overflow, discarded because the write handler failed,
    /// discarded because it was cancelled on a path that buffers nothing, or still buffered when the
    /// teardown drain ended. Use this to report the count to queue diagnostics without adding work to
    /// successful enqueue or dequeue operations.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="deliveryRule"/> is
    /// <see cref="ChangeDeliveryRule.Unspecified"/> or not a defined value. Rejected here rather than at
    /// the first flush, where it would end delivery for this processor's lifetime. Also thrown when
    /// <paramref name="maxQueueDepth"/> is zero or negative and <paramref name="bufferTime"/> is
    /// greater than zero, since a bound has to leave room for at least one change.</exception>
    public ChangeQueueProcessor(
        object? source,
        IInterceptorSubjectContext context,
        Func<PropertyReference, bool> propertyFilter,
        Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> writeHandler,
        ChangeDeliveryRule deliveryRule,
        TimeSpan? bufferTime,
        int? maxQueueDepth,
        ILogger logger,
        Action<long>? dropHandler = null)
    {
        _source = source;
        _propertyFilter = propertyFilter;
        _writeHandler = writeHandler;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _dropHandler = dropHandler;

        try
        {
            ValidateMaxQueueDepth(maxQueueDepth, _bufferTime);

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
        ILogger logger,
        Action<long>? dropHandler = null)
    {
        _source = source;
        _propertyFilter = propertyFilter;
        _writeHandler = writeHandler;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _dropHandler = dropHandler;

        try
        {
            ValidateMaxQueueDepth(maxQueueDepth, _bufferTime);

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

    // Only on the buffered path: a buffer time of zero writes each change as it is dequeued and never
    // fills the queue this bounds, so the bound is not read there.
    private static void ValidateMaxQueueDepth(int? maxQueueDepth, TimeSpan bufferTime)
    {
        if (maxQueueDepth is <= 0 && bufferTime > TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueueDepth), maxQueueDepth,
                "A bounded change queue must have room for at least one change. Pass null for an unbounded " +
                "queue, or a buffer time of zero for the immediate path, which writes each change as it is " +
                "dequeued and buffers nothing.");
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
            })
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
                        // Nothing buffers on this path, so the teardown drain cannot recover this change
                        // and counting it is the only honest outcome left.
                        CountUndelivered(1);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        CountUndelivered(1);
                        _logger.LogError(ex, "Failed to write a change, which is discarded.");
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

            // Cannot throw: the delegate catches everything and Task.Run was given no token.
            await flushTask.ConfigureAwait(false);

            await FlushRemainingChangesAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the changes that were taken off the subscription but never flushed. Nothing else can
    /// recover them: they have already left the subscription, so the retry queue drain on the next
    /// attempt cannot see them, and a reconnecting connector's initial load then hides the loss by
    /// making both sides agree on a value the caller never wrote.
    /// </summary>
    private async Task FlushRemainingChangesAsync()
    {
        if (_changes.IsEmpty)
        {
            return;
        }

        // A fresh token, not the one ProcessAsync was given: that one is already cancelled here, so
        // writing under it would fail every change, which is the loss this exists to prevent.
        var teardownTokenSource = new CancellationTokenSource(TeardownFlushBound);

        // Read once, before the task starts, because the delegate below disposes the source as soon as
        // the flush returns. Reading Token at the wait instead would race that disposal, and Token
        // throws ObjectDisposedException once the source is gone.
        var teardownToken = teardownTokenSource.Token;

        // Off this thread, because the token only bounds a handler that observes it and the OPC UA
        // server writes synchronously under the SDK's node manager lock. Awaiting inline would bound
        // nothing. The task owns the token source, so the cancel-after timer stays alive for as long as
        // the work it bounds, rather than being killed by a using on the abandoning thread.
        var flushTask = Task.Run(async () =>
        {
            try
            {
                await TryFlushAsync(teardownToken).ConfigureAwait(false);
            }
            finally
            {
                // Inside the task, so it runs after any requeue this flush performed on its unwind, even
                // when the waiter gave up at the deadline long before.
                CountRemainingAfterDrain();
                teardownTokenSource.Dispose();
            }
        });

        try
        {
            // One deadline rather than two. Waiting on the same token the flush runs under removes the
            // race in which an independent timeout disposed the source and killed its timer.
            await flushTask.WaitAsync(teardownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // An abandoned write can still reach the wire after the caller has begun tearing the
            // transport down; every client here rejects a write once disposed, so that fails cleanly.
            _logger.LogWarning(
                "Gave up waiting after {Bound} for the remaining buffered changes to be written while " +
                "stopping. A write handler that ignores cancellation may still complete it.",
                TeardownFlushBound);

            ObserveAbandonedFlush(flushTask);
            return;
        }
        catch (Exception ex)
        {
            // Never rethrown: a throw here would replace the failure that ended the processing loop.
            _logger.LogError(ex, "Failed to write the remaining buffered changes while stopping.");
        }
    }

    // Runs only once the drain has settled, never on the thread that gave up waiting. Counting at the
    // deadline would race the requeue a cancelled flush performs on its own unwind and could read the
    // queue before the batch is put back, which is exactly the loss this drain exists to prevent.
    private void CountRemainingAfterDrain()
    {
        var remaining = 0L;
        while (_changes.TryDequeue(out _))
        {
            remaining++;
        }

        if (remaining > 0)
        {
            CountUndelivered(remaining);
            _logger.LogWarning(
                "{Count} buffered changes were not written while stopping and are discarded.", remaining);
        }
    }

    // Once the wait is abandoned nothing observes the flush task: WaitAsync removes its own completion
    // action when it cancels. The common ending is Canceled, which raises nothing and does not run this
    // continuation either. What this exists for is the abandoned flush that then fails for some other
    // reason, which does end Faulted and would otherwise surface as an UnobservedTaskException.
    private static void ObserveAbandonedFlush(Task flushTask)
    {
        _ = flushTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Drops the oldest buffered changes until the queue is back within <paramref name="maxQueueDepth"/>,
    /// incrementing <see cref="DropCount"/> for each. Best-effort: a concurrent flush may drain the queue
    /// below the bound first, in which case fewer drops occur.
    /// </summary>
    private void DropOverflow(int maxQueueDepth)
    {
        var droppedCount = 0L;
        while (_changes.Count > maxQueueDepth && _changes.TryDequeue(out _))
        {
            Interlocked.Increment(ref _dropCount);
            droppedCount++;
        }

        if (droppedCount > 0)
        {
            _dropHandler?.Invoke(droppedCount);
        }
    }

    // One place for every "accepted by this processor and not delivered" count, so DropCount and the
    // drop handler cannot disagree about what happened.
    private void CountUndelivered(long count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _dropCount, count);
        _dropHandler?.Invoke(count);
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
                    // Cancellation means the batch was never attempted or never confirmed, not that it
                    // failed, and nothing else can recover it: it has already left the subscription.
                    // Returning it to the queue is what gives the teardown drain something to hand over.
                    // Order is safe because the merger resolves by commit revision rather than queue
                    // position, and the dequeue loop has already exited by the time this runs at a stop.
                    foreach (var change in _flushChanges)
                    {
                        _changes.Enqueue(change);
                    }

                    throw;
                }
                catch (Exception ex)
                {
                    // Counted rather than requeued. A transport that died on its own throws here rather
                    // than cancelling, and requeueing against one that keeps failing would grow the
                    // queue without bound.
                    var undelivered = mergedChanges.Length;
                    CountUndelivered(undelivered);
                    _logger.LogError(ex, "Failed to write {Count} changes, which are discarded.", undelivered);
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
