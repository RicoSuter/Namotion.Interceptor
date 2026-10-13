using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Tells the <see cref="IChildIndexHandler"/> services of a context which children a structural
/// write left under another key or position.
/// </summary>
internal static class ChildIndexRefresher
{
    /// <summary>
    /// Refreshes every subject of the new value that the old value did not hold at the same place
    /// under an equal index.
    /// </summary>
    /// <param name="context">The context of the subject that owns the written property.</param>
    /// <param name="oldChildren">The subjects the old value held, as the lifecycle interceptor scanned them.</param>
    /// <param name="newChildren">The subjects the new value holds, scanned the same way.</param>
    /// <param name="oldSubjectCount">The number of distinct subjects in <paramref name="oldChildren"/>.</param>
    /// <param name="newSubjects">The distinct subjects in <paramref name="newChildren"/>. The set is used up and must not be read afterwards.</param>
    public static void RefreshMovedChildren(
        IInterceptorSubjectContext context,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> oldChildren,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> newChildren,
        int oldSubjectCount,
        HashSet<IInterceptorSubject> newSubjects)
    {
        var handlers = context.GetServices<IChildIndexHandler>();
        if (handlers.Length == 0)
        {
            return;
        }

        var oldSpan = CollectionsMarshal.AsSpan(oldChildren);
        var newSpan = CollectionsMarshal.AsSpan(newChildren);

        if (oldSubjectCount != oldSpan.Length || newSubjects.Count != newSpan.Length)
        {
            // A subject held more than once is known by its first key or position, which the
            // comparison below cannot tell from a later one. Each subject is refreshed with its
            // first one instead, and taking it out of the set is what skips the later ones.
            for (var i = 0; i < newSpan.Length; i++)
            {
                if (newSubjects.Remove(newSpan[i].subject))
                {
                    Refresh(handlers, in newSpan[i]);
                }
            }

            return;
        }

        for (var i = 0; i < newSpan.Length; i++)
        {
            ref readonly var child = ref newSpan[i];

            // Unmoved when the old value enumerates the same subject at the same place under an
            // equal index. That is every child of an equal copy, and every child but the edited
            // one when an entry is appended, replaced or re-keyed. The old value is read as it is
            // now, so one that was mutated in place since it was assigned hides the moves it
            // shares with the new value.
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
