namespace Namotion.Interceptor;

public readonly struct WritePropertyInterception<TProperty>
{
    public PropertyReference Property { get; }
 
    public TProperty CurrentValue { get; }
    
    public TProperty NewValue { get; }

    public WritePropertyInterception(PropertyReference property, TProperty currentValue, TProperty newValue)
    {
        Property = property;
        CurrentValue = currentValue;
        NewValue = newValue;
    }
}