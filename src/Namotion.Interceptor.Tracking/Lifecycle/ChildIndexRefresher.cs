using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Tells the <see cref="IChildIndexHandler"/> services of a context which children a structural
/// write left under another key or position.
/// </summary>
internal static class ChildIndexRefresher
{
    /// <param name="context">The context of the subject that owns the written property.</param>
    /// <param name="oldChildren">The subjects the old value held, as the lifecycle interceptor scanned them.</param>
    /// <param name="newChildren">The subjects the new value holds, scanned the same way.</param>
    /// <param name="oldSubjectCount">The number of distinct subjects in <paramref name="oldChildren"/>.</param>
    /// <param name="newSubjectCount">The number of distinct subjects in <paramref name="newChildren"/>.</param>
    public static void RefreshMovedChildren(
        IInterceptorSubjectContext context,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> oldChildren,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> newChildren,
        int oldSubjectCount,
        int newSubjectCount)
    {
        var handlers = context.GetServices<IChildIndexHandler>();
        if (handlers.Length == 0)
        {
            return;
        }

        var oldSpan = CollectionsMarshal.AsSpan(oldChildren);
        var newSpan = CollectionsMarshal.AsSpan(newChildren);

        if (oldSubjectCount != oldSpan.Length || newSubjectCount != newSpan.Length)
        {
            // A subject held more than once has the index of its first key or position, which
            // the comparison below cannot tell from a later one. Backwards, so that the first
            // one is refreshed last.
            for (var i = newSpan.Length - 1; i >= 0; i--)
            {
                Refresh(handlers, in newSpan[i]);
            }

            return;
        }

        for (var i = 0; i < newSpan.Length; i++)
        {
            ref readonly var child = ref newSpan[i];

            // Unmoved when the old value enumerated the same subject at the same place under an
            // equal index. That is every child of an equal copy, and every child but the edited
            // one when an entry is appended, replaced or re-keyed.
            if (i < oldSpan.Length &&
                ReferenceEquals(oldSpan[i].subject, child.subject) &&
                Equals(oldSpan[i].index, child.index))
            {
                continue;
            }

            Refresh(handlers, in child);
        }
    }

    private static void Refresh(
        ImmutableArray<IChildIndexHandler> handlers,
        in (IInterceptorSubject subject, PropertyReference property, object? index) child)
    {
        for (var i = 0; i < handlers.Length; i++)
        {
            handlers[i].RefreshChildIndex(child.subject, child.property, child.index);
        }
    }
}
