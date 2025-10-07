using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Namotion.Interceptor.Tracking.Change;

public readonly struct SubjectPropertyChange
{
    // Discriminated union: either inline storage OR boxed holder (per value)
    private readonly InlineValueStorage _oldValueStorage;
    private readonly InlineValueStorage _newValueStorage;
    private readonly object? _oldBoxedHolder; // IBoxedValueHolder or null
    private readonly object? _newBoxedHolder; // IBoxedValueHolder or null

    private SubjectPropertyChange(
        PropertyReference property,
        object? source,
        DateTimeOffset timestamp,
        InlineValueStorage oldValueStorage,
        InlineValueStorage newValueStorage,
        object? oldBoxedHolder,
        object? newBoxedHolder)
    {
        Property = property;
        Source = source;
        Timestamp = timestamp;
        _oldValueStorage = oldValueStorage;
        _newValueStorage = newValueStorage;
        _oldBoxedHolder = oldBoxedHolder;
        _newBoxedHolder = newBoxedHolder;
    }

    public PropertyReference Property { get; }

    public object? Source { get; }

    public DateTimeOffset Timestamp { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectPropertyChange Create<TValue>(
        PropertyReference property,
        object? source,
        DateTimeOffset timestamp,
        TValue oldValue,
        TValue newValue)
    {
        // Fast path: value types that fit inline (primitives, small structs) - ZERO allocations
        if (typeof(TValue).IsValueType && Unsafe.SizeOf<TValue>() <= InlineValueStorage.MaxSize)
        {
            return new SubjectPropertyChange(
                property,
                source,
                timestamp,
                InlineValueStorage.Create(oldValue),
                InlineValueStorage.Create(newValue),
                null,
                null);
        }

        // Slow path: reference types or large value types - TWO allocations (one per value)
        return new SubjectPropertyChange(
            property,
            source,
            timestamp,
            default,
            default,
            new BoxedValueHolder<TValue>(oldValue),
            new BoxedValueHolder<TValue>(newValue));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetOldValue<TValue>()
    {
        if (TryGetValue(_oldValueStorage, _oldBoxedHolder, out TValue value))
        {
            return value;
        }

        throw new InvalidCastException($"Old value of property '{Property.Name}' is of type '{_oldValueStorage.StoredType?.FullName ?? _oldBoxedHolder?.GetType()?.FullName ?? "null"}' and cannot be cast to '{typeof(TValue).FullName}'.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetNewValue<TValue>()
    {
        if (TryGetValue(_newValueStorage, _newBoxedHolder, out TValue value))
        {
            return value;
        }

        throw new InvalidCastException($"New value of property '{Property.Name}' is of type '{_newValueStorage.StoredType?.FullName ?? _newBoxedHolder?.GetType()?.FullName ?? "null"}' and cannot be cast to '{typeof(TValue).FullName}'.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetOldValue<TValue>(out TValue value)
    {
        return TryGetValue(_oldValueStorage, _oldBoxedHolder, out value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetNewValue<TValue>(out TValue value)
    {
        return TryGetValue(_newValueStorage, _newBoxedHolder, out value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetValue<TValue>(InlineValueStorage storage, object? boxedHolder, out TValue value)
    {
        // Fast path: inline storage (zero allocation retrieval)
        if (boxedHolder == null)
        {
            if (storage.TryGetValue(out value))
            {
                return true;
            }

            // Support casting to object (will box the value)
            if (typeof(TValue) == typeof(object))
            {
                value = (TValue)storage.GetValueBoxed()!;
                return true;
            }

            value = default!;
            return false;
        }

        // Fast path: boxed holder with interface dispatch (no reflection)
        if (boxedHolder is IBoxedValueHolder holder)
        {
            // Fast path: try direct type match first
            if (holder.TryGetValue(out value))
            {
                return true;
            }

            // Fallback: box for object cast (supports custom structs)
            if (typeof(TValue) == typeof(object))
            {
                value = (TValue)holder.GetValueBoxed()!;
                return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Inline storage for small value types (primitives, small structs) to avoid boxing.
    /// Uses unsafe code to store values directly in the struct without heap allocation.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private readonly struct InlineValueStorage
    {
        public const int MaxSize = 8; // 8 bytes per value (covers int, long, double, float, etc.)

        [FieldOffset(0)] private readonly long _valueData;
        [FieldOffset(8)] private readonly Type? _storedType;

        public Type? StoredType => _storedType;

        public static InlineValueStorage Create<TValue>(TValue value)
        {
            var storage = new InlineValueStorage();
            Unsafe.AsRef(in storage._storedType) = typeof(TValue);
            Unsafe.WriteUnaligned(ref Unsafe.As<long, byte>(ref Unsafe.AsRef(in storage._valueData)), value);
            return storage;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue<TValue>(out TValue value)
        {
            if (_storedType == typeof(TValue))
            {
                value = Unsafe.ReadUnaligned<TValue>(ref Unsafe.As<long, byte>(ref Unsafe.AsRef(in _valueData)));
                return true;
            }

            value = default!;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object? GetValueBoxed()
        {
            return BoxValue(_valueData, _storedType);
        }

        // Cache boxing delegates for custom structs to avoid reflection on every call
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<long, object>> _boxingDelegates = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static object? BoxValue(long data, Type? type)
        {
            if (type == null) return null;

            // Fast path for common primitive types (no dictionary lookup)
            ref var byteRef = ref Unsafe.As<long, byte>(ref Unsafe.AsRef(in data));

            if (type == typeof(int)) return Unsafe.ReadUnaligned<int>(ref byteRef);
            if (type == typeof(long)) return Unsafe.ReadUnaligned<long>(ref byteRef);
            if (type == typeof(double)) return Unsafe.ReadUnaligned<double>(ref byteRef);
            if (type == typeof(float)) return Unsafe.ReadUnaligned<float>(ref byteRef);
            if (type == typeof(bool)) return Unsafe.ReadUnaligned<bool>(ref byteRef);
            if (type == typeof(byte)) return Unsafe.ReadUnaligned<byte>(ref byteRef);
            if (type == typeof(short)) return Unsafe.ReadUnaligned<short>(ref byteRef);
            if (type == typeof(decimal)) return Unsafe.ReadUnaligned<decimal>(ref byteRef);
            if (type == typeof(DateTime)) return Unsafe.ReadUnaligned<DateTime>(ref byteRef);
            if (type == typeof(DateTimeOffset)) return Unsafe.ReadUnaligned<DateTimeOffset>(ref byteRef);

            // Custom structs: use cached compiled delegate
            var boxingDelegate = _boxingDelegates.GetOrAdd(type, CreateBoxingDelegate);
            return boxingDelegate(data);
        }

        private static Func<long, object> CreateBoxingDelegate(Type type)
        {
            // Create a compiled delegate: (long data) => Unsafe.ReadUnaligned<T>(ref Unsafe.As<long, byte>(ref data))
            var dataParam = System.Linq.Expressions.Expression.Parameter(typeof(long), "data");

            // ref Unsafe.AsRef(in data)
            var asRefMethod = typeof(Unsafe).GetMethod(nameof(Unsafe.AsRef), new[] { type.MakeByRefType() })!
                .MakeGenericMethod(typeof(long));
            var dataRef = System.Linq.Expressions.Expression.Call(asRefMethod, dataParam);

            // ref Unsafe.As<long, byte>(ref dataRef)
            var asMethod = typeof(Unsafe).GetMethods()
                .First(m => m.Name == nameof(Unsafe.As) &&
                           m.IsGenericMethodDefinition &&
                           m.GetGenericArguments().Length == 2 &&
                           m.GetParameters().Length == 1 &&
                           m.GetParameters()[0].ParameterType.IsByRef)
                .MakeGenericMethod(typeof(long), typeof(byte));
            var byteRef = System.Linq.Expressions.Expression.Call(asMethod, dataRef);

            // Unsafe.ReadUnaligned<type>(ref byteRef)
            var readMethod = typeof(Unsafe).GetMethod(nameof(Unsafe.ReadUnaligned), new[] { typeof(byte).MakeByRefType() })!
                .MakeGenericMethod(type);
            var readCall = System.Linq.Expressions.Expression.Call(readMethod, byteRef);

            // Box to object
            var boxed = System.Linq.Expressions.Expression.Convert(readCall, typeof(object));

            var lambda = System.Linq.Expressions.Expression.Lambda<Func<long, object>>(boxed, dataParam);
            return lambda.Compile();
        }
    }

    /// <summary>
    /// Boxed holder for reference types or large value types that don't fit inline.
    /// Implements interface to enable fast virtual dispatch instead of reflection.
    /// </summary>
    private interface IBoxedValueHolder
    {
        bool TryGetValue<TValue>(out TValue value);
        object? GetValueBoxed();
    }

    private sealed class BoxedValueHolder<T> : IBoxedValueHolder
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
}
