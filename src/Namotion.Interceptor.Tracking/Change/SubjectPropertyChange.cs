using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

public readonly record struct SubjectPropertyChange
{
    private readonly object? _oldValue;
    private readonly object? _newValue;
    
    private SubjectPropertyChange(
        PropertyReference property,
        object? source,
        DateTimeOffset timestamp,
        object? oldValue,
        object? newValue)
    {
        Property = property;
        Source = source;
        Timestamp = timestamp;
        
        _oldValue = oldValue;
        _newValue = newValue;
    }

    public PropertyReference Property { get;  }
    
    public object? Source { get;  }
    
    public DateTimeOffset Timestamp { get;  }
    
    public static SubjectPropertyChange Create<TValue>(
        PropertyReference property,
        object? source,
        DateTimeOffset timestamp,
        TValue oldValue,
        TValue newValue)
    {
        return new SubjectPropertyChange(property, source, timestamp, oldValue, newValue);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetOldValue<TValue>()
    {
        if (TryGetOldValue<TValue>(out var value))
        {
            return value;
        }

        throw new InvalidCastException($"Old value of property '{Property.Name}' is of type '{_oldValue?.GetType().FullName ?? "null"}' and cannot be cast to '{typeof(TValue).FullName}'.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetNewValue<TValue>()
    {
        if (TryGetNewValue<TValue>(out var value))
        {
            return value;
        }

        throw new InvalidCastException($"New value of property '{Property.Name}' is of type '{_newValue?.GetType().FullName ?? "null"}' and cannot be cast to '{typeof(TValue).FullName}'.");
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetOldValue<TValue>(out TValue value)
    {
        var v = _oldValue;
        if (v is null)
        {
            if (default(TValue) is null)
            {
                value = default!;
                return true;
            }

            value = default!;
            return false;
        }

        if (v is TValue t)
        {
            value = t;
            return true;
        }

        value = default!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetNewValue<TValue>(out TValue value)
    {
        var v = _newValue;
        if (v is null)
        {
            if (default(TValue) is null)
            {
                value = default!;
                return true;
            }

            value = default!;
            return false;
        }

        if (v is TValue t)
        {
            value = t;
            return true;
        }

        value = default!;
        return false;
    }
}
