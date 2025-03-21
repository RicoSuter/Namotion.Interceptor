namespace Namotion.Interceptor.Tracking.Lifecycle;

#pragma warning disable CS0659

public class ContextInheritanceHandler : ILifecycleHandler
{
    public void Attach(SubjectLifecycleChange change)
    {
        if (change.ReferenceCount == 1 && change.Property is not null)
        {
            var parent = change.Property.Value.Subject;
            change.Subject.Context.AddFallbackContext(parent.Context);
        }
    }

    public void Detach(SubjectLifecycleChange change)
    {
        if (change.Property is not null)
        {
            var parent = change.Property.Value.Subject;
            change.Subject.Context.RemoveFallbackContext(parent.Context);
        }
    }

    public override bool Equals(object? obj)
    {
        return obj is ContextInheritanceHandler;
    }
}
