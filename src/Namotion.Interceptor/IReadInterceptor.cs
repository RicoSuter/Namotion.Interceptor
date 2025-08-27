namespace Namotion.Interceptor;

public interface IReadInterceptor : IInterceptor
{
    TProperty ReadProperty<TProperty>(ref ReadPropertyInterception context, Func<ReadPropertyInterception, TProperty> next);
}
