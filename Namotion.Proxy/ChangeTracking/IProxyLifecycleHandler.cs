using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking;

internal interface IProxyLifecycleHandler2 : IProxyHandler
{
    void AttachProxyGraph(IProxyContext context, IProxy proxy);

    void DetachProxyGraph(IProxyContext context, IProxy proxy);
}