using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public class ProxyContext : IProxyContext
{
    private readonly IEnumerable<IProxyHandler> _handlers;

    public static ProxyContextBuilder CreateBuilder()
    {
        return new ProxyContextBuilder();
    }

    public ProxyContext(IEnumerable<IProxyHandler> handlers)
    {
        _handlers = handlers;
    }

    public IEnumerable<THandler> GetHandlers<THandler>()
        where THandler : IProxyHandler
    {
        return _handlers.OfType<THandler>();
    }

    public object? GetProperty(IProxy proxy, string propertyName, Func<object?> readValue)
    {
        var context = new ReadProxyPropertyContext(new ProxyPropertyReference(proxy, propertyName), this);

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
        var context = new WriteProxyPropertyContext(new ProxyPropertyReference(proxy, propertyName), null, GetReadValueFunctionWithCache(readValue), this);

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

    private static Func<object?> GetReadValueFunctionWithCache(Func<object?> readValue)
    {
        // TODO: do we need a lock?
        var isRead = false;
        object? previousValue = null;
        return () =>
        {
            if (isRead == false)
            {
                previousValue = readValue();
                isRead = true;
            }
            return previousValue;
        };
    }
}
