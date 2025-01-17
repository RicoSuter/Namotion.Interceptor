using Namotion.Interception.Lifecycle.Abstractions;
using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle.Handlers;

public class InterceptorInheritanceHandler : ILifecycleHandler
{
    public void Attach(LifecycleContext context)
    {
        if (context.ReferenceCount == 1 && context.Property is not null)
        {
            context.Subject.AddInterceptors(context.Property.Value.Subject.Interceptors);
        }
    }

    public void Detach(LifecycleContext context)
    {
        if (context.Property is not null)
        {
            context.Subject.RemoveInterceptors(context.Property.Value.Subject.Interceptors);
        }
    }
}
