using System.Collections.Concurrent;
using System.Linq.Expressions;
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
    private static readonly ConcurrentDictionary<Type, Func<long, object>> BoxingDelegates = new();

    public const int MaxSize = 8; // 8 bytes per value (covers int, long, double, float, etc.)

    [FieldOffset(0)] private readonly long _valueData;
    [FieldOffset(8)] private readonly Type? _storedType;

    public Type? StoredType => _storedType;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        if (_storedType == null) return null;

        // Fast path for common primitive types (no dictionary lookup)
        ref var byteRef = ref Unsafe.As<long, byte>(ref Unsafe.AsRef(in _valueData));

        if (_storedType == typeof(int)) return Unsafe.ReadUnaligned<int>(ref byteRef);
        if (_storedType == typeof(long)) return Unsafe.ReadUnaligned<long>(ref byteRef);
        if (_storedType == typeof(double)) return Unsafe.ReadUnaligned<double>(ref byteRef);
        if (_storedType == typeof(float)) return Unsafe.ReadUnaligned<float>(ref byteRef);
        if (_storedType == typeof(bool)) return Unsafe.ReadUnaligned<bool>(ref byteRef);
        if (_storedType == typeof(byte)) return Unsafe.ReadUnaligned<byte>(ref byteRef);
        if (_storedType == typeof(short)) return Unsafe.ReadUnaligned<short>(ref byteRef);
        if (_storedType == typeof(decimal)) return Unsafe.ReadUnaligned<decimal>(ref byteRef);
        if (_storedType == typeof(DateTime)) return Unsafe.ReadUnaligned<DateTime>(ref byteRef);
        if (_storedType == typeof(DateTimeOffset)) return Unsafe.ReadUnaligned<DateTimeOffset>(ref byteRef);

        // Custom structs: use cached compiled delegate
        return BoxingDelegates.GetOrAdd(_storedType, CreateBoxingDelegate)(_valueData);
    }
    
    private static Func<long, object> CreateBoxingDelegate(Type type)
    {
        // Create a compiled delegate: (long data) => Unsafe.ReadUnaligned<T>(ref Unsafe.As<long, byte>(ref data))
        var dataParam = Expression.Parameter(typeof(long), "data");

        // ref Unsafe.AsRef(in data)
        var asRefMethod = typeof(Unsafe).GetMethod(nameof(Unsafe.AsRef), [type.MakeByRefType()])!.MakeGenericMethod(typeof(long));
        var dataRef = Expression.Call(asRefMethod, dataParam);

        // ref Unsafe.As<long, byte>(ref dataRef)
        var asMethod = typeof(Unsafe).GetMethods()
            .First(m => m is { Name: nameof(Unsafe.As), IsGenericMethodDefinition: true } &&
                        m.GetGenericArguments().Length == 2 &&
                        m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType.IsByRef)
            .MakeGenericMethod(typeof(long), typeof(byte));

        var byteRef = Expression.Call(asMethod, dataRef);

        // Unsafe.ReadUnaligned<type>(ref byteRef)
        var readMethod = typeof(Unsafe).GetMethod(nameof(Unsafe.ReadUnaligned), [typeof(byte).MakeByRefType()])!.MakeGenericMethod(type);

        var readCall = Expression.Call(readMethod, byteRef);
        var boxed = Expression.Convert(readCall, typeof(object));
        return Expression.Lambda<Func<long, object>>(boxed, dataParam).Compile();
    }
}

