using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Namotion.Interceptor.Tracking.Change.Performance;

/// <summary>
/// Inline storage for small value types (primitives, small structs) to avoid boxing.
/// Uses unsafe code to store values directly in the struct without heap allocation.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal readonly struct InlineValueStorage
{
    public const int MaxSize = 16; // 16 bytes per value (covers decimal, DateTime, DateTimeOffset, Guid, etc.)

    [FieldOffset(0)] private readonly long _valueData0;
    [FieldOffset(8)] private readonly long _valueData1;
    [FieldOffset(16)] private readonly InlineValueOperations? _operations;

    public Type? StoredType => _operations?.Type;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static InlineValueStorage Create<TValue>(TValue value)
    {
        // Memory-safety invariants: no references, neither TValue itself nor contained ones (the long
        // fields below are not GC-scanned, so a hidden reference could dangle), and SizeOf <= MaxSize
        // (a larger write would overwrite the GC-tracked operations field at offset 16). Such types
        // belong in BoxedValueHolder. The IsValueType leg is redundant with IsReferenceOrContainsReferences
        // and kept for symmetry with the caller's predicate. All checks fold to JIT constants: valid types
        // pay nothing, an invalid caller fails fast in every configuration.
        if (!typeof(TValue).IsValueType ||
            RuntimeHelpers.IsReferenceOrContainsReferences<TValue>() ||
            Unsafe.SizeOf<TValue>() > MaxSize)
        {
            ThrowUnsupportedType<TValue>();
        }

        var storage = new InlineValueStorage();
        Unsafe.AsRef(in storage._operations) = InlineValueOperations<TValue>.Instance;
        Unsafe.WriteUnaligned(ref Unsafe.As<long, byte>(ref Unsafe.AsRef(in storage._valueData0)), value);
        return storage;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue<TValue>(out TValue value)
    {
        // Each stored type has exactly one operations instance, so identity is the type check. The legs before it
        // repeat the guard of Create and fold to constants: a type Create rejects is never read, even from an empty
        // storage, and a reference type does not touch the instance.
        if (typeof(TValue).IsValueType &&
            !RuntimeHelpers.IsReferenceOrContainsReferences<TValue>() &&
            Unsafe.SizeOf<TValue>() <= MaxSize &&
            ReferenceEquals(_operations, InlineValueOperations<TValue>.Instance))
        {
            value = Read<TValue>();
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Reads the stored value as <typeparamref name="TValue"/>, which must be the stored type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue Read<TValue>()
    {
        Debug.Assert(ReferenceEquals(_operations, InlineValueOperations<TValue>.Instance), "The value is read as another type than the stored one.");
        return Unsafe.ReadUnaligned<TValue>(ref Unsafe.As<long, byte>(ref Unsafe.AsRef(in _valueData0)));
    }

    public object? GetValueBoxed() => _operations?.Box(in this);

    /// <summary>
    /// Returns <c>object.Equals(value, GetValueBoxed())</c>, boxing the stored value only when the result could differ
    /// otherwise.
    /// </summary>
    public bool ValueEquals(object? value) => _operations?.ValueEquals(in this, value) ?? value is null;

    // Kept out of Create so the (JIT-eliminated) guard does not bloat its inlined body.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUnsupportedType<TValue>() =>
        throw new InvalidOperationException(
            $"InlineValueStorage cannot store '{typeof(TValue)}': it is a reference type, contains " +
            $"references (which the GC cannot track inside inline storage), or exceeds {MaxSize} bytes " +
            "(which would overwrite the adjacent operations field). Route this type to BoxedValueHolder.");
}
