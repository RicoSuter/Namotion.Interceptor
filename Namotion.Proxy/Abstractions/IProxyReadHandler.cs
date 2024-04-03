namespace Namotion.Proxy.Abstractions;

public interface IProxyReadHandler : IProxyHandler
{
    object? ReadProperty(ReadProxyPropertyContext context, Func<ReadProxyPropertyContext, object?> next);
}

public record struct ReadProxyPropertyContext(
    ProxyPropertyReference Property,
    IProxyContext Context)
{
}
