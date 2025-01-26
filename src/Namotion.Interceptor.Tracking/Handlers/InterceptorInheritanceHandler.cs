using Namotion.Interceptor.Tracking.Abstractions;

namespace Namotion.Interceptor.Tracking.Handlers;

public class InterceptorInheritanceHandler : ILifecycleHandler
{
    public void Attach(LifecycleContext context)
    {
        if (context.ReferenceCount == 1 && context.Property is not null)
        {
            var parent = context.Property.Value.Subject;
            context.Subject.Interceptors.AddInterceptorCollection(parent.Interceptors);
        }
    }

    public void Detach(LifecycleContext context)
    {
        if (context.Property is not null)
        {
            var parent = context.Property.Value.Subject;
            context.Subject.Interceptors.RemoveInterceptorCollection(parent.Interceptors);
        }
    }

    public override bool Equals(object? obj)
    {
        return true;
    }
}
