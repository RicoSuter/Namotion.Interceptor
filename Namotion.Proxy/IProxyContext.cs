using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public interface IProxyContext
{
    IEnumerable<THandler> GetHandlers<THandler>()
        where THandler : IProxyHandler;

    object? GetProperty(IProxy proxy, string propertyName, bool isDerived, Func<object?> readValue);

    void SetProperty(IProxy proxy, string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue);
}
