using System.Collections.Concurrent;
using System.Reflection;
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
    private static readonly ConcurrentDictionary<Type, Func<InlineValueStorage, object>> BoxingDelegates = new();

    public const int MaxSize = 16; // 16 bytes per value (covers decimal, DateTime, DateTimeOffset, Guid, etc.)

    [FieldOffset(0)] private readonly long _valueData0;
    [FieldOffset(8)] private readonly long _valueData1;
    [FieldOffset(16)] private readonly Type? _storedType;

    public Type? StoredType => _storedType;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static InlineValueStorage Create<TValue>(TValue value)
    {
        var storage = new InlineValueStorage();
        Unsafe.AsRef(in storage._storedType) = typeof(TValue);
        Unsafe.WriteUnaligned(ref Unsafe.As<long, byte>(ref Unsafe.AsRef(in storage._valueData0)), value);
        return storage;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue<TValue>(out TValue value)
    {
        if (_storedType == typeof(TValue))
        {
            value = Unsafe.ReadUnaligned<TValue>(ref Unsafe.As<long, byte>(ref Unsafe.AsRef(in _valueData0)));
            return true;
        }

        value = default!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? GetValueBoxed()
    {
        if (_storedType == null) return null;

        // Fast path for common primitive types (no reflection)
        ref var byteRef = ref Unsafe.As<long, byte>(ref Unsafe.AsRef(in _valueData0));

        // 1-8 byte types
        if (_storedType == typeof(int)) return Unsafe.ReadUnaligned<int>(ref byteRef);
        if (_storedType == typeof(long)) return Unsafe.ReadUnaligned<long>(ref byteRef);
        if (_storedType == typeof(double)) return Unsafe.ReadUnaligned<double>(ref byteRef);
        if (_storedType == typeof(float)) return Unsafe.ReadUnaligned<float>(ref byteRef);
        if (_storedType == typeof(bool)) return Unsafe.ReadUnaligned<bool>(ref byteRef);
        if (_storedType == typeof(byte)) return Unsafe.ReadUnaligned<byte>(ref byteRef);
        if (_storedType == typeof(short)) return Unsafe.ReadUnaligned<short>(ref byteRef);
        if (_storedType == typeof(uint)) return Unsafe.ReadUnaligned<uint>(ref byteRef);
        if (_storedType == typeof(ulong)) return Unsafe.ReadUnaligned<ulong>(ref byteRef);
        if (_storedType == typeof(ushort)) return Unsafe.ReadUnaligned<ushort>(ref byteRef);
        if (_storedType == typeof(sbyte)) return Unsafe.ReadUnaligned<sbyte>(ref byteRef);
        if (_storedType == typeof(char)) return Unsafe.ReadUnaligned<char>(ref byteRef);
        if (_storedType == typeof(DateTime)) return Unsafe.ReadUnaligned<DateTime>(ref byteRef);

        // 9-16 byte types
        if (_storedType == typeof(decimal)) return Unsafe.ReadUnaligned<decimal>(ref byteRef);
        if (_storedType == typeof(DateTimeOffset)) return Unsafe.ReadUnaligned<DateTimeOffset>(ref byteRef);
        if (_storedType == typeof(Guid)) return Unsafe.ReadUnaligned<Guid>(ref byteRef);
        if (_storedType == typeof(TimeSpan)) return Unsafe.ReadUnaligned<TimeSpan>(ref byteRef);

        // Custom structs: use cached delegate (first call creates delegate via reflection, subsequent calls are fast)
        return BoxingDelegates.GetOrAdd(_storedType, CreateBoxingDelegateForType)(this);
    }

    private static Func<InlineValueStorage, object> CreateBoxingDelegateForType(Type type)
    {
        // Use reflection once to create a typed delegate, subsequent calls use the fast delegate
        var method = typeof(InlineValueStorage)
            .GetMethod(nameof(CreateBoxingDelegate), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type);

        return (Func<InlineValueStorage, object>)method.Invoke(null, null)!;
    }

    private static Func<InlineValueStorage, object> CreateBoxingDelegate<T>()
    {
        // This delegate is compiled once and reused - fast invocation after first call
        return static storage =>
        {
            storage.TryGetValue<T>(out var value);
            return value!;
        };
    }
}
