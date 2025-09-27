namespace Namotion.Interceptor.Interceptors;

public delegate TProperty ReadInterceptionFunc<out TProperty>(ref ReadPropertyContext context);

public interface IReadInterceptor
{
    TProperty ReadProperty<TProperty>(ref ReadPropertyContext context, ReadInterceptionFunc<TProperty> next);
}

public readonly struct ReadPropertyContext
{
    /// <summary>
    /// Gets the property to read a value from.
    /// </summary>
    public PropertyReference Property { get; }

    public ReadPropertyContext(PropertyReference property)
    {
        Property = property;
    }
}