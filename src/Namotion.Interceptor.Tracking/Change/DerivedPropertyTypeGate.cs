using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Whether <see cref="DerivedPropertyData"/> exists for any property of any subject of a type. The gate is
/// raised before the first data of the type is stored and never lowered, so while it is not raised no subject
/// of the type has dependents to notify or derived properties to detach, and the data lookup is skipped.
/// </summary>
internal sealed class DerivedPropertyTypeGate
{
    private static readonly ConcurrentDictionary<Type, DerivedPropertyTypeGate> Gates = new();

    [ThreadStatic]
    private static DerivedPropertyTypeGate? _last;

    private readonly Type _type;
    private bool _isRaised;

    private DerivedPropertyTypeGate(Type type)
    {
        _type = type;
    }

    /// <summary>Returns the gate of the subject's type.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static DerivedPropertyTypeGate Of(IInterceptorSubject subject)
    {
        // Consecutive accesses mostly hit subjects of one type, which the thread's last gate answers without a lookup.
        var type = subject.GetType();
        var last = _last;
        return last is not null && ReferenceEquals(last._type, type) ? last : Find(type);
    }

    private static DerivedPropertyTypeGate Find(Type type)
    {
        var gate = Gates.GetOrAdd(type, static type => new DerivedPropertyTypeGate(type));
        _last = gate;
        return gate;
    }

    internal bool IsRaised
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _isRaised);
    }

    /// <summary>Raises the gate. Called before data of a subject of the type is stored.</summary>
    internal void Raise()
    {
        if (!Volatile.Read(ref _isRaised))
        {
            // The fence orders this store before the store of the data that follows, which the write protocol
            // relies on: a writer that finds the gate not raised after its own fence (W2) wrote before any
            // data of the type existed.
            Volatile.Write(ref _isRaised, true);
            Interlocked.MemoryBarrier();
        }
    }
}
