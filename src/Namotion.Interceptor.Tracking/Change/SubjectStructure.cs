using System.ComponentModel;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Structure reads and changes for derived property tracking: whether a subject is attached and registered, its
/// reference count, its parents and its registered properties, and its children, the subjects attached through its
/// properties. Infrastructure for the lifecycle and the registry.
/// </summary>
/// <remarks>See "Structure Cells" in docs/design/tracking-derived-properties.md.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SubjectStructure
{
    // 0 while no cell was ever created, 1 while the first creation raises the gate, 2 once it is raised.
    private static int _gateState;

    /// <summary>
    /// Whether a cell may have data. Not raised only while no cell of any subject was ever created; never lowered.
    /// </summary>
    internal static bool IsGateRaised
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _gateState) != 0;
    }

    /// <summary>Records that the running derived getter reads the structure of <paramref name="subject"/>.</summary>
    /// <param name="subject">The subject. Called before its structure is read.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordRead(IInterceptorSubject subject)
    {
        if (PropertyAccessObservation.ReadObserver is DerivedPropertyRecorder recorder)
        {
            recorder.OnPropertyRead(Of(subject));
        }
    }

    /// <summary>Records that the running derived getter reads the children of <paramref name="subject"/>.</summary>
    /// <param name="subject">The subject. Called before its children are read.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordChildrenRead(IInterceptorSubject subject)
    {
        if (PropertyAccessObservation.ReadObserver is DerivedPropertyRecorder recorder)
        {
            recorder.OnPropertyRead(ChildrenOf(subject));
        }
    }

    /// <summary>Recalculates the derived properties that read the structure of <paramref name="subject"/>.</summary>
    /// <param name="subject">The subject. Called after its structure changed.</param>
    /// <param name="hasFailed">Whether the operation that changed the structure threw: its exception wins, and an
    /// exception of a recalculation is traced instead of thrown.</param>
    public static void Changed(IInterceptorSubject subject, bool hasFailed = false)
    {
        TrackingWriteObserver.OnStructureChanged(subject, hasFailed);
    }

    /// <summary>
    /// The reference under which the structure of <paramref name="subject"/> is recorded and linked: it has no
    /// property name, so it is never a property of the subject.
    /// </summary>
    internal static PropertyReference Of(IInterceptorSubject subject) => new(subject, null!);

    /// <summary>
    /// The reference under which the children of <paramref name="subject"/> are recorded and linked: its name contains
    /// a name starting with a null character, which the registry rejects for an added property.
    /// </summary>
    internal static PropertyReference ChildrenOf(IInterceptorSubject subject) => new(subject, ChildrenName);

    internal static bool IsStructure(in PropertyReference reference) => reference.Name is null or ChildrenName;

    /// <summary>
    /// Raises the structure gate. Called before the data of a cell is created, and returns only once the gate is
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
            // Pairs with the gate read of a structure change, which has no fence before it, see "Structure gate" in
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

    internal const string ChildrenName = "\0children";
}
