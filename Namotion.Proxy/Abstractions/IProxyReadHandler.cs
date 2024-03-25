namespace Namotion.Proxy.Abstractions;

public interface IProxyReadHandler : IProxyHandler
{
    object? GetProperty(ReadProxyPropertyContext context, Func<ReadProxyPropertyContext, object?> next);
}

public record struct ReadProxyPropertyContext(
    ProxyPropertyReference Property,
    IProxyContext Context)
{
}
