namespace Namotion.Interceptor.Tracking.Lifecycle;

#pragma warning disable CS0659

public class ContextInheritanceHandler : ILifecycleHandler
{
    public void Attach(SubjectLifecycleUpdate update)
    {
        if (update.ReferenceCount == 1 && update.Property is not null)
        {
            var parent = update.Property.Value.Subject;
            update.Subject.Context.AddFallbackContext(parent.Context);
        }
    }

    public void Detach(SubjectLifecycleUpdate update)
    {
        if (update.Property is not null)
        {
            var parent = update.Property.Value.Subject;
            update.Subject.Context.RemoveFallbackContext(parent.Context);
        }
    }

    public override bool Equals(object? obj)
    {
        return obj is ContextInheritanceHandler;
    }
}
