using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Automatically assigns or removes the parent context as fallback context to attached and detached subjects.
/// </summary>
/// <remarks>
/// Without <see cref="InterceptorSubjectContextExtensions.WithParents"/>, a subject that several parents hold keeps the
/// context of the parent it inherited from after that parent releases it, see docs/tracking.md.
/// </remarks>
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
            else if (change.IsPropertyReferenceRemoved)
            {
                InheritFromRemainingParent(change.Subject, change.Property.Value.Subject.Context);
            }
        }
    }

    /// <summary>
    /// Replaces <paramref name="releasingContext"/> as a fallback context of the subject by the context of a parent
    /// that still holds it. Reads the parents that <see cref="ParentTrackingHandler"/>, which runs before this
    /// handler, keeps.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InheritFromRemainingParent(IInterceptorSubject subject, IInterceptorSubjectContext releasingContext)
    {
        IInterceptorSubjectContext? remainingContext = null;
        foreach (var parent in subject.GetParents())
        {
            var parentSubject = parent.Property.Subject;
            if (parentSubject is null)
            {
                continue;
            }

            if (ReferenceEquals(parentSubject.Context, releasingContext))
            {
                return;
            }

            // A parent below the subject would make the contexts fall back on each other, and the subject would lose
            // the services of the graph when it is assigned there again.
            if (remainingContext is null && !IsBelow(parentSubject, subject))
            {
                remainingContext = parentSubject.Context;
            }
        }

        if (remainingContext is null)
        {
            return;
        }

        // Added before the removal, so that a write to the subject on another thread always runs the interceptors of
        // the graph. A subject that did not inherit the releasing context, such as one created with a context of its
        // own, does not keep the added one.
        var context = subject.Context;
        var isAdded = context.AddFallbackContext(remainingContext);
        if (!context.RemoveFallbackContext(releasingContext) && isAdded)
        {
            context.RemoveFallbackContext(remainingContext);
        }
    }

    /// <summary>Whether <paramref name="ancestor"/> is a parent of <paramref name="candidate"/> or of its parents.</summary>
    private static bool IsBelow(IInterceptorSubject candidate, IInterceptorSubject ancestor)
    {
        var visited = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance) { candidate };
        var pending = new Stack<IInterceptorSubject>();
        pending.Push(candidate);
        while (pending.TryPop(out var current))
        {
            foreach (var parent in current.GetParents())
            {
                var parentSubject = parent.Property.Subject;
                if (ReferenceEquals(parentSubject, ancestor))
                {
                    return true;
                }

                if (parentSubject is not null && visited.Add(parentSubject))
                {
                    pending.Push(parentSubject);
                }
            }
        }

        return false;
    }

    public override bool Equals(object? obj)
    {
        return obj is ContextInheritanceHandler;
    }

    public override int GetHashCode() => typeof(ContextInheritanceHandler).GetHashCode();
}
