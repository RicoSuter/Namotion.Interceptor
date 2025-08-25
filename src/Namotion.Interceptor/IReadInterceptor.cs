namespace Namotion.Interceptor;

public interface IReadInterceptor : IInterceptor
{
    TProperty ReadProperty<TProperty>(ReadPropertyInterception context, Func<ReadPropertyInterception, TProperty> next);
}
