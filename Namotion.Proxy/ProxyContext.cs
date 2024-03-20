using Namotion.Proxy.Handlers;

namespace Namotion.Proxy;

public class ProxyContext : IProxyContext
{
    private readonly IEnumerable<IProxyHandler> _handlers;

    public ProxyContext(IEnumerable<IProxyHandler> handlers)
    {
        _handlers = handlers;
    }

    public void RegisterProxy(IProxy proxy)
    {
        proxy.Context = this;
    }

    public IEnumerable<THandler> GetHandlers<THandler>()
        where THandler : IProxyHandler
    {
        return _handlers.OfType<THandler>();
    }

    public object? GetProperty(IProxy proxy, string propertyName, Func<object?> readValue)
    {
        var context = new ProxyReadHandlerContext(this, proxy, propertyName);

        foreach (var handler in GetHandlers<IProxyReadHandler>().Reverse())
        {
            var previousReadValue = readValue;
            readValue = () =>
            {
                return handler.GetProperty(context, ctx => previousReadValue());
            };
        }

        return readValue.Invoke();
    }

    public void SetProperty(IProxy proxy, string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue)
    {
        var context = new ProxyWriteHandlerContext(this, proxy, propertyName, null, readValue);

        foreach (var handler in GetHandlers<IProxyWriteHandler>().Reverse())
        {
            var previousWriteValue = writeValue;
            writeValue = (value) =>
            {
                handler.SetProperty(context with { NewValue = value }, ctx => previousWriteValue(ctx.NewValue));
            };
        }

        writeValue.Invoke(newValue);
    }
}
