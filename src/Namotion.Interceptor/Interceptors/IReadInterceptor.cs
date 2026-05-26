namespace Namotion.Interceptor.Interceptors;

/// <summary>
/// Interceptor that can read and transform property values during property access.
/// </summary>
public interface IReadInterceptor
{
    /// <summary>
    /// Intercepts a property read operation.
    /// </summary>
    /// <typeparam name="TProperty">A hint for the property type. May be <c>object</c> when
    /// values are boxed through non-generic paths. Use <c>context.Property.Metadata.Type</c>
    /// for the actual declared property type.</typeparam>
    /// <param name="context">The read context containing the property reference.</param>
    /// <param name="next">The next interceptor in the chain to call.</param>
    /// <returns>The property value.</returns>
    TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next);
}

public delegate TProperty ReadInterceptionDelegate<out TProperty>(ref PropertyReadContext context);

public readonly struct PropertyReadContext
{
    /// <summary>
    /// Gets the property to read a value from.
    /// </summary>
    public PropertyReference Property { get; }

    public PropertyReadContext(PropertyReference property)
    {
        Property = property;
    }
}