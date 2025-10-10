namespace Namotion.Interceptor.Tracking.Change.Performance;

/// <summary>
/// Boxed holder interface for reference types or large value types that don't fit inline.
/// Enables fast virtual dispatch instead of reflection.
/// </summary>
internal interface IBoxedValueHolder
{
    bool TryGetValue<TValue>(out TValue value);

    object? GetValueBoxed();
}
