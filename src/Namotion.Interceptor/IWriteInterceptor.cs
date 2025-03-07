namespace Namotion.Interceptor;

public interface IWriteInterceptor : IInterceptor
{
    object? WriteProperty(WritePropertyInterception context, Func<WritePropertyInterception, object?> next);
}