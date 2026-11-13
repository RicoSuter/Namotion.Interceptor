using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Work that arises on a thread while it holds a lifecycle lock: derived recalculation requests and change
/// deliveries. It runs outside every lifecycle lock, when the operation that took the thread's first lifecycle lock
/// completes: after the write whose reconciliation took it (the anchor write) has been announced, or when a context
/// attach, a context detach or <c>TryRunWhileAttached</c> releases it.
/// </summary>
/// <remarks>
/// Anchors nest, so the queue is a stack of segments, one per anchor that has queued work; an anchor drains only
/// its own segment. A segment drains its deliveries in arrival order first, then its recalculation requests, each
/// property once. See docs/design/tracking-lifecycle.md (Post-Lock Queue).
/// </remarks>
internal static class PostLockQueue
{
    // A segment that queued more than this is not kept for reuse, so a thread does not keep large buffers alive.
    private const int MaxPooledCapacity = 64;

    // Above this many requests, a segment finds a queued property through a dictionary instead of a linear search.
    private const int LinearSearchLimit = 8;

    [ThreadStatic]
    private static int _heldLocks;

    // The write depth of the anchor write of the locks held now; 0 when a context attach, a context detach or
    // TryRunWhileAttached took the first lock, which then drains at its release.
    [ThreadStatic]
    private static int _lockAnchorDepth;

    // The anchor depth of the innermost segment; 0 when there is none or it drains at a lock release. Read on every
    // write, so it is a plain thread-static value.
    [ThreadStatic]
    private static int _innermostAnchorDepth;

    [ThreadStatic]
    private static List<Segment>? _segments;

    [ThreadStatic]
    private static Stack<Segment>? _pool;

    /// <summary>The queued work of one anchor.</summary>
    internal sealed class Segment
    {
        /// <summary>The write depth of the anchor write, or 0 for a segment that drains at a lock release.</summary>
        public int AnchorDepth;

        public readonly List<Action> Deliveries = [];

        public readonly List<QueuedRequest> Requests = [];

        public Dictionary<PropertyReference, int>? RequestIndex;
    }

    /// <summary>
    /// A queued recalculation request. A <see cref="RawTimestamp"/> of 0 takes the anchor's timestamp at the drain;
    /// otherwise it is a timestamp handed over with the request, and the newer of the two wins.
    /// </summary>
    internal readonly record struct QueuedRequest(PropertyReference Property, long RawTimestamp);

    /// <summary>Whether the thread holds a lifecycle lock.</summary>
    internal static bool IsLockHeld
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _heldLocks > 0;
    }

    /// <summary>
    /// The write depth of the anchor write whose segment is the innermost one, 0 when there is none. The write
    /// observer completes the anchor write of this depth.
    /// </summary>
    internal static int InnermostAnchorDepth
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _innermostAnchorDepth;
    }

    /// <summary>
    /// Called by a lifecycle interceptor before it takes its lock. When the thread holds no lifecycle lock yet, a
    /// write reconciliation makes the write the anchor; otherwise the release of this lock is the anchor.
    /// </summary>
    internal static void EnterLock(bool isWriteReconciliation)
    {
        if (_heldLocks++ != 0)
        {
            return;
        }

        // The anchor write is completed by the write observer of Tracking, so another observer installed in its
        // place leaves the release of the lock as the anchor.
        _lockAnchorDepth = isWriteReconciliation &&
                           ReferenceEquals(PropertyAccessObservation.WriteObserver, TrackingWriteObserver.Instance)
            ? PropertyAccessObservation.WriteDepth
            : 0;
    }

    /// <summary>
    /// Called by a lifecycle interceptor after it released its lock. The release of the thread's last lifecycle lock
    /// drains the segment that has no anchor write; a segment of an anchor write waits for that write to complete.
    /// Exceptions of the drained work are traced.
    /// </summary>
    internal static void ExitLock()
    {
        if (--_heldLocks != 0)
        {
            return;
        }

        if (_segments is { Count: > 0 } segments && segments[^1].AnchorDepth == 0)
        {
            Drain(Pop(segments), rawTimestamp: 0, rethrow: false);
        }
    }

    /// <summary>
    /// The segment that takes the work queued now, or null when the thread holds no lifecycle lock and the work runs
    /// immediately. A second lifecycle interceptor that reconciles the same write (aggregated contexts) shares the
    /// segment of the first.
    /// </summary>
    internal static Segment? TryGetOpenSegment()
    {
        if (_heldLocks == 0)
        {
            return null;
        }

        // A segment at the anchor depth of the locks held now belongs to them: a segment of another anchor at that
        // depth has completed and was removed, since anchors at one depth do not nest.
        var segments = _segments ??= [];
        if (segments.Count > 0 && segments[^1].AnchorDepth == _lockAnchorDepth)
        {
            return segments[^1];
        }

        var segment = _pool is { Count: > 0 } pool ? pool.Pop() : new Segment();
        segment.AnchorDepth = _lockAnchorDepth;
        segments.Add(segment);
        _innermostAnchorDepth = _lockAnchorDepth;
        return segment;
    }

    /// <summary>Queues a recalculation request when the thread holds a lifecycle lock.</summary>
    /// <param name="property">The derived property.</param>
    /// <param name="handedOverRawTimestamp">The timestamp that comes with the request, 0 to take the anchor's.</param>
    internal static bool TryEnqueueRequest(PropertyReference property, long handedOverRawTimestamp)
    {
        if (_heldLocks == 0)
        {
            return false;
        }

        AddRequest(TryGetOpenSegment()!, property, handedOverRawTimestamp);
        return true;
    }

    /// <summary>Queues a change delivery when the thread holds a lifecycle lock.</summary>
    internal static bool TryEnqueueDelivery(Action delivery)
    {
        if (_heldLocks == 0)
        {
            return false;
        }

        TryGetOpenSegment()!.Deliveries.Add(delivery);
        return true;
    }

    /// <summary>Adds a request to a segment; a property already queued in it keeps one entry with the newer timestamp.</summary>
    internal static void AddRequest(Segment segment, PropertyReference property, long rawTimestamp)
    {
        var requests = segment.Requests;
        var index = IndexOf(segment, property);
        if (index >= 0)
        {
            requests[index] = requests[index] with { RawTimestamp = Newest(requests[index].RawTimestamp, rawTimestamp) };
            return;
        }

        segment.RequestIndex?.Add(property, requests.Count);
        requests.Add(new QueuedRequest(property, rawTimestamp));
        if (segment.RequestIndex is null && requests.Count > LinearSearchLimit)
        {
            var requestIndex = new Dictionary<PropertyReference, int>(requests.Count * 2, PropertyReference.Comparer);
            for (var i = 0; i < requests.Count; i++)
            {
                requestIndex.Add(requests[i].Property, i);
            }

            segment.RequestIndex = requestIndex;
        }
    }

    private static int IndexOf(Segment segment, in PropertyReference property)
    {
        if (segment.RequestIndex is { } requestIndex)
        {
            return requestIndex.GetValueOrDefault(property, -1);
        }

        var requests = segment.Requests;
        for (var i = 0; i < requests.Count; i++)
        {
            if (requests[i].Property == property)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Removes the segment of the anchor write at <see cref="InnermostAnchorDepth"/>, which the write observer
    /// completes: it adds the write's own recalculation requests and then calls <see cref="CompleteAnchorWrite"/>.
    /// </summary>
    internal static Segment TakeAnchorSegment()
    {
        var segments = _segments;
        if (segments is not { Count: > 0 } || segments[^1].AnchorDepth != _innermostAnchorDepth)
        {
            throw new InvalidOperationException(
                "PostLockQueue: the innermost segment does not belong to the completed anchor write.");
        }

        return Pop(segments);
    }

    /// <summary>
    /// Drains the segment of a completed anchor write. The first exception of the drained work is rethrown after
    /// every entry ran, unless the write itself failed: its own exception is in flight and wins, and the drained
    /// work's exception is traced.
    /// </summary>
    internal static void CompleteAnchorWrite(Segment segment, long rawTimestamp, bool hasWriteFailed)
    {
        if (_heldLocks > 0)
        {
            // Not reached by the lifecycle, whose anchor writes complete after its locks are released; kept so
            // that queued work is never lost: the enclosing segment takes it over.
            MoveToOpenSegment(segment);
            return;
        }

        Drain(segment, rawTimestamp, rethrow: !hasWriteFailed);
    }

    private static void MoveToOpenSegment(Segment segment)
    {
        var target = TryGetOpenSegment()!;
        target.Deliveries.AddRange(segment.Deliveries);
        foreach (var request in segment.Requests)
        {
            AddRequest(target, request.Property, request.RawTimestamp);
        }

        Return(segment);
    }

    private static Segment Pop(List<Segment> segments)
    {
        var segment = segments[^1];
        segments.RemoveAt(segments.Count - 1);
        _innermostAnchorDepth = segments.Count > 0 ? segments[^1].AnchorDepth : 0;
        return segment;
    }

    /// <summary>
    /// Runs the deliveries, then the recalculations, of a segment that was removed from the stack, so work that
    /// arises meanwhile runs immediately or goes to a segment of its own. Every entry runs, also after one threw.
    /// The drained work is not a dependency of a getter that made the anchor write: the recording is suspended.
    /// </summary>
    private static void Drain(Segment segment, long rawTimestamp, bool rethrow)
    {
        ExceptionDispatchInfo? firstException = null;
        var isRecordingSuspended = DerivedPropertyRecorder.TrySuspend();
        try
        {
            RunDeliveries(segment.Deliveries, ref firstException);
            RunRequests(segment.Requests, rawTimestamp, ref firstException);
        }
        finally
        {
            if (isRecordingSuspended)
            {
                DerivedPropertyRecorder.Resume();
            }

            Return(segment);
        }

        if (firstException is null)
        {
            return;
        }

        if (rethrow)
        {
            firstException.Throw();
        }

        DerivedPropertyOwnership.TraceDropped("queued work threw after a lifecycle operation", firstException.SourceException);
    }

    private static void RunDeliveries(List<Action> deliveries, ref ExceptionDispatchInfo? firstException)
    {
        for (var i = 0; i < deliveries.Count; i++)
        {
            try
            {
                deliveries[i]();
            }
            catch (Exception exception)
            {
                firstException ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
    }

    private static void RunRequests(List<QueuedRequest> requests, long rawTimestamp, ref ExceptionDispatchInfo? firstException)
    {
        if (requests.Count == 0)
        {
            return;
        }

        var anchorTimestamp = ResolveAnchorTimestamp(rawTimestamp, ref firstException);
        for (var i = 0; i < requests.Count; i++)
        {
            try
            {
                DerivedPropertyOwnership.RequestRecalculation(
                    requests[i].Property, Newest(requests[i].RawTimestamp, anchorTimestamp));
            }
            catch (Exception exception)
            {
                firstException ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
    }

    // The anchor write's timestamp; at a lock release, or for an anchor write that did not land, the current one. A
    // time source that throws leaves 0, which each request resolves again.
    private static long ResolveAnchorTimestamp(long rawTimestamp, ref ExceptionDispatchInfo? firstException)
    {
        if (rawTimestamp != 0)
        {
            return rawTimestamp;
        }

        try
        {
            return SubjectChangeContext.Current.ResolveChangedTimestamp();
        }
        catch (Exception exception)
        {
            firstException ??= ExceptionDispatchInfo.Capture(exception);
            return 0;
        }
    }

    private static void Return(Segment segment)
    {
        if (segment.Deliveries.Capacity > MaxPooledCapacity || segment.Requests.Capacity > MaxPooledCapacity)
        {
            return;
        }

        segment.Deliveries.Clear();
        segment.Requests.Clear();
        segment.RequestIndex = null;
        (_pool ??= new Stack<Segment>()).Push(segment);
    }

    // Raw timestamps are ticks, negated for an explicit null scope; 0 means none.
    private static long Newest(long left, long right)
    {
        if (left == 0)
        {
            return right;
        }

        return right == 0 || Math.Abs(left) >= Math.Abs(right) ? left : right;
    }
}
