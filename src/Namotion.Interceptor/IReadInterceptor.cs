namespace Namotion.Interceptor;

public delegate TProperty ReadInterceptionFunc<out TProperty>(ref ReadPropertyInterception interception);

public interface IReadInterceptor : IInterceptor
{
    TProperty ReadProperty<TProperty>(ref ReadPropertyInterception context, ReadInterceptionFunc<TProperty> next);
}
