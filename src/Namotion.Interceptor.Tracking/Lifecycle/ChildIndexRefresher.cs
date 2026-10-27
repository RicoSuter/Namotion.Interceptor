using System.Runtime.InteropServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Tells the <see cref="IChildIndexHandler"/> services of a context which children a structural
/// write left under another key or position.
/// </summary>
internal static class ChildIndexRefresher
{
    [ThreadStatic]
    private static List<SubjectChild>? _reusableChanges;

    [ThreadStatic]
    private static Dictionary<IInterceptorSubject, object?>? _reusableOldIndices;

    /// <summary>
    /// Reports every subject both values hold whose first key or position differs between them.
    /// </summary>
    /// <param name="property">The written property.</param>
    /// <param name="oldChildren">The subjects the old value held, as the lifecycle interceptor scanned them.</param>
    /// <param name="newChildren">The subjects the new value holds, scanned the same way.</param>
    /// <param name="oldSubjectCount">The number of distinct subjects in <paramref name="oldChildren"/>.</param>
    /// <param name="newSubjects">The distinct subjects in <paramref name="newChildren"/>. The set is used up and must not be read afterwards.</param>
    public static void RefreshMovedChildren(
        PropertyReference property,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> oldChildren,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> newChildren,
        int oldSubjectCount,
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
            CollectMovedChildren(oldChildren, newChildren, oldSubjectCount, newSubjects, changes);
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
        int oldSubjectCount,
        HashSet<IInterceptorSubject> newSubjects,
        List<SubjectChild> changes)
    {
        var oldSpan = CollectionsMarshal.AsSpan(oldChildren);
        var newSpan = CollectionsMarshal.AsSpan(newChildren);

        // A subject held more than once is known by its first key or position, which a comparison
        // by place cannot tell from a later one, so every first occurrence is looked up instead.
        var holdsDuplicates = oldSubjectCount != oldSpan.Length || newSubjects.Count != newSpan.Length;

        // Filled on the first subject that is not where it was, and done with before any handler
        // runs, so a handler that writes another property cannot find it in use.
        Dictionary<IInterceptorSubject, object?>? oldIndices = null;
        try
        {
            for (var i = 0; i < newSpan.Length; i++)
            {
                var (subject, _, index) = newSpan[i];

                // Taking a subject out of the set is what skips its later occurrences.
                if (holdsDuplicates ? !newSubjects.Remove(subject) : IsWhereItWas(oldSpan, i, subject, index))
                    continue;

                oldIndices ??= GetOldIndices(oldSpan);

                // A subject this write attached already carries its index.
                if (oldIndices.TryGetValue(subject, out var oldIndex) && !Equals(oldIndex, index))
                {
                    changes.Add(new SubjectChild(subject, index));
                }
            }
        }
        finally
        {
            oldIndices?.Clear();
        }
    }

    // Every child of an equal copy, and every child but the edited one when an entry is appended,
    // replaced or re-keyed, is where it was, which spares the lookup.
    private static bool IsWhereItWas(
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> oldSpan,
        int position, IInterceptorSubject subject, object? index)
    {
        return position < oldSpan.Length &&
               ReferenceEquals(oldSpan[position].subject, subject) &&
               Equals(oldSpan[position].index, index);
    }

    private static Dictionary<IInterceptorSubject, object?> GetOldIndices(
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> oldSpan)
    {
        var oldIndices = _reusableOldIndices ??= new Dictionary<IInterceptorSubject, object?>(ReferenceEqualityComparer.Instance);
        foreach (var (subject, _, index) in oldSpan)
        {
            oldIndices.TryAdd(subject, index);
        }

        return oldIndices;
    }
}
