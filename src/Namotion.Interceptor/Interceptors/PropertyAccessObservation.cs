// src/Namotion.Interceptor/Interceptors/PropertyAccessObservation.cs
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Interceptors;

/// <summary>
/// Observes every property read on the thread while it is installed, including reads of subjects
/// that have no context. Infrastructure for derived property tracking.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPropertyReadObserver
{
    void OnPropertyRead(in PropertyReference property);
}

/// <summary>
/// Observes every property write once it has passed the whole interceptor chain, including writes
/// of subjects that have no context. Must not throw when <see cref="PropertyWriteObservation.HasFailed"/>
/// is set, because the write's own exception is in flight. Infrastructure for derived property tracking.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPropertyWriteObserver
{
    void OnPropertyWritten(in PropertyWriteObservation write);
}

/// <param name="Property">The written property.</param>
/// <param name="IsWritten">Whether the value landed (false when an interceptor vetoed or captured it).</param>
/// <param name="RawTimestamp">The write's raw timestamp (ticks, negated for an explicit null scope), 0 when it did not land or the subject has no context.</param>
/// <param name="IsDerivedRecalculation">Whether the write publishes a derived recalculation.</param>
/// <param name="Depth">The write's nesting depth on the thread, 1 for the outermost write.</param>
/// <param name="HasFailed">Whether the chain threw.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly record struct PropertyWriteObservation(
    PropertyReference Property,
    bool IsWritten,
    long RawTimestamp,
    bool IsDerivedRecalculation,
    int Depth,
    bool HasFailed);

/// <summary>
/// The read observer of the current thread, the write depth of the current thread, and the
/// process-wide write observer. Infrastructure for derived property tracking.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class PropertyAccessObservation
{
    [ThreadStatic]
    private static IPropertyReadObserver? _readObserver;

    [ThreadStatic]
    private static int _writeDepth;

    private static IPropertyWriteObserver? _writeObserver;

    public static IPropertyReadObserver? ReadObserver
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _readObserver;
        set => _readObserver = value;
    }

    public static int WriteDepth => _writeDepth;

    public static IPropertyWriteObserver? WriteObserver
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _writeObserver);
    }

    /// <summary>
    /// Installs the process-wide write observer once; later calls keep the first one.
    /// </summary>
    public static void SetWriteObserver(IPropertyWriteObserver observer)
    {
        Interlocked.CompareExchange(ref _writeObserver, observer, null);
    }

    internal static int EnterWrite() => ++_writeDepth;

    internal static void ExitWrite(int depth) => _writeDepth = depth - 1;

    internal static void ReplaceWriteObserverForTests(IPropertyWriteObserver? observer)
    {
        Volatile.Write(ref _writeObserver, observer);
    }
}
