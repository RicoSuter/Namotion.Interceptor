namespace Namotion.Interceptor.Tracking.Lifecycle;

#pragma warning disable CS0659

/// <summary>
/// Automatically assigns or removes the parent context as fallback context to attached and detached subjects.
/// </summary>
public class ContextInheritanceHandler : IReferenceLifecycleHandler
{
    public void OnSubjectAttachedToProperty(SubjectLifecycleChange change)
    {
        // Add context for first property reference
        if (change.ReferenceCount == 1)
        {
            var parent = change.Property!.Value.Subject;
            change.Subject.Context.AddFallbackContext(parent.Context);
        }
    }

    public void OnSubjectDetachedFromProperty(SubjectLifecycleChange change)
    {
        // Remove context inheritance when last property reference is removed
        if (change.ReferenceCount == 0)
        {
            var parent = change.Property!.Value.Subject;
            change.Subject.Context.RemoveFallbackContext(parent.Context);
        }
    }

    public override bool Equals(object? obj)
    {
        return obj is ContextInheritanceHandler;
    }
}
