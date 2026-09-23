using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Automatically assigns or removes the parent context as fallback context to attached and detached subjects.
/// </summary>
public class ContextInheritanceHandler : ILifecycleHandler
{
    // A subject with several parents detaches through whichever parent lets go last, which need not
    // be the one it attached through, so the composed context is recorded instead of re-derived.
    private const string InheritedContextKey = "Namotion.Interceptor.Tracking.InheritedContext";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.Property.HasValue)
        {
            // Only add fallback when subject first enters the graph via property reference
            // (IsContextAttach ensures we don't add fallback for subjects already in graph via context)
            if (change is { ReferenceCount: 1, IsContextAttach: true })
            {
                var inheritedContext = change.Property.Value.Subject.Context;
                change.Subject.Data[(null, InheritedContextKey)] = inheritedContext;
                change.Subject.Context.AddFallbackContext(inheritedContext);
            }
            else if (change is { ReferenceCount: 0, IsPropertyReferenceRemoved: true })
            {
                // Composing a context by hand attaches the subject, so a subject composed onto its
                // parent before being referenced (as connectors do) has no record and relies on the
                // parent it is detached from being decomposed.
                change.Subject.Data.TryRemove((null, InheritedContextKey), out var recordedContext);
                change.Subject.Context.RemoveFallbackContext(
                    recordedContext as IInterceptorSubjectContext ?? change.Property.Value.Subject.Context);
            }
        }
    }

    public override bool Equals(object? obj)
    {
        return obj is ContextInheritanceHandler;
    }

    public override int GetHashCode() => typeof(ContextInheritanceHandler).GetHashCode();
}
