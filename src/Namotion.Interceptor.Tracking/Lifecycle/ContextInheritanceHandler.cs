using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

#pragma warning disable CS0659

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
            // A batch scope defers the last detach, so a subject moved between structural properties within
            // one update re-attaches without IsContextAttach and its deferred detach is never processed:
            // neither predicate below sees the move. Follow it here instead, with a swap rather than a
            // remove and an add, because removing a fallback context detaches the subject and its children
            // and the subject is attached again at this point. A move within one parent needs no change.
            else if (change.MovedFromProperty is { } movedFromProperty &&
                     change.Subject.Context is InterceptorSubjectContext subjectContext)
            {
                var currentParentContext = change.Property.Value.Subject.Context;
                var previousParentContext = movedFromProperty.Subject.Context;
                if (!ReferenceEquals(currentParentContext, previousParentContext))
                {
                    subjectContext.ReplaceFallbackContext(previousParentContext, currentParentContext);
                }
            }
            // Keyed off IsContextDetach, not ReferenceCount: under a batch scope the count can be
            // transiently 0 while last-detach processing is deferred, and the scope stamps
            // IsContextDetach only once the detach actually lands. That keeps the inherited context
            // alive while a subject moves between structural properties within one update.
            else if (change is { IsContextDetach: true, IsPropertyReferenceRemoved: true })
            {
                change.Subject.Context.RemoveFallbackContext(change.Property.Value.Subject.Context);
            }
        }
    }

    public override bool Equals(object? obj)
    {
        return obj is ContextInheritanceHandler;
    }
}
