namespace Namotion.Interceptor;

public readonly record struct WritePropertyInterception(
    PropertyReference Property,
    object? CurrentValue,
    object? NewValue,
    bool IsDerived)
{
}
