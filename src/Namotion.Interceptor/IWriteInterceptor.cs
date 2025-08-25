namespace Namotion.Interceptor;

public interface IWriteInterceptor : IInterceptor
{
    TProperty WriteProperty<TProperty>(WritePropertyInterception context, Func<WritePropertyInterception, TProperty> next);
}