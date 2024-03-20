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
                var registryContext = new ProxyPropertyRegistryHandlerContext(currentContext, proxy);
                foreach (var handler in context.GetHandlers<IProxyPropertyRegistryHandler>())
                {
                    handler.DetachProxy(registryContext, proxy);
                }
            }

            proxy.Context = context;

            if (context is not null)
            {
                var registryContext = new ProxyPropertyRegistryHandlerContext(context, proxy);
                foreach (var handler in context.GetHandlers<IProxyPropertyRegistryHandler>())
                {
                    handler.AttachProxy(registryContext, proxy);
                }
            }
        }
    }
}
