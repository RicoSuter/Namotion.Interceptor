namespace Namotion.Interceptor;

public interface IWriteInterceptor : IInterceptor
{
    TProperty WriteProperty<TProperty>(WritePropertyInterception<TProperty> context, Func<WritePropertyInterception<TProperty>, TProperty> next);
}