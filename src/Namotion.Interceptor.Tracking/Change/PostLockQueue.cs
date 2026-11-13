using System.Diagnostics;
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

    // The innermost segment of the stack, read on every write; the others follow through Segment.Next.
    [ThreadStatic]
    private static Segment? _innermost;

    // The segments kept for reuse, linked through Segment.Next.
    [ThreadStatic]
    private static Segment? _pool;

    /// <summary>The queued work of one anchor.</summary>
    internal sealed class Segment
    {
        /// <summary>The write depth of the anchor write, or 0 for a segment that drains at a lock release.</summary>
        public int AnchorDepth;

        /// <summary>The enclosing segment on the stack, or the next segment in the pool.</summary>
        public Segment? Next;

        // A null entry is a slot reserved for a delivery that was not filled (the write did not land, or had no
        // consumers); the drain skips it.
        public readonly List<Action?> Deliveries = [];

        public readonly List<QueuedRequest> Requests = [];

        public Dictionary<PropertyReference, int>? RequestIndex;
    }

    /// <summary>
    /// A queued recalculation request. A <see cref="RawTimestamp"/> of 0 takes the anchor write's timestamp at the
    /// drain, or the time of the drain at a release anchor; otherwise it is the request's own (handed over with it, or
    /// the causing write's at a release anchor), and the newer of it and the anchor write's wins. The
    /// <see cref="AttachCount"/> of the property when it was queued drops the request at the drain when the property
    /// was attached again since: that attach evaluated after the request's cause.
    /// </summary>
    internal readonly record struct QueuedRequest(PropertyReference Property, long RawTimestamp, long AttachCount);

    /// <summary>Whether the thread holds a lifecycle lock.</summary>
    internal static bool IsLockHeld
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _heldLocks > 0;
    }

    /// <summary>
    /// Whether the write of this depth is the anchor write of the innermost segment, which the write observer
    /// completes. Write depths start at 1, so no write matches a segment that drains at a lock release.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsAnchorWrite(int depth) => _innermost is { } innermost && innermost.AnchorDepth == depth;

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
        if (--_heldLocks == 0 && _innermost is { AnchorDepth: 0 })
        {
            Drain(Pop(), rawTimestamp: 0, rethrow: false);
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
        if (_innermost is { } innermost && innermost.AnchorDepth == _lockAnchorDepth)
        {
            return innermost;
        }

        var segment = _pool ?? new Segment();
        _pool = segment.Next;
        segment.AnchorDepth = _lockAnchorDepth;
        segment.Next = _innermost;
        _innermost = segment;
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

    /// <summary>The delivery slot of a change written while the thread holds no lifecycle lock: it is delivered at once.</summary>
    internal const int ImmediateDelivery = -2;

    /// <summary>The delivery slot of a change written under a lifecycle lock without a reserved slot.</summary>
    internal const int UnreservedDelivery = -1;

    /// <summary>The delivery slot of a change that reserved none: queued when the thread holds a lifecycle lock.</summary>
    internal static int DeliverySlot => _heldLocks == 0 ? ImmediateDelivery : UnreservedDelivery;

    /// <summary>
    /// Reserves the place of a delivery in arrival order, for a write that the thread makes while it holds a lifecycle
    /// lock, before the write's chain runs. Returns the slot to pass to <see cref="EnqueueDelivery"/>.
    /// </summary>
    internal static int ReserveDelivery()
    {
        var deliveries = TryGetOpenSegment()!.Deliveries;
        deliveries.Add(null);
        return deliveries.Count - 1;
    }

    /// <summary>
    /// Queues a change delivery while the thread holds a lifecycle lock: in the slot reserved for it, or, for
    /// <see cref="UnreservedDelivery"/>, after the deliveries queued so far.
    /// </summary>
    internal static void EnqueueDelivery(int slot, Action delivery)
    {
        var deliveries = TryGetOpenSegment()!.Deliveries;
        if (slot == UnreservedDelivery)
        {
            deliveries.Add(delivery);
            return;
        }

        deliveries[slot] = delivery;
    }

    /// <summary>
    /// Adds a request to a segment; a property already queued in it keeps one entry with the newer timestamp and the
    /// newer attach count.
    /// </summary>
    internal static void AddRequest(Segment segment, PropertyReference property, long rawTimestamp)
    {
        var attachCount = property.TryGetDerivedPropertyData() is { } data ? Volatile.Read(ref data.AttachCount) : 0;
        var requests = segment.Requests;
        var index = IndexOf(segment, property);
        if (index >= 0)
        {
            var queued = requests[index];
            requests[index] = queued with
            {
                RawTimestamp = Newest(queued.RawTimestamp, rawTimestamp),
                AttachCount = Math.Max(queued.AttachCount, attachCount)
            };
            return;
        }

        segment.RequestIndex?.Add(property, requests.Count);
        requests.Add(new QueuedRequest(property, rawTimestamp, attachCount));
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
    /// Removes the innermost segment, which belongs to an anchor write (<see cref="IsAnchorWrite"/>) or a lock release.
    /// The write observer completes an anchor write: it adds the write's own recalculation requests and then calls
    /// <see cref="CompleteAnchorWrite"/>.
    /// </summary>
    internal static Segment Pop()
    {
        var segment = _innermost!;
        _innermost = segment.Next;
        segment.Next = null;
        return segment;
    }

    /// <summary>
    /// Drains the segment of a completed anchor write. The first exception of the drained work is rethrown after
    /// every entry ran, unless the write itself failed: its own exception is in flight and wins, and the drained
    /// work's exception is traced.
    /// </summary>
    internal static void CompleteAnchorWrite(Segment segment, long rawTimestamp, bool hasWriteFailed)
    {
        // An anchor write took the thread's first lifecycle lock, and every lock taken in its chain is released
        // before the write observer runs.
        Debug.Assert(_heldLocks == 0, "An anchor write completes outside every lifecycle lock.");
        Drain(segment, rawTimestamp, rethrow: !hasWriteFailed);
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

    private static void RunDeliveries(List<Action?> deliveries, ref ExceptionDispatchInfo? firstException)
    {
        for (var i = 0; i < deliveries.Count; i++)
        {
            try
            {
                deliveries[i]?.Invoke();
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
                var request = requests[i];
                DerivedPropertyOwnership.RunQueuedRequest(
                    request.Property, TimestampOf(request, rawTimestamp, anchorTimestamp), request.AttachCount);
            }
            catch (Exception exception)
            {
                firstException ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
    }

    // Under an anchor write, the newer of the request's and the write's timestamp. At a release anchor, or for an
    // anchor write that did not land, the request's own, or the time of the drain for a request without one.
    private static long TimestampOf(in QueuedRequest request, long rawTimestamp, long anchorTimestamp)
    {
        return rawTimestamp == 0 && request.RawTimestamp != 0 ? request.RawTimestamp : Newest(request.RawTimestamp, anchorTimestamp);
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
        segment.Next = _pool;
        _pool = segment;
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
