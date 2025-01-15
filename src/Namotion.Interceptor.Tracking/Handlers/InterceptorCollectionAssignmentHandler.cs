using Namotion.Interception.Lifecycle.Abstractions;
using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle.Handlers;

public class InterceptorCollectionAssignmentHandler : IProxyLifecycleHandler
{
    private readonly IInterceptorCollection _interceptors;

    public InterceptorCollectionAssignmentHandler(IInterceptorCollection interceptors)
    {
        _interceptors = interceptors;
    }
    
    public void OnProxyAttached(ProxyLifecycleContext context)
    {
        if (context.ReferenceCount == 1)
        {
            context.Proxy.Interceptor = _interceptors;
        }
    }

    public void OnProxyDetached(ProxyLifecycleContext context)
    {
        if (context.ReferenceCount == 0)
        {
            context.Proxy.Interceptor = null;
        }
    }
}
