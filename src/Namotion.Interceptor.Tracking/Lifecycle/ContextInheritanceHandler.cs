using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Automatically assigns or removes the parent context as fallback context to attached and detached subjects.
/// </summary>
public class ContextInheritanceHandler : ILifecycleHandler
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.Property.HasValue)
        {
            // Only add fallback when subject first enters the graph via property reference
            // (IsContextAttach ensures we don't add fallback for subjects already in graph via context)
            if (change is { ReferenceCount: 1, IsContextAttach: true })
            {
                change.Subject.Context.AddFallbackContext(change.Property.Value.Subject.Context);
            }
            else if (change is { ReferenceCount: 0, IsPropertyReferenceRemoved: true })
            {
                change.Subject.Context.RemoveFallbackContext(change.Property.Value.Subject.Context);
            }
            else if (change is { IsPropertyReferenceRemoved: true, IsContextDetach: false })
            {
                InheritFromRemainingParent(change.Subject, change.Property.Value.Subject, change.RemainingReferences);
            }
        }
    }

    /// <summary>
    /// Makes a subject that a parent released, while other parents still hold it, inherit the context of one of them
    /// in place of the releasing parent's context, so that it keeps the services of the graph after that parent leaves.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InheritFromRemainingParent(IInterceptorSubject subject, IInterceptorSubject releasingParent, PropertyReferenceSet remainingReferences)
    {
        if (remainingReferences.IsEmpty ||
            subject.Context is not InterceptorSubjectContext context ||
            remainingReferences.ContainsSubject(releasingParent))
        {
            return;
        }

        // Fails without a change when the subject did not inherit the releasing context, and for a parent whose
        // context falls back on the subject's own, such as a child of the subject that holds it as well.
        var releasingContext = releasingParent.Context;
        var additionalReferences = remainingReferences.Additional;
        if (context.ReplaceFallbackContext(releasingContext, remainingReferences.First.Subject.Context) || additionalReferences is null)
        {
            return;
        }

        foreach (var reference in additionalReferences)
        {
            if (context.ReplaceFallbackContext(releasingContext, reference.Subject.Context))
            {
                return;
            }
        }
    }

    public override bool Equals(object? obj)
    {
        return obj is ContextInheritanceHandler;
    }

    public override int GetHashCode() => typeof(ContextInheritanceHandler).GetHashCode();
}
