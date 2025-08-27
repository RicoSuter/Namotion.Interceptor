namespace Namotion.Interceptor;

public struct WritePropertyInterception<TProperty>
{
    public PropertyReference Property { get; }
 
    public TProperty CurrentValue { get; }
    
    public TProperty NewValue { get; set; }

    public WritePropertyInterception(PropertyReference property, TProperty currentValue, TProperty newValue)
    {
        Property = property;
        CurrentValue = currentValue;
        NewValue = newValue;
    }
}