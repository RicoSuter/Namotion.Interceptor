namespace Namotion.Interceptor.Interceptors;

public delegate void WriteInterceptionAction<TProperty>(ref WritePropertyInterception<TProperty> interception);

public interface IWriteInterceptor
{
    void WriteProperty<TProperty>(ref WritePropertyInterception<TProperty> context, WriteInterceptionAction<TProperty> next);
}

public struct WritePropertyInterception<TProperty>
{
    /// <summary>
    /// Gets the property to write a value to.
    /// </summary>
    public PropertyReference Property { get; }
 
    /// <summary>
    /// Gets the current property value.
    /// </summary>
    public TProperty CurrentValue { get; }
    
    /// <summary>
    /// Gets the new value to write (might be different than the value returned by calling the
    /// getter after the write, use <see cref="GetCurrentValue"/> for that).
    /// </summary>
    public TProperty NewValue { get; set; }

    public WritePropertyInterception(PropertyReference property, TProperty currentValue, TProperty newValue)
    {
        Property = property;
        CurrentValue = currentValue;
        NewValue = newValue;
    }
    
    /// <summary>
    /// Reads the current property value (might be different from <see cref="NewValue"/> if the property is derived).
    /// Must only be used after the 'next()' call in the write interceptor.
    /// </summary>
    /// <returns>The property value.</returns>
    public TProperty GetCurrentValue() => Property.Metadata.IsDerived ? 
        (TProperty)Property.Metadata.GetValue?.Invoke(Property.Subject)! : 
        NewValue;
}