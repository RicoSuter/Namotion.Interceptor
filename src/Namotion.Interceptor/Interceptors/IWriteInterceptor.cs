namespace Namotion.Interceptor.Interceptors;

/// <summary>
/// Interceptor that can intercept and modify property write operations.
/// </summary>
public interface IWriteInterceptor
{
    /// <summary>
    /// Intercepts a property write operation.
    /// </summary>
    /// <typeparam name="TProperty">A hint for the property type. May be <c>object</c> when
    /// values are boxed through non-generic paths (e.g., <c>SetPropertyValueWithInterception</c>).
    /// Use <c>context.Property.Metadata.Type</c> for the actual declared property type.</typeparam>
    /// <param name="context">The write context containing the property reference and values.</param>
    /// <param name="next">The next interceptor in the chain to call.</param>
    void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next);
}

public delegate void WriteInterceptionDelegate<TProperty>(ref PropertyWriteContext<TProperty> context);

/// <summary>
/// Context for a property write operation.
/// <typeparamref name="TProperty"/> is a hint — it may be <c>object</c> when values are
/// boxed through non-generic paths. Use <c>Property.Metadata.Type</c> for the actual
/// declared property type.
/// </summary>
public struct PropertyWriteContext<TProperty>
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
    /// getter after the write, use <see cref="GetFinalValue"/> for that).
    /// </summary>
    public TProperty NewValue { get; set; }

    /// <summary>
    /// Gets or sets whether the write was performed.
    /// Set to true by the write action when the value is actually written.
    /// </summary>
    public bool IsWritten { get; set; }

    public PropertyWriteContext(PropertyReference property, TProperty currentValue, TProperty newValue)
    {
        Property = property;
        CurrentValue = currentValue;
        NewValue = newValue;
        IsWritten = false;
    }

    /// <summary>
    /// Reads the current property value (might be different from <see cref="NewValue"/> if the property is derived).
    /// Must only be used after the 'next()' call in the write interceptor.
    /// </summary>
    /// <returns>The property value.</returns>
    public TProperty GetFinalValue() => Property.Metadata.IsDerived ?
        (TProperty)Property.Metadata.GetValue?.Invoke(Property.Subject)! :
        NewValue;
}