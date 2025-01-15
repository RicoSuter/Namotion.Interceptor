namespace Namotion.Interceptor;

public interface IWriteInterceptor : IInterceptor
{
    void WriteProperty(WritePropertyInterception context, Action<WritePropertyInterception> next);
}
