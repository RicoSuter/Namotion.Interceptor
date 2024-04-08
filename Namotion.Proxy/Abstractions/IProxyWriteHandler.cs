namespace Namotion.Proxy.Abstractions;

public interface IProxyWriteHandler : IProxyHandler
{
    void WriteProperty(WriteProxyPropertyContext context, Action<WriteProxyPropertyContext> next);
}
