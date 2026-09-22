using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Automatically assigns or removes the parent context as fallback context to attached and detached subjects.
/// </summary>
public class ContextInheritanceHandler : ILifecycleHandler
{
    // Remembered per subject because the parent that pulls a subject into the graph is not
    // necessarily the parent whose removal takes it out again: a subject with more than one parent
    // leaves through whichever edge goes last, and decomposing that last parent's context would
    // match nothing, leaving the subject resolving the graph's services after its detach.
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
                // Composing a context by hand attaches the subject to it, so a subject composed onto
                // its parent before it was referenced never takes the branch above and has no record.
                // Connector code does exactly that and relies on the parent being decomposed here.
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
