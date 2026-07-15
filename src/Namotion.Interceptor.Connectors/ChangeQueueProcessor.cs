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

    // Bounded post-write revalidation attempts per correction. On exhaustion the correction is
    // dropped and the source recovers on its next inbound event or an explicit resynchronization.
    private const int MaxCorrectionRevalidationAttempts = 8;

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
    private int _processingStarted; // 0 = never started, 1 = ProcessAsync called (single-consumer guard)

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

    // Corrections separated out of a flush batch; delivered individually with send-time revalidation.
    private readonly List<SubjectPropertyChange> _flushCorrections = [];
    private readonly SubjectPropertyChange[] _correctionBuffer = new SubjectPropertyChange[1];

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
            ArrayPool<SubjectPropertyChange>.Shared.Return(_flushDedupedBuffer);
            _flushDedupedBuffer = null!;
            throw;
        }
    }

    /// <summary>
    /// Processes changes from the queue until cancellation is requested.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task.</returns>
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        // Single-consumer contract, enforced: the flush scratch buffers are unsynchronized because
        // exactly one processing loop may ever run.
        if (Interlocked.Exchange(ref _processingStarted, 1) == 1)
        {
            throw new InvalidOperationException("ProcessAsync may only be called once per ChangeQueueProcessor instance.");
        }

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
                // A correction is not an echo of anything (no model change occurred), so it bypasses
                // the own-source skip entirely; property filters and connector topology decide the
                // actual recipients. All other kinds keep the single-comparison echo suppression.
                if (change.Origin.Kind != ChangeOriginKind.Correction &&
                    ReferenceEquals(change.Origin.Source, _source))
                {
                    continue;
                }

                if (!_propertyFilter(change.Property))
                {
                    continue;
                }

                if (periodicTimer is null)
                {
                    if (change.Origin.Kind == ChangeOriginKind.Correction)
                    {
                        // Immediate mode has no dedup, but dedup was never the safety mechanism for
                        // corrections: send-time revalidation is. WriteCorrectionWithRevalidationAsync
                        // re-reads the live model before writing (the model is updated at the terminal
                        // write before the change is enqueued), so a stale correction racing a normal
                        // change is dropped here just as it would be in a buffered flush.
                        await WriteCorrectionWithRevalidationAsync(change, linkedTokenSource.Token).ConfigureAwait(false);
                        continue;
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
    // Kind-blind eviction: a burst of corrections can evict queued normal changes, losing data
    // toward the source until its next inbound event. Latent today (every in-repo caller passes a
    // null maxQueueDepth); revisit with kind-aware eviction if the bound is ever wired up.
    private void DropOverflow(int maxQueueDepth)
    {
        while (_changes.Count > maxQueueDepth && _changes.TryDequeue(out _))
        {
            Interlocked.Increment(ref _dropCount);
        }
    }

    /// <summary>
    /// Test seam (see <c>InternalsVisibleTo</c>): enqueues a change straight into the flush buffer,
    /// bypassing the subscription and its echo/property filtering, so delivery and deduplication can
    /// be exercised with hand-crafted changes that the write pipeline cannot produce. Pair with
    /// <see cref="TryFlushAsync"/> for a single deterministic flush.
    /// </summary>
    internal void EnqueueForTesting(in SubjectPropertyChange change) => _changes.Enqueue(change);

    // Internal rather than private so tests can trigger one deterministic flush without reflection;
    // production drives it from the ProcessAsync buffer timer.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    internal async ValueTask TryFlushAsync(CancellationToken cancellationToken)
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
            _flushCorrections.Clear(); // dedup below may already route displaced corrections here

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
            // Track whether any correction survives into the deduped buffer so the all-normal flush
            // (the overwhelmingly common case) can skip the correction-partition rescan below.
            var bufferHasCorrection = false;
            for (var i = _flushChanges.Count - 1; i >= 0; i--)
            {
                var change = _flushChanges[i];
                if (!_flushPropertyIndices.TryGetValue(change.Property, out var existingIndex))
                {
                    _flushPropertyIndices[change.Property] = _flushDedupedCount;
                    _flushDedupedBuffer[_flushDedupedCount++] = change;
                    bufferHasCorrection |= change.Origin.Kind == ChangeOriginKind.Correction;
                }
                else
                {
                    // Earlier occurrence for a property already kept (as its later occurrence).
                    // Normal changes own the batch slot: two normals coalesce (oldest old value,
                    // newest new value), and a normal displaces a kept correction from the slot. A
                    // correction on either side of a normal is routed through send-time revalidation
                    // instead of being dropped: dedup order says nothing about freshness (a fresh
                    // correction can legitimately sit behind an older normal change in the queue,
                    // because the inbound apply that made it fresh is echo-suppressed and never
                    // queued), and revalidation against the live model is what actually decides
                    // staleness. Only a correction displaced by another correction is dropped: they
                    // coalesce, and the kept one re-asserts the same live model value.
                    var existing = _flushDedupedBuffer[existingIndex];
                    var existingIsCorrection = existing.Origin.Kind == ChangeOriginKind.Correction;
                    if (change.Origin.Kind != ChangeOriginKind.Correction)
                    {
                        if (existingIsCorrection)
                        {
                            _flushCorrections.Add(existing);
                            _flushDedupedBuffer[existingIndex] = change;
                        }
                        else
                        {
                            _flushDedupedBuffer[existingIndex] = change.MergeWithNewer(existing);
                        }
                    }
                    else if (!existingIsCorrection)
                    {
                        _flushCorrections.Add(change);
                    }
                }
            }

            // Reverse to restore chronological order of last occurrences
            if (_flushDedupedCount > 1)
            {
                Array.Reverse(_flushDedupedBuffer, 0, _flushDedupedCount);
            }

            if (_flushDedupedCount > 0)
            {
                // Partition: normal changes are written as one batch; corrections are delivered one
                // at a time with send-time revalidation (including corrections the dedup above
                // displaced with a normal change for the same property; revalidation against the
                // live model decides which of the two representations of that property is current).
                // The all-normal flush needs no partition, so it writes the deduped buffer as-is.
                var normalCount = _flushDedupedCount;
                if (bufferHasCorrection)
                {
                    normalCount = 0;
                    for (var i = 0; i < _flushDedupedCount; i++)
                    {
                        var change = _flushDedupedBuffer[i];
                        if (change.Origin.Kind == ChangeOriginKind.Correction)
                        {
                            _flushCorrections.Add(change);
                        }
                        else
                        {
                            // Compact normals to the front; normalCount <= i always, so no unread slot
                            // is overwritten (each correction was already copied into _flushCorrections).
                            _flushDedupedBuffer[normalCount++] = change;
                        }
                    }
                }

                if (normalCount > 0)
                {
                    try
                    {
                        await _writeHandler(new ReadOnlyMemory<SubjectPropertyChange>(_flushDedupedBuffer, 0, normalCount), cancellationToken).ConfigureAwait(false);
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

                for (var i = 0; i < _flushCorrections.Count; i++)
                {
                    await WriteCorrectionWithRevalidationAsync(_flushCorrections[i], cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Clear buffers to allow GC of SubjectPropertyChange objects
            _flushChanges.Clear();
            _flushPropertyIndices.Clear();
            _flushCorrections.Clear();

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
    /// Writes a single correction to the source with per-correction send-time revalidation. Pre-write
    /// revalidation is a cheap early drop; the post-write bounded loop closes the in-flight window, where
    /// an echo-suppressed inbound-from-source apply lands while the write is in flight and is therefore
    /// still visible to the post-write read. An apply that lands after that read is beyond any send-time
    /// check and stays the documented delayed-notification residue (#373); it is the same residue an
    /// ordinary outbound write carries, not one corrections introduce.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask WriteCorrectionWithRevalidationAsync(SubjectPropertyChange correction, CancellationToken cancellationToken)
    {
        var property = correction.Property;
        var getValue = property.Metadata.GetValue;
        if (getValue is null)
        {
            _logger.LogDebug("Correction for {Property} has no getter to revalidate against; dropping.", property);
            return;
        }

        var propertyType = property.Metadata.Type;
        var correctionValue = correction.GetNewValue<object?>();

        // Pre-write revalidation (cheap early drop): if the model already moved, its newer change is
        // flowing outbound on its own; drop the stale correction. This read alone cannot see an
        // in-flight apply that lands during the write; the post-write loop handles that. Equality uses
        // the property type's own semantics (matching detection and the equality handler), so an
        // IEquatable getter returning a fresh equivalent instance is not misread as a move.
        if (!TryReadModelValue(out var modelValue) || !PropertyValueEquality.Equals(propertyType, modelValue, correctionValue))
        {
            return;
        }

        var attempt = 0;
        while (true)
        {
            // Stamped fresh at send time (a queued correction may have waited for a flush interval, and
            // connectors serialize ChangedTimestamp outbound). Plain local clock, no wire-ordering
            // arithmetic; see the synthesis comment in SubjectChangeContextExtensions.
            _correctionBuffer[0] = SubjectPropertyChange.Create(
                property, correction.Origin,
                SubjectChangeContext.GetTimestampFunction(),
                correction.ReceivedTimestamp,
                correctionValue, correctionValue);

            try
            {
                await _writeHandler(new ReadOnlyMemory<SubjectPropertyChange>(_correctionBuffer, 0, 1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write correction.");
                return;
            }
            finally
            {
                _correctionBuffer[0] = default;
            }

            // Post-write revalidation: if the model no longer equals what was just written, an inbound
            // apply from this source landed during the write (a local write in flight would have enqueued
            // its own following normal change), so re-assert the current model value as a follow-up
            // correction. Closes the in-flight window up to this read only; an apply landing after it is
            // the delayed-notification residue (#373), no different from an ordinary outbound write.
            if (!TryReadModelValue(out modelValue))
            {
                return;
            }

            if (PropertyValueEquality.Equals(propertyType, modelValue, correctionValue))
            {
                break; // converged: the source holds the current model value
            }

            if (++attempt >= MaxCorrectionRevalidationAttempts)
            {
                _logger.LogWarning(
                    "Correction for {Property} did not converge after {Attempts} attempts; dropping. " +
                    "The source recovers on its next inbound event or an explicit RequestResynchronization (#342).",
                    property, attempt);
                break;
            }

            correctionValue = modelValue; // follow-up correction to the newer model value
        }

        // A user getter (or read interceptor) that throws must drop the correction, not escape:
        // in immediate mode this method is awaited directly in the ProcessAsync dequeue loop, and
        // an escaping exception would terminate the processor. Read committed state: the processing
        // loop normally carries no transaction, but if one flowed in via the AsyncLocal captured when
        // ProcessAsync was started, ReadCommittedValue detaches it so revalidation compares against
        // committed state, not a pending overlay (matching synthesis).
        bool TryReadModelValue(out object? value)
        {
            try
            {
                value = SubjectChangeContextExtensions.ReadCommittedValue(property.Subject, getValue);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Correction revalidation read for {Property} threw; dropping the correction.", property);
                value = null;
                return false;
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

        _subscription.Dispose();

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
