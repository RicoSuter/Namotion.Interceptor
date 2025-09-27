namespace Namotion.Interceptor.Interceptors;

public delegate TProperty ReadInterceptionFunc<out TProperty>(ref PropertyReadContext context);

public interface IReadInterceptor
{
    TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionFunc<TProperty> next);
}

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