namespace Namotion.Interceptor;

public interface IWriteInterceptor : IProxyHandler
{
    void WriteProperty(WritePropertyInterception context, Action<WritePropertyInterception> next);
}
