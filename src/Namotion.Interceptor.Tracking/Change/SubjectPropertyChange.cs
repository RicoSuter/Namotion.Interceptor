using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Namotion.Interceptor.Tracking.Change;

public struct SubjectPropertyChange
{
    // Inline storage for values (16 bytes - enough for most value types)
    // This avoids boxing by storing values directly in the struct
    private InlineValueStorage _inlineStorage;
    private BoxedValues? _boxedHolder; // Only used for object initializer pattern or large types
    
    private byte _holderType; // 0 = none, 1 = inline (typed), 2 = boxed

    public PropertyReference Property { get; init; }
    
    public object? Source { get; init; }
    
    public DateTimeOffset Timestamp { get; init; }

    // Internal constructor used by Create<T> method for typed values
    private SubjectPropertyChange(PropertyReference property, object? source, DateTimeOffset timestamp, InlineValueStorage inlineStorage)
    {
        Property = property;
        Source = source;
        Timestamp = timestamp;
        _inlineStorage = inlineStorage; // No boxing! Values stored inline
        _boxedHolder = null;
        _holderType = 1;
    }

    // Factory method that avoids boxing the values completely
    public static SubjectPropertyChange Create<TValue>(
        PropertyReference property,
        object? source,
        DateTimeOffset timestamp,
        TValue oldValue,
        TValue newValue)
    {
        // For reference types or types too large for inline storage, use boxed approach
        if (!typeof(TValue).IsValueType || Unsafe.SizeOf<TValue>() > InlineValueStorage.MaxInlineSize)
        {
            return new SubjectPropertyChange
            {
                Property = property,
                Source = source,
                Timestamp = timestamp,
                _boxedHolder = new BoxedValues { OldValue = oldValue, NewValue = newValue },
                _holderType = 2
            };
        }

        // For value types that fit, store inline without boxing
        return new SubjectPropertyChange(
            property,
            source,
            timestamp,
            InlineValueStorage.Create(oldValue, newValue));
    }

    /// <summary>
    /// Gets the old value with the specified type, avoiding boxing for value types when stored inline.
    /// </summary>
    public TValue GetOldValue<TValue>()
    {
        if (_holderType == 1 && _inlineStorage.TryGetOldValue<TValue>(out var value))
        {
            return value; // No boxing!
        }

        return _holderType switch
        {
            1 => ConvertOrThrow<TValue>(_inlineStorage.GetOldValueBoxed(), nameof(GetOldValue)),
            2 => ConvertOrThrow<TValue>(_boxedHolder!.OldValue, nameof(GetOldValue)),
            _ => throw new InvalidOperationException("No value holder available.")
        };
    }

    /// <summary>
    /// Gets the new value with the specified type, avoiding boxing for value types when stored inline.
    /// </summary>
    public TValue GetNewValue<TValue>()
    {
        if (_holderType == 1 && _inlineStorage.TryGetNewValue<TValue>(out var value))
        {
            return value; // No boxing!
        }

        return _holderType switch
        {
            1 => ConvertOrThrow<TValue>(_inlineStorage.GetNewValueBoxed(), nameof(GetNewValue)),
            2 => ConvertOrThrow<TValue>(_boxedHolder!.NewValue, nameof(GetNewValue)),
            _ => throw new InvalidOperationException("No value holder available.")
        };
    }

    /// <summary>
    /// Tries to get the old value with the specified type, avoiding boxing for value types when stored inline.
    /// </summary>
    public bool TryGetOldValue<TValue>(out TValue value)
    {
        if (_holderType == 1)
        {
            return _inlineStorage.TryGetOldValue(out value);
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Tries to get the new value with the specified type, avoiding boxing for value types when stored inline.
    /// </summary>
    public bool TryGetNewValue<TValue>(out TValue value)
    {
        if (_holderType == 1)
        {
            return _inlineStorage.TryGetNewValue(out value);
        }

        value = default!;
        return false;
    }

    private static TValue ConvertOrThrow<TValue>(object? value, string valueName)
    {
        if (value is TValue typedValue)
        {
            return typedValue;
        }
        
        if (value == null && Equals(default(TValue), value))
        {
            return default!;
        }

        var actualType = value?.GetType()?.Name ?? "null";
        var expectedType = typeof(TValue).Name;
        throw new InvalidOperationException(
            $"Type mismatch for {valueName}: expected {expectedType}, but got {actualType}.");
    }

    // Inline storage using unsafe code to avoid boxing
    [StructLayout(LayoutKind.Explicit)]
    private struct InlineValueStorage
    {
        public const int MaxInlineSize = 8; // 8 bytes per value (16 total for old + new)

        [FieldOffset(0)] private long _oldValueData;
        [FieldOffset(8)] private long _newValueData;
        [FieldOffset(16)] private Type? _valueType;

        public static InlineValueStorage Create<TValue>(TValue oldValue, TValue newValue)
        {
            var storage = new InlineValueStorage { _valueType = typeof(TValue) };
            
            // Store values inline using unsafe code
            Unsafe.WriteUnaligned(ref Unsafe.As<long, byte>(ref storage._oldValueData), oldValue);
            Unsafe.WriteUnaligned(ref Unsafe.As<long, byte>(ref storage._newValueData), newValue);
            
            return storage;
        }

        public bool TryGetOldValue<TValue>(out TValue value)
        {
            if (_valueType == typeof(TValue))
            {
                value = Unsafe.ReadUnaligned<TValue>(ref Unsafe.As<long, byte>(ref _oldValueData));
                return true;
            }

            value = default!;
            return false;
        }

        public bool TryGetNewValue<TValue>(out TValue value)
        {
            if (_valueType == typeof(TValue))
            {
                value = Unsafe.ReadUnaligned<TValue>(ref Unsafe.As<long, byte>(ref _newValueData));
                return true;
            }

            value = default!;
            return false;
        }

        public object? GetOldValueBoxed()
        {
            if (_valueType == null) return null;
            
            // Box only when explicitly requested
            var getterMethod = typeof(InlineValueStorage)
                .GetMethod(nameof(TryGetOldValue))!
                .MakeGenericMethod(_valueType);
            
            var parameters = new object?[] { null };
            getterMethod.Invoke(this, parameters);
            return parameters[0];
        }

        public object? GetNewValueBoxed()
        {
            if (_valueType == null) return null;
            
            // Box only when explicitly requested
            var getterMethod = typeof(InlineValueStorage)
                .GetMethod(nameof(TryGetNewValue))!
                .MakeGenericMethod(_valueType);
            
            var parameters = new object?[] { null };
            getterMethod.Invoke(this, parameters);
            return parameters[0];
        }
    }

    // Mutable class for object initializer pattern
    private class BoxedValues
    {
        public object? OldValue { get; set; }
        public object? NewValue { get; set; }
    }
}
