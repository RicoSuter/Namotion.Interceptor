using System;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>
/// A live subscription to a decomposed path from a root subject. It installs one per-property listener
/// per resolved segment (a subscribe-before-read chain) so a write to any watched link is observed, and
/// exposes the path's current observed value via a fresh, tracker-lock-free walk. Disposal is one-shot.
/// On a watched write it computes the event (a validating walk plus a divergent retrack of the suffix)
/// and delivers it synchronously via a direct invoke outside the lock; the ordered exclusive-drain queue
/// that serializes nested and concurrent deliveries is still pending in a later phase.
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
            BuildFrom(0, root);

            // Seed from a fresh walk AFTER the listeners are installed: any write that lands between the
            // build and this read is already covered by an installed listener, so the seed cannot open a
            // false unresolved -> resolved edge on the first delivery.
            _lastObserved = PathWalker.Walk<TValue>(_segments, _root, new IInterceptorSubject?[length]);
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

    /// <summary>One-shot dispose: after this returns, <see cref="Current"/> is unresolved.</summary>
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
    /// retrack, the kind decision, the suppression check and the <see cref="_lastObserved"/> advance) is
    /// computed under <see cref="_lock"/>; the observer callback is then invoked OUTSIDE the lock. When
    /// disposed or when the callback's observer is stale (a rebuild has already replaced it at its
    /// position), the invocation is dropped.
    /// </summary>
    private void ProcessSegmentCallback(PathSegmentObserver observer, in SubjectPropertyChange cause)
    {
        SubjectPathChangeKind kind;
        SubjectPathValue<TValue> oldObserved;
        SubjectPathValue<TValue> newValue;

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

            // Fresh validating walk from the root: it recomputes the observed value AND records, per
            // position, the subject each segment now reads on, which the divergence check compares
            // against the subscribed chain.
            var scratch = new IInterceptorSubject?[_segments.Length];
            newValue = PathWalker.Walk<TValue>(_segments, _root, scratch);

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
            kind = SubjectPathChangeKind.PathChange;
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

            // Suppression: an observed state equivalent to the last one (same resolvedness, equal value)
            // delivers nothing. Draining a queued backlog on a suppressed event arrives in a later phase.
            if (SubjectPathValue<TValue>.AreEquivalent(_lastObserved, newValue))
            {
                return;
            }

            // Advance BEFORE the callback: a transition chained from the callback then sees the new
            // baseline, and a throwing callback cannot replay this same transition.
            oldObserved = _lastObserved;
            _lastObserved = newValue;
        }

        // The observer callback runs OUTSIDE _lock: it may re-enter (a nested write), and holding the
        // lock across it would serialize the graph or deadlock. Ordered exclusive-drain delivery arrives
        // in a later phase; a direct invoke under the released lock is correct for a single event.
        var change = new SubjectPathChange<TValue>(kind, oldObserved, newValue, in cause);
        _observer.OnChange(in change);
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
