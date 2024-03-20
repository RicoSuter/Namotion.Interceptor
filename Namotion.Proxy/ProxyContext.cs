using Namotion.Proxy.Handlers;

namespace Namotion.Proxy;

public class ProxyContext : IProxyContext, IProxyContextProvider
{
    private readonly IEnumerable<IProxyHandler> _handlers;

    IProxyContext IProxyContextProvider.Context => this;

    public ProxyContext(IEnumerable<IProxyHandler> handlers)
    {
        _handlers = handlers;
    }

    public object? GetProperty(object proxy, string propertyName, Func<object?> readValue)
    {
        var context = new ProxyReadHandlerContext(this, proxy, propertyName);

        foreach (var handler in _handlers
            .OfType<IProxyReadHandler>()
            .Reverse())
        {
            var previousReadValue = readValue;
            readValue = () =>
            {
                return handler.GetProperty(context, ctx => previousReadValue());
            };
        }

        return readValue.Invoke();
    }

    public void SetProperty(object proxy, string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue)
    {
        var context = new ProxyWriteHandlerContext(this, proxy, propertyName, newValue, readValue);
        
        foreach (var handler in _handlers
            .OfType<IProxyWriteHandler>()
            .Reverse())
        {
            var previousWriteValue = writeValue;
            writeValue = (value) =>
            {
                handler.SetProperty(context, ctx => previousWriteValue(ctx.NewValue));
            };
        }

        writeValue.Invoke(newValue);
    }
}
