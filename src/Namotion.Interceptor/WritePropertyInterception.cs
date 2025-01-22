namespace Namotion.Interceptor;

public readonly struct WritePropertyInterception
{
    public PropertyReference Property { get; }
 
    public object? CurrentValue { get; }
    
    public object? NewValue { get; }

    public WritePropertyInterception(PropertyReference property, object? currentValue, object? newValue)
    {
        Property = property;
        CurrentValue = currentValue;
        NewValue = newValue;
    }
}