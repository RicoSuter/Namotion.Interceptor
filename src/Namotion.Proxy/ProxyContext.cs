using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public class ProxyContext : IProxyContext
{
    private readonly IEnumerable<IProxyHandler> _handlers;

    private readonly IProxyReadHandler[] _readHandlers;
    private readonly IProxyWriteHandler[] _writeHandlers;

    public static ProxyContextBuilder CreateBuilder()
    {
        return new ProxyContextBuilder();
    }

    public ProxyContext(IEnumerable<IProxyHandler> handlers)
    {
        _handlers = handlers.ToArray();
        _readHandlers = _handlers.OfType<IProxyReadHandler>().Reverse().ToArray();
        _writeHandlers = _handlers.OfType<IProxyWriteHandler>().Reverse().ToArray();
    }

    public IEnumerable<THandler> GetHandlers<THandler>()
        where THandler : IProxyHandler
    {
        return _handlers.OfType<THandler>();
    }

    public object? GetProperty(IInterceptorSubject subject, string propertyName, Func<object?> readValue)
    {
        var context = new ProxyPropertyReadContext(new ProxyPropertyReference(subject, propertyName), this);

        for (int i = 0; i < _readHandlers.Length; i++)
        {
            var handler = _readHandlers[i];
            var previousReadValue = readValue;
            readValue = () =>
            {
                return handler.ReadProperty(context, ctx => previousReadValue());
            };
        }

        return readValue.Invoke();
    }

    public void SetProperty(IInterceptorSubject subject, string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue)
    {
        var context = new ProxyPropertyWriteContext(new ProxyPropertyReference(subject, propertyName), readValue(), null, IsDerived: false, this);
        context.CallWriteProperty(newValue, writeValue, _writeHandlers);
    }
}
