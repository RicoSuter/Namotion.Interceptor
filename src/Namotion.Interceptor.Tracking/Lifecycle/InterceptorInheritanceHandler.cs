namespace Namotion.Interceptor.Tracking.Lifecycle;

public class InterceptorInheritanceHandler : ILifecycleHandler
{
    public void Attach(LifecycleContext context)
    {
        if (context.ReferenceCount == 1 && context.Property is not null)
        {
            var parent = context.Property.Value.Subject;
            context.Subject.Context.AddFallbackContext(parent.Context);
        }
    }

    public void Detach(LifecycleContext context)
    {
        if (context.Property is not null)
        {
            var parent = context.Property.Value.Subject;
            context.Subject.Context.RemoveFallbackContext(parent.Context);
        }
    }

    public override bool Equals(object? obj)
    {
        return true;
    }
}
