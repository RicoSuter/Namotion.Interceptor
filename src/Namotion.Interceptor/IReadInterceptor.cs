namespace Namotion.Interceptor;

public interface IReadInterceptor : IInterceptor
{
    object? ReadProperty(ReadPropertyInterception context, Func<ReadPropertyInterception, object?> next);
}
