using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Decides which sources a branch-scoped wait must observe.
/// </summary>
internal static class SourceScope
{
    /// <summary>
    /// True when the source's root and the anchor lie on the same root-to-leaf path, in either
    /// direction: the source is rooted above the anchor and may claim into it, or rooted inside it.
    /// A source on a sibling branch is in neither set, which is what stops an unrelated failing
    /// connection from blocking a wait.
    /// </summary>
    internal static bool IsInScope(ISubjectSource source, IInterceptorSubject anchor)
    {
        var sourceRoot = source.RootSubject;
        return IsAncestorOrSelf(sourceRoot, anchor) || IsAncestorOrSelf(anchor, sourceRoot);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="target"/> or reachable by walking
    /// up from it through tracked parents.
    /// </summary>
    /// <remarks>
    /// Nothing enforces that the parent graph is acyclic: two subjects can reference each other
    /// (directly or through a longer chain), so the walk always tracks visited subjects and cannot
    /// loop, even on a single-parent chain.
    /// </remarks>
    internal static bool IsAncestorOrSelf(IInterceptorSubject candidate, IInterceptorSubject target)
    {
        if (ReferenceEquals(candidate, target))
        {
            return true;
        }

        return SearchGraph(candidate, target);
    }

    private static bool SearchGraph(IInterceptorSubject candidate, IInterceptorSubject start)
    {
        var visited = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<IInterceptorSubject>();
        pending.Push(start);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (ReferenceEquals(current, candidate))
            {
                return true;
            }

            foreach (var parent in current.GetParents())
            {
                pending.Push(parent.Property.Subject);
            }
        }

        return false;
    }
}
