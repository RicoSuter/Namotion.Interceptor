using Namotion.Interception.Lifecycle.Abstractions;
using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle.Handlers;

public class AssignInterceptorsHandler : ILifecycleHandler
{
    public void AddChild(LifecycleContext context)
    {
        if (context is { ReferenceCount: 1, Property.Subject.Interceptors: not null })
        {
            context.Subject.AddInterceptors(context.Property.Subject.Interceptors);
        }
    }

    public void RemoveChild(LifecycleContext context)
    {
        if (context is { ReferenceCount: 0, Property.Subject.Interceptors: not null })
        {
            context.Subject.RemoveInterceptors(context.Property.Subject.Interceptors);
        }
    }
}
