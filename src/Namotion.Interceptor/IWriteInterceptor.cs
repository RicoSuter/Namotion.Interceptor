namespace Namotion.Interceptor;

public interface IWriteInterceptor : IInterceptor
{
    void WriteProperty<TProperty>(WritePropertyInterception<TProperty> context, Action<WritePropertyInterception<TProperty>> next);
}