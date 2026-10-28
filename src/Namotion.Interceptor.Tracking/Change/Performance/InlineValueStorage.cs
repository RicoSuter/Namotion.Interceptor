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

    /// <summary>
    /// Gets whether no value is stored.
    /// </summary>
    public bool IsEmpty => _operations is null;

    public Type? StoredType => _operations?.Type;

    /// <summary>
    /// Gets whether a <typeparamref name="TValue"/> can be stored inline: it holds no reference and fits in
    /// <see cref="MaxSize"/> bytes. Other types belong in <see cref="BoxedValueHolder{T}"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanStore<TValue>() =>
        // The value fields are not GC-scanned, so a reference in them could dangle, and a larger write would overwrite
        // the GC-tracked operations field. Both checks fold to JIT constants, so a guard on this costs nothing.
        !RuntimeHelpers.IsReferenceOrContainsReferences<TValue>() && Unsafe.SizeOf<TValue>() <= MaxSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static InlineValueStorage Create<TValue>(TValue value)
    {
        if (!CanStore<TValue>())
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
        // Each stored type has exactly one operations instance, so identity is the type check. The guards first keep a
        // type that is never stored from touching the instance. IsValueType stays inline rather than in CanStore: the
        // Native AOT scanner folds it here but not through a call, and would otherwise compile a reference instantiation.
        if (typeof(TValue).IsValueType && CanStore<TValue>() && ReferenceEquals(_operations, InlineValueOperations<TValue>.Instance))
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
        if (!CanStore<TValue>())
        {
            ThrowUnsupportedType<TValue>();
        }

        Debug.Assert(ReferenceEquals(_operations, InlineValueOperations<TValue>.Instance), "The value is read as another type than the stored one.");
        return Unsafe.ReadUnaligned<TValue>(ref Unsafe.As<long, byte>(ref Unsafe.AsRef(in _valueData0)));
    }

    public object? GetValueBoxed() => _operations?.Box(in this);

    /// <summary>
    /// Returns <c>object.Equals(value, GetValueBoxed())</c>, boxing the stored value only when the result could differ
    /// otherwise.
    /// </summary>
    public bool ValueEquals(object? value) => _operations?.ValueEquals(in this, value) ?? value is null;

    // Kept out of the inlined callers so their (JIT-eliminated) guard does not bloat them.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUnsupportedType<TValue>() =>
        throw new InvalidOperationException(
            $"InlineValueStorage cannot store '{typeof(TValue)}': it is a reference type, contains " +
            $"references (which the GC cannot track inside inline storage), or exceeds {MaxSize} bytes " +
            "(which would overwrite the adjacent operations field). Route this type to BoxedValueHolder.");
}
