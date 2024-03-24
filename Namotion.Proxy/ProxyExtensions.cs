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
                currentContext.GetHandlers<IProxyLifecycleHandler2>().Single()
                    .DetachProxyGraph(currentContext, proxy);
            }

            proxy.Context = context;

            if (context is not null)
            {
                context.GetHandlers<IProxyLifecycleHandler2>().Single()
                    .AttachProxyGraph(context, proxy);
            }
        }
    }
}
