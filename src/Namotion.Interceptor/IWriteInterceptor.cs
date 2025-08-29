namespace Namotion.Interceptor;

public delegate void WriteInterceptionAction<TProperty>(ref WritePropertyInterception<TProperty> interception);

public interface IWriteInterceptor
{
    void WriteProperty<TProperty>(ref WritePropertyInterception<TProperty> context, WriteInterceptionAction<TProperty> next);
}