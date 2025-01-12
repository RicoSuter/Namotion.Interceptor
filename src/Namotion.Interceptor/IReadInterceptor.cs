namespace Namotion.Interceptor;

public interface IReadInterceptor : IProxyHandler
{
    object? ReadProperty(ReadPropertyInterception context, Func<ReadPropertyInterception, object?> next);
}
