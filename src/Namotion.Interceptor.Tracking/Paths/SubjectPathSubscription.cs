using System;
using System.Collections.Generic;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>
/// A live subscription to a decomposed path from a root subject. It installs one per-property listener
/// per resolved segment (a subscribe-before-read chain) so a write to any watched link is observed, and
/// exposes the path's current observed value via a fresh, tracker-lock-free walk. Disposal is one-shot.
/// On a watched write it computes the event (a validating walk plus a divergent retrack of the suffix)
/// under the lock, then delivers it through an ordered exclusive-drain queue: one drainer at a time runs
/// callbacks outside the lock, so nested and concurrent writes enqueue and are delivered in FIFO order
/// after the current callback returns (flattened, never inline).
/// </summary>
public sealed class SubjectPathSubscription<TValue> : IDisposable
{
    private readonly IInterceptorSubject _root;
    private readonly PathSegment[] _segments;
    private readonly ISubjectPathChangeObserver<TValue> _observer;

    private readonly Lock _lock = new();

    // All arrays are indexed by segment position and are mutated only under _lock. _segmentHandles[p] is
    // the installed listener for position p; _segmentObservers[p] is the CURRENT observer for position p
    // (the slot-identity record a late callback is matched against); _resolvedSubjects[p] is the subject
    // segment p is read on. Entries beyond the resolved prefix stay null.
    private readonly IDisposable?[] _segmentHandles;
    private readonly PathSegmentObserver?[] _segmentObservers;
    private readonly IInterceptorSubject?[] _resolvedSubjects;

    // The last observed value, seeded at subscribe time and advanced in ProcessSegmentCallback (before the
    // observer callback runs) so an event's Old reflects prior observed state rather than a false
    // unresolved starting point.
    private SubjectPathValue<TValue> _lastObserved;

    // Ordered exclusive-drain delivery, both guarded by _lock. At most one thread is the drainer
    // (_draining); every other computed event enqueues into _pending, and the drainer delivers them
    // FIFO with the lock released. This flattens nested/concurrent writes and preserves total order.
    private readonly Queue<SubjectPathChange<TValue>> _pending = new();
    private bool _draining;

    // Set (under _lock) by the thread-affine reentrancy guard when a property getter invoked during this
    // subscription's own event walk writes a watched segment and re-enters ProcessSegmentCallback on the
    // SAME thread. The in-flight computation loop observes it and re-walks, so the getter's write is picked
    // up by a fresh event after the current walk finishes instead of corrupting it.
    private bool _deferredRevalidation;

    private bool _disposed;

    internal SubjectPathSubscription(IInterceptorSubject root, PathSegment[] segments, ISubjectPathChangeObserver<TValue> observer)
    {
        _root = root;
        _segments = segments;
        _observer = observer;

        var length = segments.Length;
        _segmentHandles = new IDisposable?[length];
        _segmentObservers = new PathSegmentObserver?[length];
        _resolvedSubjects = new IInterceptorSubject?[length];

        lock (_lock)
        {
            try
            {
                BuildFrom(0, root);

                // Seed from a fresh walk AFTER the listeners are installed: any write that lands between the
                // build and this read is already covered by an installed listener, so the seed cannot open a
                // false unresolved -> resolved edge on the first delivery.
                _lastObserved = PathWalker.Walk<TValue>(_segments, _root, new IInterceptorSubject?[length]);
            }
            catch
            {
                // Teardown on throw: BuildFrom only throws on a reserved-key contract violation
                // (PropertyChangeSubscription.Create raising InvalidOperationException for a foreign ni.pcl
                // value; name resolution during the build is non-throwing, and the seed walk never throws).
                // Dispose the segment handles already installed so the process-wide count is rolled back
                // before the exception propagates and no partial subscription escapes to the caller.
                DisposeSuffix(0);
                throw;
            }
        }
    }

    /// <summary>
    /// The path's current observed value, computed by a fresh walk from the root on every read (never
    /// cached) so it always reflects the live graph. The walk takes no tracker lock and never throws:
    /// any unreachable segment resolves to <see cref="SubjectPathValue{TValue}.Unresolved"/>. Returns
    /// unresolved once the subscription is disposed.
    /// </summary>
    public SubjectPathValue<TValue> Current
    {
        get
        {
            if (Volatile.Read(ref _disposed))
            {
                return SubjectPathValue<TValue>.Unresolved;
            }

            // A fresh per-read buffer: the array holds reference-type subjects, so it cannot be stack
            // allocated. Allocation-free delivery is a concern of the event path (a later phase), not of
            // this pure read.
            var resolvedSubjects = new IInterceptorSubject?[_segments.Length];
            return PathWalker.Walk<TValue>(_segments, _root, resolvedSubjects);
        }
    }

    /// <summary>
    /// One-shot dispose: tears down every installed segment listener (returning the process-wide count to
    /// zero), drops any queued-but-undelivered events, and marks the subscription disposed. Idempotent and
    /// never throws; after it returns <see cref="Current"/> is unresolved. A callback already dispatched
    /// outside the lock may run to completion, but the drain loop observes the disposed flag and starts no
    /// new delivery, so post-dispose delivery is bounded.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _disposed, true);
            DisposeSuffix(0);
            _pending.Clear();
        }
    }

    /// <summary>
    /// Installs the subscribe-before-read chain from <paramref name="startPosition"/>, walking forward
    /// from <paramref name="startSubject"/>. For each segment the listener is installed BEFORE the read
    /// that resolves the next subject, so a write landing in that window is never missed. The build stops
    /// at the first unresolved intermediate, leaving the suffix (handles/observers/subjects past that
    /// position) null. Callers must hold <see cref="_lock"/>.
    /// </summary>
    private void BuildFrom(int startPosition, IInterceptorSubject startSubject)
    {
        var subject = startSubject;
        for (var position = startPosition; position < _segments.Length; position++)
        {
            var segment = _segments[position];

            // Record the current observer for this position (slot identity) and the subject this segment
            // reads on, then install the listener.
            var observer = new PathSegmentObserver(this) { Position = position };
            _segmentObservers[position] = observer;
            _resolvedSubjects[position] = subject;

            // Subscribe FIRST, then resolve the next subject below: the install must precede the read.
            _segmentHandles[position] = PropertyChangeSubscription.Create(
                new PropertyReference(subject, segment.PropertyName), observer);

            if (segment.IsLeaf)
            {
                // The leaf is subscribed but resolves no next subject.
                return;
            }

            var child = TryResolveChild(subject, segment);
            if (child is null)
            {
                // Unresolved intermediate: stop, leaving the suffix torn down (null).
                return;
            }

            subject = child;
        }
    }

    /// <summary>
    /// Resolves the next subject of a non-leaf segment by name against <paramref name="subject"/>. Name
    /// resolution during the build is non-throwing: a missing or non-subscribable property, or a hostile
    /// getter/indexer that throws, all resolve to null so the build stops cleanly.
    /// </summary>
    private static IInterceptorSubject? TryResolveChild(IInterceptorSubject subject, PathSegment segment)
    {
        if (!subject.Properties.TryGetValue(segment.PropertyName, out var metadata))
        {
            return null;
        }

        if (!(metadata.IsIntercepted || metadata.IsDerived))
        {
            return null;
        }

        try
        {
            return PathWalker.ResolveChild(subject, metadata, segment);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Tears down the chain from <paramref name="fromPosition"/> onward: disposes and clears each
    /// listener, its observer, and the subject it was read on. Entries the suffix owned become null so a
    /// later rebuild (or dispose) starts from a clean tail. Callers must hold <see cref="_lock"/>.
    /// </summary>
    private void DisposeSuffix(int fromPosition)
    {
        for (var position = fromPosition; position < _segments.Length; position++)
        {
            _segmentHandles[position]?.Dispose();
            _segmentHandles[position] = null;
            _segmentObservers[position] = null;
            _resolvedSubjects[position] = null;
        }
    }

    /// <summary>
    /// Entry point for a per-segment listener callback. All state (the validating walk, a divergent
    /// retrack, the kind decision, the suppression check, the <see cref="_lastObserved"/> advance and the
    /// enqueue/claim-drainer decision) is computed under <see cref="_lock"/>; the observer callbacks are
    /// then run OUTSIDE the lock by the exclusive drainer. A getter that writes a watched segment during the
    /// walk re-enters here on the same thread; the thread-affine guard defers it and the computation loops
    /// until no deferral remains, so a side-effecting getter never deadlocks nor runs a callback under the
    /// lock. When disposed or when the callback's observer is stale (a rebuild has already replaced it at its
    /// position), the invocation is dropped.
    /// </summary>
    private void ProcessSegmentCallback(PathSegmentObserver observer, in SubjectPropertyChange cause)
    {
        // Thread-affine reentrancy guard. If THIS thread already holds _lock we are re-entering our own
        // computation: a property getter invoked by the event walk below wrote a watched segment of this
        // same subscription, and that write dispatched synchronously back here. Computing now would run a
        // nested walk while the outer one is mid-flight (corrupting the outer walk and the _lastObserved
        // advance) and could even claim the drainer and run a callback while _lock is still held, an AB-BA
        // hazard. Instead flag a deferred revalidation and return WITHOUT computing; the outer computation
        // loop re-walks once it finishes, so the getter's write is observed by a fresh event and callbacks
        // never run under _lock. IsHeldByCurrentThread is a thread-local read, free on the common path.
        if (_lock.IsHeldByCurrentThread)
        {
            _deferredRevalidation = true;
            return;
        }

        // The uncontended fast path hands the drainer a single stack-local event, skipping the enqueue and
        // dequeue copies of the large change struct. Both live outside the lock so the drain section can read
        // them after the lock is released.
        SubjectPathChange<TValue> directEvent = default;
        var hasDirectEvent = false;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            // Slot identity: a callback whose observer is no longer the current one for its position was
            // fired by a torn-down listener; drop it rather than acting on a stale chain.
            if (_segmentObservers[observer.Position] != observer)
            {
                return;
            }

            // Suppress the ambient transaction for the whole computation below. A [Derived]-with-setter or
            // cross-context write on a transaction-holding flow bypasses staging and dispatches here
            // synchronously, so the validating walk and every retrack read run WHILE a transaction is active.
            // Left unsuppressed, those reads would return the transaction's staged read-your-writes view, and
            // a divergence computed against a speculative staged subject would retrack the chain onto it and
            // then be stranded when the transaction rolls back. Reading committed state keeps every retrack
            // decision durable. Restored in the finally BEFORE the drain runs callbacks (outside the lock), so
            // callbacks still observe the caller's transaction. Gated on HasActiveTransaction so the AsyncLocal
            // round-trip is paid only when a transaction is actually active during dispatch.
            var ambientTransaction = SubjectTransaction.HasActiveTransaction ? SubjectTransaction.Current : null;
            if (ambientTransaction is not null)
            {
                SubjectTransaction.SetCurrent(null);
            }

            try
            {
                // Deferred-revalidation loop. Each pass computes one event (a validating walk, a divergent
                // retrack, the kind decision, the suppression check and the _lastObserved advance). A getter
                // that wrote a watched segment during a pass's walk re-entered via the guard above and set
                // _deferredRevalidation, so the loop recomputes a fresh event until no deferral remains. Passes
                // enqueue in order, giving total FIFO delivery; the loop settles fully before any callback runs,
                // so callbacks never run under _lock. The common (no side-effecting getter) case runs the loop
                // exactly once. Termination assumes convergence: a getter that writes a fresh, non-suppressed
                // watched value on EVERY walk would loop here forever holding _lock, but that is a caller
                // contract violation (getters must be side-effect-free), not a supported case.
                SubjectPathChange<TValue> firstEvent = default;
                var hasFirstEvent = false;
                do
                {
                    _deferredRevalidation = false;

                    // Fresh validating walk from the root: it recomputes the observed value AND records, per
                    // position, the subject each segment now reads on, which the divergence check compares
                    // against the subscribed chain.
                    var scratch = new IInterceptorSubject?[_segments.Length];
                    var newValue = PathWalker.Walk<TValue>(_segments, _root, scratch);

                    // Divergence is the first position where the fresh walk reads on a different subject than the
                    // subscribed chain (reference identity). This one comparison covers every structural case: an
                    // intact chain never diverges; a reassigned intermediate, a heal (null -> subject) and a break
                    // (subject -> null) each first differ at the affected position.
                    var divergencePoint = -1;
                    for (var position = 0; position < _segments.Length; position++)
                    {
                        if (!ReferenceEquals(scratch[position], _resolvedSubjects[position]))
                        {
                            divergencePoint = position;
                            break;
                        }
                    }

                    var diverged = divergencePoint >= 0;
                    if (diverged)
                    {
                        // Retrack the suffix below the change: tear down the stale listeners, then reinstall the
                        // subscribe-before-read chain from the new subject. A null divergent subject is a break
                        // (an unresolved intermediate); dispose only, leaving the suffix torn down.
                        var divergentSubject = scratch[divergencePoint];
                        DisposeSuffix(divergencePoint);
                        if (divergentSubject is not null)
                        {
                            BuildFrom(divergencePoint, divergentSubject);
                        }

                        // Recompute from the rebuilt chain: the retrack's reads supersede the initial walk, so a
                        // write that raced the listener install is observed rather than lost.
                        newValue = PathWalker.Walk<TValue>(_segments, _root, new IInterceptorSubject?[_segments.Length]);
                    }

                    // ValueChange only when the intact chain's own resolved leaf was the write; any structural
                    // change (divergence) or an unresolved result is a PathChange.
                    var kind = SubjectPathChangeKind.PathChange;
                    if (!diverged && newValue.IsResolved)
                    {
                        var leafSubject = _resolvedSubjects[_segments.Length - 1];
                        var leafName = _segments[_segments.Length - 1].PropertyName;
                        if (leafSubject is not null
                            && ReferenceEquals(cause.Property.Subject, leafSubject)
                            && string.Equals(cause.Property.Name, leafName, StringComparison.Ordinal))
                        {
                            kind = SubjectPathChangeKind.ValueChange;
                        }
                    }

                    if (SubjectPathValue<TValue>.AreEquivalent(_lastObserved, newValue))
                    {
                        // Suppressed: an observed state equivalent to the last one delivers nothing and does not
                        // advance the baseline. The pass still counts for the loop because its own walk may have
                        // set _deferredRevalidation via a getter write.
                        continue;
                    }

                    // Advance BEFORE the callback: a transition chained from the callback then sees the new
                    // baseline, and a throwing callback cannot replay this same transition.
                    var oldObserved = _lastObserved;
                    _lastObserved = newValue;

                    // A deferred pass reuses the outer triggering cause (not the getter write that produced this
                    // delta), so Cause/Kind reflect the triggering write while Old/New are the true observed
                    // transition. Only reachable under a getter contract violation, so acceptable, but noted so a
                    // future debugger of the cause fields is not confused.
                    var change = new SubjectPathChange<TValue>(kind, oldObserved, newValue, in cause);

                    if (!hasFirstEvent && _pending.Count == 0)
                    {
                        // Hold the first event of an uncontended call in a local so the common no-deferral path
                        // can deliver it without touching the queue (zero copy). A later pass flushes it first.
                        firstEvent = change;
                        hasFirstEvent = true;
                    }
                    else
                    {
                        // Second (deferred) event this call, or a prior throwing callback stranded a backlog:
                        // flush any held first event, then enqueue, so total order is enqueue order.
                        if (hasFirstEvent)
                        {
                            _pending.Enqueue(firstEvent);
                            hasFirstEvent = false;
                        }

                        _pending.Enqueue(change);
                    }
                }
                while (_deferredRevalidation);

                if (_draining)
                {
                    // A drainer is active (a nested or concurrent write): hand it everything this call produced
                    // and let it deliver after the current callback returns. Flattens nesting, preserves FIFO.
                    if (hasFirstEvent)
                    {
                        _pending.Enqueue(firstEvent);
                    }

                    return;
                }

                if (!hasFirstEvent && _pending.Count == 0)
                {
                    // Every pass was suppressed and no prior throwing callback stranded a backlog: nothing to do.
                    return;
                }

                // Claim the drainer; it runs the callbacks below with _lock released.
                _draining = true;

                if (hasFirstEvent)
                {
                    // hasFirstEvent implies _pending was empty when the single event was produced and no later
                    // pass or concurrent producer could enqueue under the held lock, so this is the uncontended
                    // zero-copy fast path: deliver directly without touching the queue.
                    directEvent = firstEvent;
                    hasDirectEvent = true;
                }
                // Otherwise multiple events and/or a stranded backlog are already queued; the drain loop delivers.
            }
            finally
            {
                // Restore the caller's ambient transaction. This runs before the drain section (outside the
                // lock) so callbacks observe the transaction, and guarantees restoration even if a retrack's
                // BuildFrom throws a reserved-key contract violation while suppression is in effect.
                if (ambientTransaction is not null)
                {
                    SubjectTransaction.SetCurrent(ambientTransaction);
                }
            }
        }

        // Drain section: all observer callbacks run here, OUTSIDE _lock. A callback may re-enter (a nested
        // write); holding the lock across it would serialize the graph or deadlock.
        try
        {
            if (hasDirectEvent)
            {
                _observer.OnChange(in directEvent);
            }

            while (true)
            {
                SubjectPathChange<TValue> next;
                lock (_lock)
                {
                    // Atomic empty-check and drainer clear: a write enqueuing just as the drainer exits is
                    // either seen here (dequeued and delivered) or finds _draining false under the lock and
                    // becomes the next drainer. Neither loses the event nor lets two threads drain at once.
                    // The disposed check bounds post-dispose delivery: once disposal is observed the drainer
                    // starts no new delivery and drops the remaining backlog (Dispose already cleared
                    // _pending), so at most the one callback already dispatched outside the lock completes.
                    if (_disposed || _pending.Count == 0)
                    {
                        _draining = false;
                        return;
                    }

                    next = _pending.Dequeue();
                }

                _observer.OnChange(in next);
            }
        }
        catch
        {
            // A throwing callback abandons the drain. Reset the drainer flag (leaving _pending intact) so the
            // next computed event can claim the drainer and deliver the stranded backlog; without this reset
            // _draining stays true forever and every later event strands permanently.
            lock (_lock)
            {
                _draining = false;
            }

            throw;
        }
    }

    /// <summary>
    /// A per-position property listener. Its identity is the slot-identity token
    /// (<see cref="Position"/>): the subscription matches a callback against the observer currently
    /// recorded for that position to reject deliveries from a torn-down build.
    /// </summary>
    private sealed class PathSegmentObserver(SubjectPathSubscription<TValue> subscription) : IPropertyChangeObserver
    {
        public required int Position { get; init; }

        public void OnChange(in SubjectPropertyChange change) => subscription.ProcessSegmentCallback(this, in change);
    }
}
