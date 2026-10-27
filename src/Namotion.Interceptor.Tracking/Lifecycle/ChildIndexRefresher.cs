using System.Runtime.InteropServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Tells the <see cref="IChildIndexHandler"/> services of a context which children a structural
/// write left under another key or position.
/// </summary>
internal static class ChildIndexRefresher
{
    [ThreadStatic]
    private static List<SubjectChildIndex>? _reusableChanges;

    /// <summary>
    /// Reports every subject both values hold that the old value did not hold at the same place
    /// under an equal index.
    /// </summary>
    /// <param name="property">The written property.</param>
    /// <param name="oldChildren">The subjects the old value held, as the lifecycle interceptor scanned them.</param>
    /// <param name="newChildren">The subjects the new value holds, scanned the same way.</param>
    /// <param name="oldSubjects">The distinct subjects in <paramref name="oldChildren"/>.</param>
    /// <param name="newSubjects">The distinct subjects in <paramref name="newChildren"/>. The set is used up and must not be read afterwards.</param>
    public static void RefreshMovedChildren(
        PropertyReference property,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> oldChildren,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> newChildren,
        HashSet<IInterceptorSubject> oldSubjects,
        HashSet<IInterceptorSubject> newSubjects)
    {
        var handlers = property.Subject.Context.GetServices<IChildIndexHandler>();
        if (handlers.Length == 0)
        {
            return;
        }

        // Taken out of its slot while in use, so that a handler which writes another structural
        // property gets a list of its own.
        var changes = _reusableChanges ?? [];
        _reusableChanges = null;
        try
        {
            CollectMovedChildren(oldChildren, newChildren, oldSubjects, newSubjects, changes);
            if (changes.Count == 0)
            {
                return;
            }

            var span = CollectionsMarshal.AsSpan(changes);
            for (var i = 0; i < handlers.Length; i++)
            {
                handlers[i].HandleChildIndexChanges(property, span);
            }
        }
        finally
        {
            changes.Clear();
            _reusableChanges = changes;
        }
    }

    private static void CollectMovedChildren(
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> oldChildren,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> newChildren,
        HashSet<IInterceptorSubject> oldSubjects,
        HashSet<IInterceptorSubject> newSubjects,
        List<SubjectChildIndex> changes)
    {
        var oldSpan = CollectionsMarshal.AsSpan(oldChildren);
        var newSpan = CollectionsMarshal.AsSpan(newChildren);

        if (oldSubjects.Count != oldSpan.Length || newSubjects.Count != newSpan.Length)
        {
            CollectFirstOccurrences(newSpan, oldSubjects, newSubjects, changes);
            return;
        }

        for (var i = 0; i < newSpan.Length; i++)
        {
            var (subject, _, index) = newSpan[i];

            // Unmoved when the old value enumerates the same subject at the same place under an
            // equal index. That is every child of an equal copy, and every child but the edited
            // one when an entry is appended, replaced or re-keyed. The old value is read as it is
            // now, so one that was mutated in place since it was assigned hides the moves it
            // shares with the new value.
            if (i < oldSpan.Length &&
                ReferenceEquals(oldSpan[i].subject, subject) &&
                Equals(oldSpan[i].index, index))
            {
                continue;
            }

            // A subject this write attached already carries its index.
            if (oldSubjects.Contains(subject))
            {
                changes.Add(new SubjectChildIndex(subject, index));
            }
        }
    }

    private static void CollectFirstOccurrences(
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> newSpan,
        HashSet<IInterceptorSubject> oldSubjects,
        HashSet<IInterceptorSubject> newSubjects,
        List<SubjectChildIndex> changes)
    {
        // A subject held more than once is known by its first key or position, which a comparison
        // by place cannot tell from a later one. Each kept subject is reported with its first one
        // instead, and taking it out of the set is what skips the later ones.
        for (var i = 0; i < newSpan.Length; i++)
        {
            var (subject, _, index) = newSpan[i];
            if (newSubjects.Remove(subject) && oldSubjects.Contains(subject))
            {
                changes.Add(new SubjectChildIndex(subject, index));
            }
        }
    }
}
