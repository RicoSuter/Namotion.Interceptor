using Namotion.Interception.Lifecycle.Abstractions;
using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle.Handlers;

public class InterceptorInheritanceHandler : ILifecycleHandler
{
    public void Attach(LifecycleContext context)
    {
        if (context.ReferenceCount == 1 && context.Property is not null)
        {
            var parent = context.Property.Value.Subject;
            context.Subject.Interceptors.AddInterceptors(parent.Interceptors.Interceptors);
        }
    }

    public void Detach(LifecycleContext context)
    {
        if (context.Property is not null)
        {
            var parent = context.Property.Value.Subject;
            context.Subject.Interceptors.RemoveInterceptors(parent.Interceptors.Interceptors);
        }
    }
}
