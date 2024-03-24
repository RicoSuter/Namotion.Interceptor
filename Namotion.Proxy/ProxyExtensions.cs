using Namotion.Proxy.ChangeTracking;

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
                foreach (var handler in currentContext.GetHandlers<IProxyLifecycleHandler2>())
                {
                    handler.DetachProxyGraph(currentContext, proxy);
                }
            }

            proxy.Context = context;

            if (context is not null)
            {
                foreach (var handler in context.GetHandlers<IProxyLifecycleHandler2>())
                {
                    handler.AttachProxyGraph(context, proxy);
                }
            }
        }
    }
}
