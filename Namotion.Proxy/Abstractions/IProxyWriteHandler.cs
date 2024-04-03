namespace Namotion.Proxy.Abstractions;

public interface IProxyWriteHandler : IProxyHandler
{
    void WriteProperty(WriteProxyPropertyContext context, Action<WriteProxyPropertyContext> next);
}

public record struct WriteProxyPropertyContext(
    ProxyPropertyReference Property,
    object? NewValue,
    Func<object?> GetValueBeforeWrite,
    IProxyContext Context)
{
}
