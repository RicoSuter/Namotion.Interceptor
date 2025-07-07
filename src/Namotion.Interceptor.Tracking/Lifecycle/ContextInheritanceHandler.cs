namespace Namotion.Interceptor.Tracking.Lifecycle;

#pragma warning disable CS0659

/// <summary>
/// Automatically assigns or removes the parent context as fallback context to attached and detached subjects.
/// </summary>
public class ContextInheritanceHandler : ISubjectLifecycleHandler
{
    public void AttachSubject(SubjectLifecycleChange change)
    {
        if (change.ReferenceCount == 1 && change.Property is not null)
        {
            var parent = change.Property.Value.Subject;
            change.Subject.Context.AddFallbackContext(parent.Context);
        }
    }

    public void DetachSubject(SubjectLifecycleChange change)
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
