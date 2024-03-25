namespace Namotion.Proxy.Abstractions;

public interface IProxyWriteHandler : IProxyHandler
{
    void SetProperty(WriteProxyPropertyContext context, Action<WriteProxyPropertyContext> next);
}

public record struct WriteProxyPropertyContext(
    IProxyContext Context,
    ProxyPropertyReference Property,
    object? NewValue,
    Func<object?> GetValueBeforeWrite)
{
}
