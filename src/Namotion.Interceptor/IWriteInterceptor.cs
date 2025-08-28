namespace Namotion.Interceptor;

public delegate void WriteInterceptionAction<TProperty>(ref WritePropertyInterception<TProperty> interception);

public interface IWriteInterceptor : IInterceptor
{
    void WriteProperty<TProperty>(ref WritePropertyInterception<TProperty> context, WriteInterceptionAction<TProperty> next);
}