namespace Namotion.Interceptor;

public interface IWriteInterceptor : IInterceptor
{
    void WriteProperty<TProperty>(ref WritePropertyInterception<TProperty> context, Action<WritePropertyInterception<TProperty>> next);
}