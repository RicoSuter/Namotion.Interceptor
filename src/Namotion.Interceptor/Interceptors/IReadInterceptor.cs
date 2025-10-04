namespace Namotion.Interceptor.Interceptors;

public interface IReadInterceptor
{
    TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next);
}

public delegate TProperty ReadInterceptionDelegate<out TProperty>(ref PropertyReadContext context);

public readonly struct PropertyReadContext
{
    /// <summary>
    /// Gets the property to read a value from.
    /// </summary>
    public PropertyReference Property { get; }

    public PropertyReadContext(IInterceptorSubject subject, string name)
    {
        Property = new PropertyReference(subject, name);
    }
}