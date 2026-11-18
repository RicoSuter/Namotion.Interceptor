using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Derived;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Work that arises on a thread while it holds a lifecycle lock: derived recalculation requests and change
/// deliveries. It runs outside every lifecycle lock, when the operation that took the thread's first lifecycle lock
/// completes: after the write whose reconciliation took it (the anchor write) has been announced, or when a context
/// attach, a context detach or <c>TryRunWhileAttached</c> releases it.
/// </summary>
/// <remarks>
/// Anchors nest, so the requests are a stack of segments, one per anchor that has queued work; an anchor drains only
/// its own segment's requests. The deliveries of a thread are one list in write order: while a queued delivery is
/// pending, a delivery made without a lifecycle lock is queued behind it. A drain runs the pending deliveries until
/// none is left, then the requests of its segment, each property once. See docs/design/tracking-lifecycle.md
/// (Post-Lock Queue).
/// </remarks>
internal static class PostLockQueue
{
    // A segment that queued more than this is not kept for reuse, so a thread does not keep large buffers alive.
    private const int MaxPooledCapacity = 64;

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

    // The queued deliveries of all segments, in write order. A null entry is a slot reserved for a delivery that was
    // not filled (the write did not land, or had no consumers), or one that already ran; the drain skips it.
    [ThreadStatic]
    private static List<Action?>? _deliveries;

    // The index of the next delivery to run.
    [ThreadStatic]
    private static int _nextDelivery;

    // The held lifecycle locks plus the pending deliveries, so a write reads one count to learn whether its delivery
    // may be queued. A delivery is pending from the write that queued it until it ran and returned.
    [ThreadStatic]
    private static int _deferrals;

    // The delivery loops running on the thread: a drain that a delivery causes runs inside the loop of another.
    [ThreadStatic]
    private static int _deliveryLoops;

    /// <summary>The queued work of one anchor.</summary>
    internal sealed class Segment
    {
        /// <summary>The write depth of the anchor write, or 0 for a segment that drains at a lock release.</summary>
        public int AnchorDepth;

        /// <summary>The enclosing segment on the stack, or the next segment in the pool.</summary>
        public Segment? Next;

        public readonly List<QueuedRequest> Requests = [];

        /// <summary>The index of each queued property's entry in <see cref="Requests"/>.</summary>
        public readonly Dictionary<PropertyReference, int> RequestIndex = new(PropertyReference.Comparer);
    }

    /// <summary>
    /// A queued recalculation request. A <see cref="RawTimestamp"/> of 0 takes the anchor write's timestamp at the
    /// drain, or the time of the drain at a release anchor; otherwise it is the request's own (handed over with it, or
    /// the causing write's at a release anchor), and the newer of it and the anchor write's wins. The
    /// <see cref="AttachCount"/> of the property when the request was queued, or when an attach handed it over, drops
    /// the request at the drain when the property was attached again since: that attach evaluated after the request's
    /// cause.
    /// </summary>
    internal readonly record struct QueuedRequest(PropertyReference Property, long RawTimestamp, long AttachCount);

    /// <summary>
    /// Whether a change delivery made now is queued: the thread holds a lifecycle lock, or a queued delivery is
    /// pending, which the change must not overtake.
    /// </summary>
    internal static bool IsDeliveryDeferred
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _deferrals != 0;
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
        _deferrals++;
        if (_heldLocks++ != 0)
        {
            return;
        }

        _lockAnchorDepth = isWriteReconciliation ? PropertyAccessObservation.WriteDepth : 0;
    }

    /// <summary>
    /// Called by a lifecycle interceptor after it released its lock. The release of the thread's last lifecycle lock
    /// drains the segment that has no anchor write; a segment of an anchor write waits for that write to complete.
    /// Exceptions of the drained work are traced.
    /// </summary>
    internal static void ExitLock()
    {
        _deferrals--;
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
    /// <param name="attachCount">
    /// The attach count the request answers for, or <see cref="DerivedPropertyOwnership.AnyAttachCount"/> to take the
    /// property's when it is queued.
    /// </param>
    internal static bool TryEnqueueRequest(PropertyReference property, long handedOverRawTimestamp, long attachCount)
    {
        if (TryGetOpenSegment() is not { } segment)
        {
            return false;
        }

        AddRequest(segment, property, handedOverRawTimestamp, attachCount);
        return true;
    }

    /// <summary>
    /// The delivery slot of a change without a reserved slot: under a lifecycle lock, or while a queued delivery is
    /// pending, it is queued after the deliveries queued so far.
    /// </summary>
    internal const int UnreservedDelivery = -1;

    /// <summary>
    /// Reserves the place of a delivery in write order before the write's chain runs, for a structural write that the
    /// thread makes while <see cref="IsDeliveryDeferred"/>, so it is delivered before the writes that the handlers of
    /// its reconciliation make. Returns the slot to pass to <see cref="IsDeliveryQueued"/> and
    /// <see cref="EnqueueDelivery"/>.
    /// </summary>
    internal static int ReserveDelivery()
    {
        return Append(null);
    }

    /// <summary>
    /// Whether the delivery of a write that completed its chain is queued instead of dispatched now: it has a reserved
    /// slot that has not run yet, the thread holds a lifecycle lock, or a queued delivery is pending. The anchor write
    /// of the innermost segment is delivered at once, before the work queued under its lock, unless it reserved a slot
    /// behind older deliveries.
    /// </summary>
    /// <remarks>
    /// A structural write whose consumers were installed while it ran reserved no slot, so as an anchor it is
    /// delivered at once, possibly before older pending deliveries.
    /// </remarks>
    internal static bool IsDeliveryQueued(int slot)
    {
        if (slot != UnreservedDelivery && slot >= _nextDelivery)
        {
            return true;
        }

        // Without a held lock, the deferral count is the number of pending deliveries.
        return _heldLocks != 0 ||
               (_deferrals != 0 && !IsAnchorWrite(PropertyAccessObservation.WriteDepth));
    }

    /// <summary>
    /// Queues a change delivery for which <see cref="IsDeliveryQueued"/> returned true: in the slot reserved for it,
    /// or after the deliveries queued so far.
    /// </summary>
    internal static void EnqueueDelivery(int slot, Action delivery)
    {
        if (slot != UnreservedDelivery && slot >= _nextDelivery)
        {
            _deliveries![slot] = delivery;
            return;
        }

        Append(delivery);
    }

    private static int Append(Action? delivery)
    {
        // Under a lifecycle lock, the segment that the anchor of the lock drains must exist. Without one, a pending
        // delivery exists, which a segment on the stack or a running delivery loop runs.
        if (_heldLocks != 0)
        {
            TryGetOpenSegment();
        }

        Debug.Assert(_innermost is not null || _deliveryLoops != 0, "A queued delivery has a segment or a delivery loop that runs it.");
        var deliveries = _deliveries ??= [];
        deliveries.Add(delivery);
        _deferrals++;
        return deliveries.Count - 1;
    }

    /// <summary>
    /// Adds a request to a segment; a property already queued in it keeps one entry with the newer timestamp and the
    /// newer attach count. A request raised by a write takes the property's attach count now, after the write's W4
    /// ("The Version Protocol" in docs/design/tracking-derived-properties.md).
    /// </summary>
    internal static void AddRequest(
        Segment segment, PropertyReference property, long rawTimestamp, long attachCount = DerivedPropertyOwnership.AnyAttachCount)
    {
        if (attachCount == DerivedPropertyOwnership.AnyAttachCount)
        {
            attachCount = property.TryGetDerivedPropertyData() is { } data ? Volatile.Read(ref data.AttachCount) : 0;
        }

        var requests = segment.Requests;
        ref var index = ref CollectionsMarshal.GetValueRefOrAddDefault(segment.RequestIndex, property, out var exists);
        if (exists)
        {
            var queued = requests[index];
            requests[index] = queued with
            {
                RawTimestamp = Newest(queued.RawTimestamp, rawTimestamp),
                AttachCount = Math.Max(queued.AttachCount, attachCount)
            };
            return;
        }

        index = requests.Count;
        requests.Add(new QueuedRequest(property, rawTimestamp, attachCount));
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
    /// Runs the pending deliveries, also those queued while they run, then the recalculations of a segment that was
    /// removed from the stack, so a request that arises meanwhile runs immediately or goes to a segment of its own.
    /// Every entry runs, also after one threw. The drained work is not a dependency of a getter that made the anchor
    /// write: the recording is suspended.
    /// </summary>
    private static void Drain(Segment segment, long rawTimestamp, bool rethrow)
    {
        ExceptionDispatchInfo? firstException = null;
        var isRecordingSuspended = DerivedPropertyRecorder.TrySuspend();
        try
        {
            RunDeliveries(ref firstException);
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

        DerivedPropertyOwnership.ThrowOrTrace(firstException, rethrow, "queued work threw after a lifecycle operation");
    }

    // The pending deliveries include those that an enclosing segment queued before this segment's, which are older.
    // A drain that a delivery causes runs inside this loop and advances the shared index.
    private static void RunDeliveries(ref ExceptionDispatchInfo? firstException)
    {
        if (_deliveries is not { } deliveries)
        {
            return;
        }

        _deliveryLoops++;
        try
        {
            while (_nextDelivery < deliveries.Count)
            {
                var index = _nextDelivery++;
                var delivery = deliveries[index];
                deliveries[index] = null;
                try
                {
                    delivery?.Invoke();
                }
                catch (Exception exception)
                {
                    firstException ??= ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    // The delivery is pending until it returns, so a write that one of its observers makes is queued
                    // behind it, also when it is the last one.
                    _deferrals--;
                }
            }
        }
        finally
        {
            // A reserved slot belongs to a write that a segment on the stack or a running loop encloses, so the list
            // is reset only when neither is left.
            if (--_deliveryLoops == 0 && _innermost is null)
            {
                Debug.Assert(_deferrals == _heldLocks, "No delivery is pending when the list is reset.");
                _nextDelivery = 0;
                if (deliveries.Capacity > MaxPooledCapacity)
                {
                    _deliveries = null;
                }
                else
                {
                    deliveries.Clear();
                }
            }
        }
    }

    // A request takes the newer of its own timestamp and the anchor write's. At a release anchor, or for an anchor
    // write that did not land, rawTimestamp is 0: a request keeps its own, and the requests without one share the
    // time of the drain, resolved at the first of them.
    private static void RunRequests(List<QueuedRequest> requests, long rawTimestamp, ref ExceptionDispatchInfo? firstException)
    {
        long drainTimestamp = 0;
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            try
            {
                var timestamp = Newest(request.RawTimestamp, rawTimestamp);
                DerivedPropertyOwnership.Request(
                    request.Property,
                    timestamp != 0 ? timestamp : TrackingWriteObserver.ResolveTimestamp(ref drainTimestamp),
                    request.AttachCount);
            }
            catch (Exception exception)
            {
                firstException ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
    }

    private static void Return(Segment segment)
    {
        if (segment.Requests.Capacity > MaxPooledCapacity)
        {
            return;
        }

        segment.Requests.Clear();
        segment.RequestIndex.Clear();
        segment.Next = _pool;
        _pool = segment;
    }

    /// <summary>
    /// The newer of two raw timestamps, which are ticks, negated for an explicit null scope; 0 means none.
    /// </summary>
    internal static long Newest(long left, long right)
    {
        if (left == 0)
        {
            return right;
        }

        return right == 0 || Math.Abs(left) >= Math.Abs(right) ? left : right;
    }
}
