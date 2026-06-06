namespace Namotion.Interceptor.Tracking.Lifecycle;

#pragma warning disable CS0659

/// <summary>
/// Automatically assigns or removes the parent context as fallback context to attached and detached subjects.
/// On detach, also removes all ancestor contexts that were reachable through the parent's fallback
/// chain at attach time, fully disconnecting the subject from the tree it was part of.
/// Independently added fallback contexts (not in the parent's chain) are preserved.
/// </summary>
public class ContextInheritanceHandler : ILifecycleHandler
{
    private Dictionary<IInterceptorSubject, IInterceptorSubjectContext[]>? _ancestorSnapshots;

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.Property.HasValue)
        {
            if (change is { ReferenceCount: 1, IsContextAttach: true })
            {
                change.Subject.Context.AddFallbackContext(change.Property.Value.Subject.Context);
            }

            if (change is { ReferenceCount: 1, IsPropertyReferenceAdded: true })
            {
                SnapshotAncestors(change.Subject, change.Property.Value.Subject.Context);
            }
            else if (change is { ReferenceCount: 0, IsPropertyReferenceRemoved: true })
            {
                var parentContext = change.Property.Value.Subject.Context;
                change.Subject.Context.RemoveFallbackContext(parentContext);

                if (_ancestorSnapshots is not null &&
                    _ancestorSnapshots.Remove(change.Subject, out var ancestors))
                {
                    var context = (InterceptorSubjectContext)change.Subject.Context;
                    for (var i = 0; i < ancestors.Length; i++)
                    {
                        context.RemoveFallbackContextDirect(ancestors[i]);
                    }
                }
            }
        }
    }

    private void SnapshotAncestors(IInterceptorSubject subject, IInterceptorSubjectContext parentContext)
    {
        var ancestors = CollectAllAncestors(parentContext);
        if (ancestors.Length > 0)
        {
            _ancestorSnapshots ??= new Dictionary<IInterceptorSubject, IInterceptorSubjectContext[]>();
            _ancestorSnapshots[subject] = ancestors;
        }
    }

    private static IInterceptorSubjectContext[] CollectAllAncestors(IInterceptorSubjectContext context)
    {
        HashSet<IInterceptorSubjectContext>? visited = null;
        CollectAncestorsRecursive(context, ref visited);
        return visited is not null ? [.. visited] : [];
    }

    private static void CollectAncestorsRecursive(
        IInterceptorSubjectContext context,
        ref HashSet<IInterceptorSubjectContext>? visited)
    {
        var fallbacks = context.GetFallbackContexts();
        if (fallbacks.Count == 0)
            return;

        visited ??= new HashSet<IInterceptorSubjectContext>();
        foreach (var fallback in fallbacks)
        {
            if (visited!.Add(fallback))
            {
                CollectAncestorsRecursive(fallback, ref visited);
            }
        }
    }

    public override bool Equals(object? obj)
    {
        return obj is ContextInheritanceHandler;
    }
}
