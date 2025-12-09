using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change.Performance;

namespace Namotion.Interceptor.Tracking.Change;

public readonly record struct SubjectPropertyChange
{
    // Discriminated union: either inline storage OR boxed holder (per value)
    private readonly InlineValueStorage _oldValueStorage;
    private readonly InlineValueStorage _newValueStorage;
    private readonly object? _oldBoxedHolder; // IBoxedValueHolder or null
    private readonly object? _newBoxedHolder; // IBoxedValueHolder or null

    private SubjectPropertyChange(
        PropertyReference property,
        object? source,
        DateTimeOffset changedTimestamp,
        DateTimeOffset? receivedTimestamp,
        InlineValueStorage oldValueStorage,
        InlineValueStorage newValueStorage,
        object? oldBoxedHolder,
        object? newBoxedHolder)
    {
        Property = property;
        Source = source;
        ChangedTimestamp = changedTimestamp;
        ReceivedTimestamp = receivedTimestamp;
        _oldValueStorage = oldValueStorage;
        _newValueStorage = newValueStorage;
        _oldBoxedHolder = oldBoxedHolder;
        _newBoxedHolder = newBoxedHolder;
    }

    public PropertyReference Property { get; }

    public object? Source { get; }

    public DateTimeOffset ChangedTimestamp { get; }

    public DateTimeOffset? ReceivedTimestamp { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectPropertyChange Create<TValue>(
        PropertyReference property,
        object? source,
        DateTimeOffset changedTimestamp,
        DateTimeOffset? receivedTimestamp,
        TValue oldValue,
        TValue newValue)
    {
        // Fast path: value types that fit inline (primitives, small structs) - ZERO allocations
        if (typeof(TValue).IsValueType && Unsafe.SizeOf<TValue>() <= InlineValueStorage.MaxSize)
        {
            return new SubjectPropertyChange(
                property,
                source,
                changedTimestamp,
                receivedTimestamp,
                InlineValueStorage.Create(oldValue),
                InlineValueStorage.Create(newValue),
                null,
                null);
        }

        // Fast path: strings - store directly without wrapper (ZERO allocations)
        if (typeof(TValue) == typeof(string))
        {
            return new SubjectPropertyChange(
                property,
                source,
                changedTimestamp,
                receivedTimestamp,
                default,
                default,
                oldValue,
                newValue);
        }

        // Slow path: other reference types or large value types - TWO allocations (one per value)
        return new SubjectPropertyChange(
            property,
            source,
            changedTimestamp,
            receivedTimestamp,
            default,
            default,
            new BoxedValueHolder<TValue>(oldValue),
            new BoxedValueHolder<TValue>(newValue));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetOldValue<TValue>() =>
        TryGetValue(_oldValueStorage, _oldBoxedHolder, out TValue value)
            ? value
            : throw new InvalidCastException($"Old value of property '{Property.Name}' is of type '{_oldValueStorage.StoredType?.FullName ?? _oldBoxedHolder?.GetType().FullName ?? "null"}' and cannot be cast to '{typeof(TValue).FullName}'.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetNewValue<TValue>() =>
        TryGetValue(_newValueStorage, _newBoxedHolder, out TValue value)
            ? value
            : throw new InvalidCastException($"New value of property '{Property.Name}' is of type '{_newValueStorage.StoredType?.FullName ?? _newBoxedHolder?.GetType().FullName ?? "null"}' and cannot be cast to '{typeof(TValue).FullName}'.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetOldValue<TValue>(out TValue value) =>
        TryGetValue(_oldValueStorage, _oldBoxedHolder, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetNewValue<TValue>(out TValue value) =>
        TryGetValue(_newValueStorage, _newBoxedHolder, out value);

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
                // If no inline storage was used, this was a null string/reference
                if (storage.StoredType == null)
                {
                    value = default!;
                    return true;
                }
                value = (TValue)storage.GetValueBoxed()!;
                return true;
            }

            // Handle null strings: boxedHolder is null AND no inline storage was used
            if (typeof(TValue) == typeof(string) && storage.StoredType == null)
            {
                value = default!;
                return true;
            }

            value = default!;
            return false;
        }

        // Fast path: direct string retrieval (strings stored without wrapper)
        if (typeof(TValue) == typeof(string) && boxedHolder is string)
        {
            value = (TValue)boxedHolder;
            return true;
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

        // Support casting stored strings to object
        if (typeof(TValue) == typeof(object) && boxedHolder is string)
        {
            value = (TValue)boxedHolder;
            return true;
        }

        value = default!;
        return false;
    }
}
