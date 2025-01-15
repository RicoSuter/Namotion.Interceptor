using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Lifecycle;

internal class AutomaticContextAssignmentHandler : IProxyLifecycleHandler
{
    private readonly IProxyContext _context;

    public AutomaticContextAssignmentHandler(IProxyContext context)
    {
        _context = context;
    }
    
    public void OnProxyAttached(ProxyLifecycleContext context)
    {
        if (context.ReferenceCount == 1)
        {
            context.Proxy.Interceptor = _context;
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
