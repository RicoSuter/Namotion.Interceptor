using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public static class ProxyExtensions
{
    public static void SetContext(this IProxy proxy, IProxyContext context)
    {
        var currentContext = proxy.Context;
        if (currentContext != context)
        {
            if (currentContext is not null)
            {
                var registryContext = new ProxyLifecycleContext(default, null, proxy, 0, currentContext);
                foreach (var handler in currentContext.GetHandlers<IProxyLifecycleHandler>())
                {
                    handler.OnProxyDetached(registryContext);
                }
            }

            proxy.Context = context;

            if (context is not null)
            {
                var registryContext = new ProxyLifecycleContext(default, null, proxy, 1, context);
                foreach (var handler in context.GetHandlers<IProxyLifecycleHandler>())
                {
                    handler.OnProxyAttached(registryContext);
                }
            }
        }
    }

    public static void SetData(this IProxy proxy, string key, object? value)
    {
        proxy.Data[key] = value;
    }

    public static bool TryGetData(this IProxy proxy, string key, out object? value)
    {
        return proxy.Data.TryGetValue(key, out value);
    }
}
