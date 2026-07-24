using System;
using System.Threading;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>
/// Owns the subscribe-before-read segment chain for one <see cref="SubjectPathSubscription{TValue}"/>: the
/// installed per-segment listeners, the observer recorded per position (slot identity), the subject each
/// segment reads on, and the cached per-segment accessors. It creates the
/// <see cref="PathSegmentObserver{TValue}"/> instances, installs and tears down listeners, walks the graph,
/// and reports divergence, but holds NO lock of its own.
/// </summary>
/// <remarks>
/// LOCK CONTRACT: every method is called under the coordinator's lock, EXCEPT the <c>_resolvedSegments</c>
/// reads that <see cref="Walk"/> performs when it is called from the lock-free
/// <see cref="SubjectPathSubscription{TValue}.Current"/>; those reads go through <see cref="Volatile"/>. The
/// immutable root and segment array are safe to read without the lock. Subscribe-before-read ordering in
/// <see cref="BuildFrom"/> (install the listener, THEN read to resolve the next subject) is load-bearing for
/// the missed-write guarantee and must be preserved exactly.
/// </remarks>
internal sealed class PathSubscriptionChain<TValue>
{
    private readonly SubjectPathSubscription<TValue> _coordinator;
    private readonly IInterceptorSubject _root;
    private readonly PathSegment[] _segments;

    // All arrays are indexed by segment position and are mutated only under the coordinator's lock.
    // _segmentHandles[p] is the installed listener for position p; _segmentObservers[p] is the CURRENT
    // observer for position p (the slot-identity record a late callback is matched against);
    // _resolvedSubjects[p] is the subject segment p is read on. Entries beyond the resolved prefix stay null.
    private readonly IDisposable?[] _segmentHandles;
    private readonly PathSegmentObserver<TValue>?[] _segmentObservers;
    private readonly IInterceptorSubject?[] _resolvedSubjects;
    // Immutable subject/accessor pairs used by the hot validating walk. Entries are atomically replaced
    // for lock-free Current reads and cleared with the same suffix as the corresponding subject/listener.
    private readonly ResolvedPathSegment<TValue>?[] _resolvedSegments;

    internal PathSubscriptionChain(SubjectPathSubscription<TValue> coordinator, IInterceptorSubject root, PathSegment[] segments)
    {
        _coordinator = coordinator;
        _root = root;
        _segments = segments;

        var length = segments.Length;
        _segmentHandles = new IDisposable?[length];
        _segmentObservers = new PathSegmentObserver<TValue>?[length];
        _resolvedSubjects = new IInterceptorSubject?[length];
        _resolvedSegments = new ResolvedPathSegment<TValue>?[length];
    }

    /// <summary>The number of segments in the path. Immutable; safe to read without the lock.</summary>
    internal int Length => _segments.Length;

    /// <summary>
    /// Walks the segments from the root into <paramref name="buffer"/> and returns the observed leaf value.
    /// Reads only the immutable root/segments and the volatile <c>_resolvedSegments</c> entries, so it is
    /// safe either under the coordinator's lock (the seed and every event/retrack walk, sharing the
    /// coordinator's scratch buffer) or lock-free (<see cref="SubjectPathSubscription{TValue}.Current"/>
    /// with a rented buffer). The caller supplies the buffer.
    /// </summary>
    internal SubjectPathValue<TValue> Walk(IInterceptorSubject?[] buffer)
        => PathWalker.Walk(_segments, _root, buffer, _resolvedSegments);

    /// <summary>
    /// The first position where <paramref name="walkedSubjects"/> read on a different subject (reference
    /// identity) than the subscribed chain (<c>_resolvedSubjects</c>), or -1 when the chain is intact. This
    /// one comparison covers every structural case: a reassigned intermediate, a heal (null -> subject) and
    /// a break (subject -> null) each first differ at the affected position. Callers must hold the lock.
    /// </summary>
    internal int FindDivergence(IInterceptorSubject?[] walkedSubjects)
    {
        for (var position = 0; position < _segments.Length; position++)
        {
            if (!ReferenceEquals(walkedSubjects[position], _resolvedSubjects[position]))
            {
                return position;
            }
        }

        return -1;
    }

    /// <summary>
    /// True when <paramref name="cause"/> is a write to the chain's current resolved leaf (subject reference
    /// and name match), the condition that makes an intact-chain, resolved event a
    /// <see cref="SubjectPathChangeKind.ValueChange"/>. Callers must hold the lock.
    /// </summary>
    internal bool IsResolvedLeafWrite(in SubjectPropertyChange cause)
    {
        var leafSubject = _resolvedSubjects[_segments.Length - 1];
        var leafName = _segments[_segments.Length - 1].PropertyName;
        return leafSubject is not null
            && ReferenceEquals(cause.Property.Subject, leafSubject)
            && string.Equals(cause.Property.Name, leafName, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="observer"/> is still the current observer recorded for its position: a
    /// callback fired by a torn-down listener fails this check and is dropped rather than acting on a stale
    /// chain. Callers must hold the lock.
    /// </summary>
    internal bool IsCurrentObserver(PathSegmentObserver<TValue> observer)
        => _segmentObservers[observer.Position] == observer;

    /// <summary>
    /// Installs the subscribe-before-read chain from <paramref name="startPosition"/>, walking forward from
    /// <paramref name="startSubject"/>. For each segment the listener is installed BEFORE the read that
    /// resolves the next subject, so a write landing in that window is never missed. The build stops at the
    /// first unresolved intermediate, leaving the suffix (handles/observers/subjects past that position)
    /// null. Callers must hold the coordinator's lock.
    /// </summary>
    internal void BuildFrom(int startPosition, IInterceptorSubject startSubject)
    {
        var subject = startSubject;
        for (var position = startPosition; position < _segments.Length; position++)
        {
            var segment = _segments[position];

            // Record the current observer for this position (slot identity) and the subject this segment
            // reads on, then install the listener.
            var observer = new PathSegmentObserver<TValue>(_coordinator) { Position = position };
            _segmentObservers[position] = observer;
            _resolvedSubjects[position] = subject;

            // Subscribe FIRST, then resolve the next subject below: the install must precede the read.
            _segmentHandles[position] = PropertyChangeSubscription.Create(
                new PropertyReference(subject, segment.PropertyName), observer);

            var resolvedSegment = TryResolveSegment(subject, segment);
            Volatile.Write(ref _resolvedSegments[position], resolvedSegment);

            if (segment.IsLeaf)
            {
                // The leaf is subscribed but resolves no next subject.
                return;
            }

            IInterceptorSubject? child;
            try
            {
                child = resolvedSegment?.ResolveChild(subject, segment);
            }
            catch
            {
                child = null;
            }

            if (child is null)
            {
                // Unresolved intermediate: stop, leaving the suffix torn down (null).
                return;
            }

            subject = child;
        }
    }

    /// <summary>
    /// Resolves and caches the segment accessor by name against <paramref name="subject"/>. Name resolution
    /// and accessor construction are non-throwing: a missing or non-subscribable property returns null.
    /// </summary>
    private static ResolvedPathSegment<TValue>? TryResolveSegment(IInterceptorSubject subject, PathSegment segment)
    {
        if (!subject.Properties.TryGetValue(segment.PropertyName, out var metadata))
        {
            return null;
        }

        return ResolvedPathSegment<TValue>.TryCreate(subject, metadata, segment);
    }

    /// <summary>
    /// Tears down the chain from <paramref name="fromPosition"/> onward: disposes and clears each listener,
    /// its observer, and the subject it was read on. Entries the suffix owned become null so a later rebuild
    /// (or dispose) starts from a clean tail. Callers must hold the coordinator's lock.
    /// </summary>
    internal void DisposeSuffix(int fromPosition)
    {
        for (var position = fromPosition; position < _segments.Length; position++)
        {
            _segmentHandles[position]?.Dispose();
            _segmentHandles[position] = null;
            _segmentObservers[position] = null;
            _resolvedSubjects[position] = null;
            Volatile.Write(ref _resolvedSegments[position], null);
        }
    }

    /// <summary>Tears down the entire chain (equivalent to <c>DisposeSuffix(0)</c>). Callers must hold the lock.</summary>
    internal void DisposeAll() => DisposeSuffix(0);
}
