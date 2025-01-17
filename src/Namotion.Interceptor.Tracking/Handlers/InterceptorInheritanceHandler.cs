using Namotion.Interception.Lifecycle.Abstractions;
using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle.Handlers;

public class InterceptorInheritanceHandler : ILifecycleHandler
{
    public void AddChild(LifecycleContext context)
    {
        if (context.ReferenceCount == 1)
        {
            context.Subject.AddInterceptors(context.Property.Subject.Interceptors);
        }
    }

    public void RemoveChild(LifecycleContext context)
    {
        context.Subject.RemoveInterceptors(context.Property.Subject.Interceptors);
    }
}
