namespace Namotion.Interceptor.Interceptors;

public delegate TProperty ReadInterceptionFunc<out TProperty>(ref ReadPropertyInterception interception);

public interface IReadInterceptor
{
    TProperty ReadProperty<TProperty>(ref ReadPropertyInterception context, ReadInterceptionFunc<TProperty> next);
}

public readonly struct ReadPropertyInterception
{
    public PropertyReference Property { get; }

    public ReadPropertyInterception(PropertyReference property)
    {
        Property = property;
    }
}