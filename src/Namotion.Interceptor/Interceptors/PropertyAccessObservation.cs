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

/// <summary>
/// A property write as reported to the <see cref="IPropertyWriteObserver"/>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct PropertyWriteObservation
{
    public PropertyWriteObservation(
        PropertyReference property,
        bool isWritten,
        long rawTimestamp,
        bool isDerivedRecalculation,
        int depth,
        bool hasFailed)
    {
        Property = property;
        IsWritten = isWritten;
        RawTimestamp = rawTimestamp;
        IsDerivedRecalculation = isDerivedRecalculation;
        Depth = depth;
        HasFailed = hasFailed;
    }

    /// <summary>The written property.</summary>
    public PropertyReference Property { get; }

    /// <summary>Whether the value landed (false when an interceptor vetoed or captured it).</summary>
    public bool IsWritten { get; }

    /// <summary>
    /// The write's raw timestamp (ticks, negated for an explicit null scope), 0 when it did not land or the subject
    /// has no context.
    /// </summary>
    public long RawTimestamp { get; }

    /// <summary>Whether the write publishes a derived recalculation.</summary>
    public bool IsDerivedRecalculation { get; }

    /// <summary>The write's nesting depth on the thread, 1 for the outermost write.</summary>
    public int Depth { get; }

    /// <summary>Whether the chain threw.</summary>
    public bool HasFailed { get; }
}

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

    /// <summary>
    /// The read observer of the current thread. A single slot: whoever sets it replaces the previous observer, and
    /// clearing it does not restore one.
    /// </summary>
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
    /// Installs the process-wide write observer once; later calls keep the first one. The first caller wins, so
    /// code that installs an observer before Tracking does pre-empts derived property tracking.
    /// </summary>
    public static void SetWriteObserver(IPropertyWriteObserver observer)
    {
        if (Interlocked.CompareExchange(ref _writeObserver, observer, null) is null)
        {
            // Pairs with the read again of a write without a context, see "Observer installation" in
            // docs/design/tracking-derived-properties.md.
            Interlocked.MemoryBarrierProcessWide();
        }
    }

    internal static int EnterWrite() => ++_writeDepth;

    internal static void ExitWrite(int depth) => _writeDepth = depth - 1;

    internal static void ReplaceWriteObserverForTests(IPropertyWriteObserver? observer)
    {
        Volatile.Write(ref _writeObserver, observer);
    }
}
