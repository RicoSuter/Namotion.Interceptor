using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Automatically assigns or removes the parent context as fallback context to attached and detached subjects.
/// </summary>
/// <remarks>
/// A subject attached through properties inherits from the context of one subject referencing it. When that
/// subject stops referencing it while it stays attached, it inherits from another subject referencing it, or
/// from the root of the graph when each of those inherits from it in turn, as in a cycle of references, or
/// when a batch scope defers its detach. So an attached subject never inherits from a subject that left the
/// graph, and no fallback contexts form a cycle. A subject attached to several graphs inherits from the one
/// it was attached to first, and stops inheriting from it when it leaves that graph.
/// </remarks>
public class ContextInheritanceHandler : ILifecycleHandler
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.Property is not { } property)
        {
            return;
        }

        var context = change.Subject.Context;
        if (change is { ReferenceCount: 1, IsContextAttach: true })
        {
            context.AddFallbackContext(property.Subject.Context);
        }
        else if (context is InterceptorSubjectContext subjectContext)
        {
            FollowReferences(subjectContext, property.Subject.Context, in change);
        }
        else if (change is { ReferenceCount: 0, IsContextDetach: true })
        {
            context.RemoveFallbackContext(property.Subject.Context);
        }
    }

    private static void FollowReferences(
        InterceptorSubjectContext context, IInterceptorSubjectContext parentContext, in SubjectLifecycleChange change)
    {
        var inheritedContext = context.TryGetSubjectFallbackContext();
        if (inheritedContext is null)
        {
            return;
        }

        if (change.IsContextDetach)
        {
            // A subject still referenced from another graph keeps what it inherits from there.
            if (change.ReferenceCount == 0 || IsInSameGraph(inheritedContext, parentContext))
            {
                context.RemoveFallbackContext(inheritedContext);
            }
        }
        else if (change.IsPropertyReferenceRemoved)
        {
            if (ReferenceEquals(inheritedContext, parentContext))
            {
                InheritFromRemainingParent(context, inheritedContext, change.References);
            }
        }
        else if (change.EndsDeferredDetach && IsValidParent(parentContext, context))
        {
            ReplaceInheritedContext(context, inheritedContext, (InterceptorSubjectContext)parentContext);
        }
    }

    private static void InheritFromRemainingParent(
        InterceptorSubjectContext context, InterceptorSubjectContext inheritedContext, PropertyReferenceSet references)
    {
        var newParentContext = FindParentContext(context, references);
        if (newParentContext is null && TryGetInheritanceRoot(inheritedContext, context, out var rootContext))
        {
            newParentContext = rootContext;
        }

        ReplaceInheritedContext(context, inheritedContext, newParentContext);
    }

    private static void ReplaceInheritedContext(
        InterceptorSubjectContext context, InterceptorSubjectContext inheritedContext, InterceptorSubjectContext? newParentContext)
    {
        if (newParentContext is not null && !ReferenceEquals(newParentContext, inheritedContext))
        {
            context.ReplaceFallbackContext(inheritedContext, newParentContext);
        }
    }

    /// <summary>
    /// Finds the context of a subject still referencing the changed subject that does not itself inherit
    /// from it.
    /// </summary>
    private static InterceptorSubjectContext? FindParentContext(InterceptorSubjectContext context, PropertyReferenceSet references)
    {
        if (references.IsEmpty)
        {
            return null;
        }

        if (IsValidParent(references.First.Subject.Context, context))
        {
            return (InterceptorSubjectContext)references.First.Subject.Context;
        }

        if (references.Additional is { } additional)
        {
            foreach (var reference in additional)
            {
                if (IsValidParent(reference.Subject.Context, context))
                {
                    return (InterceptorSubjectContext)reference.Subject.Context;
                }
            }
        }

        return null;
    }

    private static bool IsInSameGraph(IInterceptorSubjectContext context, IInterceptorSubjectContext otherContext)
        => ReferenceEquals(context.TryGetLifecycleInterceptor(), otherContext.TryGetLifecycleInterceptor());

    private static bool IsValidParent(IInterceptorSubjectContext candidate, InterceptorSubjectContext context)
        => candidate is InterceptorSubjectContext candidateContext && TryGetInheritanceRoot(candidateContext, context, out _);

    /// <summary>
    /// Follows what <paramref name="start"/> inherits from up to the context that inherits from no subject,
    /// and returns <c>false</c> when the way leads to <paramref name="context"/> or into a cycle, since
    /// <paramref name="context"/> cannot inherit from anything on that way.
    /// </summary>
    private static bool TryGetInheritanceRoot(
        InterceptorSubjectContext start,
        InterceptorSubjectContext context,
        out InterceptorSubjectContext root)
    {
        // Two walkers at single and double speed, which meet on a cycle without tracking what they visited.
        var slow = start;
        root = start;
        while (true)
        {
            for (var step = 0; step < 2; step++)
            {
                if (ReferenceEquals(root, context))
                {
                    return false;
                }

                var next = root.TryGetSubjectFallbackContext();
                if (next is null)
                {
                    return true;
                }

                root = next;
            }

            slow = slow.TryGetSubjectFallbackContext()!;
            if (ReferenceEquals(slow, root))
            {
                return false;
            }
        }
    }

    public override bool Equals(object? obj)
    {
        return obj is ContextInheritanceHandler;
    }

    public override int GetHashCode() => typeof(ContextInheritanceHandler).GetHashCode();
}
