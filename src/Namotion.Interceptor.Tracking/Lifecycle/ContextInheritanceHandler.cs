namespace Namotion.Interceptor.Tracking.Lifecycle;

#pragma warning disable CS0659

public class ContextInheritanceHandler : ILifecycleHandler
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
        return obj is ContextInheritanceHandler;
    }
}
