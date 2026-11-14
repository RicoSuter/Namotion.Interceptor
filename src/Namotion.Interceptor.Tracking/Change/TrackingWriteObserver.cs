using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Runs once per write after the whole interceptor chain: requests the recalculation of every derived
/// property that depends on the written property. A request raised while the thread holds a lifecycle lock is queued
/// (<see cref="PostLockQueue"/>), and the anchor write of queued work drains it here.
/// </summary>
internal sealed class TrackingWriteObserver : IPropertyWriteObserver
{
    internal static readonly TrackingWriteObserver Instance = new();

    // The data of the derived property whose recalculation this thread publishes, so its publication finds the data
    // without a lookup.
    [ThreadStatic]
    private static DerivedPropertyData? _publication;

    internal static void EnsureRegistered() => PropertyAccessObservation.SetWriteObserver(Instance);

    /// <summary>
    /// Marks <paramref name="data"/> as published on this thread until <see cref="ExitPublication"/>, which takes the
    /// returned value.
    /// </summary>
    internal static DerivedPropertyData? EnterPublication(DerivedPropertyData data)
    {
        var previous = _publication;
        _publication = data;
        return previous;
    }

    internal static void ExitPublication(DerivedPropertyData? previous) => _publication = previous;

    public void OnPropertyWritten(in PropertyWriteObservation write)
    {
        if (PostLockQueue.IsAnchorWrite(write.Depth))
        {
            CompleteAnchorWrite(write);
            return;
        }

        var data = TryGetWrittenData(write);
        if (data is not null)
        {
            RequestRecalculations(write, data, segment: null);
        }
    }

    // The anchor write was announced in its chain; its own requests join the work queued under the lock, which runs
    // now: the queued deliveries first, then the recalculations.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CompleteAnchorWrite(in PropertyWriteObservation write)
    {
        var segment = PostLockQueue.Pop();
        var data = TryGetWrittenData(write);
        if (data is not null)
        {
            RequestRecalculations(write, data, segment);
        }

        PostLockQueue.CompleteAnchorWrite(segment, write.RawTimestamp, write.HasFailed);
    }

    /// <summary>
    /// W2 to W5 for a change of the structure of <paramref name="subject"/> (<see cref="SubjectStructure"/>). Called
    /// after the structure changed.
    /// </summary>
    internal static void OnStructureChanged(IInterceptorSubject subject) => OnCellChanged(SubjectStructure.Of(subject));

    /// <summary>
    /// W2 to W5 for a change of the children of <paramref name="subject"/> (<see cref="SubjectStructure"/>). Called
    /// after a subject was attached or detached through one of its properties, or moved within one.
    /// </summary>
    internal static void OnChildrenChanged(IInterceptorSubject subject) => OnCellChanged(SubjectStructure.ChildrenOf(subject));

    private static void OnCellChanged(PropertyReference cell)
    {
        // W2: the structure changed before its data is looked up.
        Interlocked.MemoryBarrier();
        var data = cell.TryGetDerivedPropertyData();
        if (data is not null)
        {
            RequestRecalculations(
                new PropertyWriteObservation(cell, isWritten: true, rawTimestamp: 0, isDerivedRecalculation: false, depth: 0, hasFailed: false),
                data, segment: null);
        }
    }

    private static DerivedPropertyData? TryGetWrittenData(in PropertyWriteObservation write)
    {
        if (!write.IsWritten)
        {
            return null;
        }

        // W2: the value landed before the gate and the data are read, so an evaluation that creates the data
        // later reads this value.
        Interlocked.MemoryBarrier();
        var property = write.Property;

        // The gate is raised before the first data of the subject's type is stored: not raised means no data.
        if (!DerivedPropertyTypeGate.Of(property.Subject).IsRaised)
        {
            return null;
        }

        return write.IsDerivedRecalculation && _publication is { } publication && publication.Property == property
            ? publication
            : property.TryGetDerivedPropertyData();
    }

    private static void RequestRecalculations(in PropertyWriteObservation write, DerivedPropertyData data, PostLockQueue.Segment? segment)
    {
        var property = write.Property;

        // W4, then W5: the used-by set is read after the version moved.
        var newVersion = Interlocked.Increment(ref data.Version);
        if (DerivedPropertyRecorder.IsEvaluatingOnThisThread)
        {
            // A getter that writes what it read (or a write it triggers on this thread) does not fail the version check.
            DerivedPropertyRecorder.Current.OnOwnWrite(property, newVersion);
        }

        var isSettableDerived = !write.IsDerivedRecalculation &&
                                Volatile.Read(ref data.IsDerived) &&
                                property.Metadata.SetValue is not null;
        var dependents = data.GetUsedByProperties();
        if (!isSettableDerived && dependents.IsEmpty)
        {
            return;
        }

        // A write under a lifecycle lock queues its requests. Under an anchor write they take that write's timestamp at
        // the drain; at a release anchor, which has no write, they keep this write's.
        segment ??= PostLockQueue.TryGetOpenSegment();
        if (segment is not null)
        {
            Enqueue(segment, property, segment.AnchorDepth == 0 ? write.RawTimestamp : 0, isSettableDerived, dependents);
            return;
        }

        RequestNow(write, isSettableDerived, dependents);
    }

    private static void RequestNow(in PropertyWriteObservation write, bool isSettableDerived, ReadOnlySpan<PropertyReference> dependents)
    {
        // Every dependent is requested, also after one threw (an observer of its change). A request that throws ends
        // this loop and RequestAfterException requests the remaining dependents, so the loop runs without a handler
        // per request.
        var property = write.Property;
        var rawTimestamp = write.RawTimestamp;
        var next = 0;
        try
        {
            if (isSettableDerived)
            {
                DerivedPropertyOwnership.RequestRecalculation(property, ResolveTimestamp(ref rawTimestamp));
            }

            while (next < dependents.Length)
            {
                var dependent = dependents[next++];
                if (dependent != property)
                {
                    DerivedPropertyOwnership.RequestRecalculation(dependent, ResolveTimestamp(ref rawTimestamp));
                }
            }
        }
        catch (Exception exception)
        {
            RequestAfterException(write, dependents[next..], rawTimestamp, exception);
        }
    }

    private static void Enqueue(
        PostLockQueue.Segment segment, PropertyReference property, long rawTimestamp, bool isSettableDerived,
        ReadOnlySpan<PropertyReference> dependents)
    {
        if (isSettableDerived)
        {
            PostLockQueue.AddRequest(segment, property, rawTimestamp);
        }

        foreach (var dependent in dependents)
        {
            if (dependent != property)
            {
                PostLockQueue.AddRequest(segment, dependent, rawTimestamp);
            }
        }
    }

    // The first exception is rethrown after the remaining dependents were requested, unless the write itself failed:
    // its own exception is in flight and wins, and the dependent's exception is traced.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RequestAfterException(
        in PropertyWriteObservation write, ReadOnlySpan<PropertyReference> remaining, long rawTimestamp, Exception exception)
    {
        var property = write.Property;
        var firstException = ExceptionDispatchInfo.Capture(exception);
        foreach (var dependent in remaining)
        {
            if (dependent != property)
            {
                try
                {
                    DerivedPropertyOwnership.RequestRecalculation(dependent, ResolveTimestamp(ref rawTimestamp));
                }
                catch (Exception)
                {
                    // Only the first exception is rethrown or traced.
                }
            }
        }

        DerivedPropertyOwnership.ThrowOrTrace(firstException, rethrow: !write.HasFailed, "a dependent of a failed write threw");
    }

    // A write without a context has no timestamp: resolved at its first request, so every dependent shares it. A
    // time source that throws leaves 0, which the next request resolves again.
    internal static long ResolveTimestamp(ref long rawTimestamp)
    {
        if (rawTimestamp == 0)
        {
            rawTimestamp = SubjectChangeContext.Current.ResolveChangedTimestamp();
        }

        return rawTimestamp;
    }
}
