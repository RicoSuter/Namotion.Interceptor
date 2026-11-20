using System.ComponentModel;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Derived;

/// <summary>
/// Reads and changes of a subject's graph state and children for derived property tracking. The graph state is the
/// subject's lifecycle and registry state: whether it is attached and registered, its reference count, its parents and
/// its registered properties. Its children are the subjects attached through its properties. Infrastructure for the
/// lifecycle and the registry.
/// </summary>
/// <remarks>See "Graph Dependencies" in docs/design/tracking-derived-properties.md.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class DerivedGraphDependencies
{
    // 0 while no graph dependency was ever created, 1 while the first creation raises the gate, 2 once it is marked raised.
    // A change counts 1 as raised, which "Graph gate" in docs/design/tracking-derived-properties.md depends on: a
    // change that reads the gate after the barrier may still read 1. Only a creation waits for 2.
    private static int _gateState;

    /// <summary>
    /// Whether a graph dependency may have data. Not raised only while no graph dependency of any subject was ever created; never lowered.
    /// </summary>
    private static bool IsGateRaised
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _gateState) != 0;
    }

    /// <summary>Records that the running derived getter reads the graph state of <paramref name="subject"/>.</summary>
    /// <param name="subject">The subject. Called before its graph state is read.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordRead(IInterceptorSubject subject)
    {
        if (PropertyAccessObservation.ReadObserver is DerivedPropertyRecorder recorder)
        {
            recorder.OnPropertyRead(new PropertyReadObservation(Of(subject)));
        }
    }

    /// <summary>Records that the running derived getter reads the children of <paramref name="subject"/>.</summary>
    /// <param name="subject">The subject. Called before its children are read.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordChildrenRead(IInterceptorSubject subject)
    {
        if (PropertyAccessObservation.ReadObserver is DerivedPropertyRecorder recorder)
        {
            recorder.OnPropertyRead(new PropertyReadObservation(ChildrenOf(subject)));
        }
    }

    /// <summary>Recalculates the derived properties that read the graph state of <paramref name="subject"/>.</summary>
    /// <param name="subject">The subject. Called after its graph state changed.</param>
    /// <param name="hasFailed">Whether the operation that changed the graph state threw: its exception wins, and an
    /// exception of a recalculation is traced instead of thrown.</param>
    public static void Changed(IInterceptorSubject subject, bool hasFailed = false) => Changed(subject, holder: null, hasFailed);

    /// <summary>
    /// Recalculates the derived properties that read the graph state of <paramref name="subject"/> and the children of
    /// <paramref name="holder"/>. Called after a subject was attached or detached through a property of
    /// <paramref name="holder"/>, once both changed.
    /// </summary>
    internal static void Changed(IInterceptorSubject subject, IInterceptorSubject holder) => Changed(subject, holder, hasFailed: false);

    /// <summary>
    /// Recalculates the derived properties that read the graph state of <paramref name="subject"/> and, when given, the
    /// children of <paramref name="holder"/>.
    /// </summary>
    private static void Changed(IInterceptorSubject subject, IInterceptorSubject? holder, bool hasFailed)
    {
        // No graph dependency has data while the graph gate is not raised, see "Graph gate" in
        // docs/design/tracking-derived-properties.md.
        if (!IsGateRaised)
        {
            return;
        }

        // W2 ("The Version Protocol" in docs/design/tracking-derived-properties.md), once for every graph dependency:
        // all of them changed before the first data lookup.
        Interlocked.MemoryBarrier();
        RequestRecalculations(Of(subject), hasFailed);
        if (holder is not null)
        {
            RequestRecalculations(ChildrenOf(holder), hasFailed);
        }
    }

    /// <summary>
    /// Recalculates the derived properties that read the graph state of a subject in <paramref name="children"/> or the
    /// children of <paramref name="holder"/>. Called after the children moved within a property of
    /// <paramref name="holder"/>.
    /// </summary>
    internal static void ChildrenMoved(ReadOnlySpan<SubjectChild> children, IInterceptorSubject holder)
    {
        if (!IsGateRaised)
        {
            return;
        }

        // W2, once for every graph dependency: all of them changed before the first data lookup.
        Interlocked.MemoryBarrier();
        foreach (var child in children)
        {
            RequestRecalculations(Of(child.Subject), hasFailed: false);
        }

        RequestRecalculations(ChildrenOf(holder), hasFailed: false);
    }

    /// <summary>
    /// W3 to W5 ("The Version Protocol" in docs/design/tracking-derived-properties.md) for a change of the graph
    /// dependency <paramref name="dependency"/>, after the W2 of its dispatch. W3 is only the data lookup: the dispatch
    /// read the graph gate before W2, and a graph dependency has no type gate.
    /// </summary>
    private static void RequestRecalculations(PropertyReference dependency, bool hasFailed)
    {
        var data = dependency.TryGetDerivedPropertyData();
        if (data is not null)
        {
            TrackingWriteObserver.RequestRecalculations(dependency, data, hasFailed);
        }
    }

    /// <summary>
    /// The reference under which the graph state of <paramref name="subject"/> is recorded as a dependency: its name,
    /// <see cref="GraphName"/>, is reserved and is never the name of a property.
    /// </summary>
    internal static PropertyReference Of(IInterceptorSubject subject) => new(subject, GraphName);

    /// <summary>
    /// The reference under which the children of <paramref name="subject"/> are recorded as a dependency: its name,
    /// <see cref="ChildrenName"/>, is reserved and is never the name of a property.
    /// </summary>
    internal static PropertyReference ChildrenOf(IInterceptorSubject subject) => new(subject, ChildrenName);

    internal static bool IsGraphDependency(in PropertyReference reference) => reference.Name is GraphName or ChildrenName;

    /// <summary>
    /// Raises the graph gate. Called before the data of a graph dependency is created, and returns only once the gate is
    /// raised, also to a caller that lost the race to raise it.
    /// </summary>
    internal static void RaiseGate()
    {
        if (Volatile.Read(ref _gateState) == 2)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _gateState, 1, 0) == 0)
        {
            // Pairs with the gate read of a graph state change, which has no fence before it, see "Graph gate" in
            // docs/design/tracking-derived-properties.md.
            try
            {
                Interlocked.MemoryBarrierProcessWide();
            }
            finally
            {
                Volatile.Write(ref _gateState, 2);
            }

            return;
        }

        SpinWait.SpinUntil(static () => Volatile.Read(ref _gateState) == 2);
    }

    /// <summary>The reserved name of the graph state dependency, never the name of a property.</summary>
    internal const string GraphName = "\0graph";

    /// <summary>The reserved name of the children dependency, never the name of a property.</summary>
    internal const string ChildrenName = "\0children";
}
