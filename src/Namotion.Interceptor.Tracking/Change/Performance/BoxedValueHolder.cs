using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change.Performance;

/// <summary>
/// Boxed holder for reference types or large value types that don't fit inline.
/// </summary>
internal sealed class BoxedValueHolder<T> : IBoxedValueHolder
{
    private readonly T _value;

    public BoxedValueHolder(T value)
    {
        _value = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue<TValue>(out TValue value)
    {
        // Fast path: exact type match
        if (typeof(T) == typeof(TValue))
        {
            value = Unsafe.As<T, TValue>(ref Unsafe.AsRef(in _value));
            return true;
        }

        // Cast to object
        if (typeof(TValue) == typeof(object))
        {
            value = (TValue)(object?)_value!;
            return true;
        }

        // Nullable/reference type unboxing
        if (default(T) == null)
        {
            if (_value == null)
            {
                value = default!;
                return default(TValue) == null;
            }

            if (_value is TValue typedValue)
            {
                value = typedValue;
                return true;
            }
        }

        value = default!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? GetValueBoxed() => _value;
}
