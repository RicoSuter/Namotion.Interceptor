using System.Runtime.InteropServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Tells the <see cref="IChildIndexHandler"/> services of a context which children a structural
/// write left under another key or position.
/// </summary>
internal static class ChildIndexRefresher
{
    // Taken out of its slot while in use: a handler, or the Equals override of a key, can start a
    // nested structural write on this thread, which then gets buffers of its own.
    [ThreadStatic]
    private static Buffers? _reusableBuffers;

    /// <summary>
    /// Reports every subject both values hold whose keys or positions differ between them, with the first of them in
    /// the new value.
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

        var buffers = _reusableBuffers ?? new Buffers();
        _reusableBuffers = null;
        try
        {
            CollectMovedChildren(oldChildren, newChildren, oldSubjects, newSubjects, buffers);
            if (buffers.Changes.Count == 0)
            {
                return;
            }

            var span = CollectionsMarshal.AsSpan(buffers.Changes);
            for (var i = 0; i < handlers.Length; i++)
            {
                handlers[i].HandleChildIndexChanges(property, span);
            }
        }
        finally
        {
            // Release references so subjects can be GC'd on idle threads
            buffers.Changes.Clear();
            buffers.OldIndices.Clear();
            buffers.OldPlaces.Clear();
            buffers.ChangedSubjects.Clear();
            _reusableBuffers = buffers;
        }
    }

    private static void CollectMovedChildren(
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> oldChildren,
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> newChildren,
        HashSet<IInterceptorSubject> oldSubjects,
        HashSet<IInterceptorSubject> newSubjects,
        Buffers buffers)
    {
        var oldSpan = CollectionsMarshal.AsSpan(oldChildren);
        var newSpan = CollectionsMarshal.AsSpan(newChildren);

        // A subject held more than once is known by its first key or position, which a comparison
        // by place cannot tell from a later one, so every first occurrence is looked up instead.
        var holdsDuplicates = oldSubjects.Count != oldSpan.Length || newSubjects.Count != newSpan.Length;
        if (holdsDuplicates)
        {
            CollectSubjectsWithChangedPlaces(oldSpan, newSpan, buffers.OldPlaces, buffers.ChangedSubjects);
        }

        var oldIndices = buffers.OldIndices;
        for (var i = 0; i < newSpan.Length; i++)
        {
            var (subject, _, index) = newSpan[i];

            // Taking a subject out of the set is what skips its later occurrences.
            if (holdsDuplicates ? !newSubjects.Remove(subject) : IsWhereItWas(oldSpan, i, subject, index))
                continue;

            // A subject this write attached already carries its index.
            if (!oldSubjects.Contains(subject))
                continue;

            if (oldIndices.Count == 0)
            {
                MapFirstIndices(oldSpan, oldIndices);
            }

            AddIfChanged(buffers, subject, oldIndices[subject], index);
        }
    }

    private static void AddIfChanged(Buffers buffers, IInterceptorSubject subject, object? oldIndex, object? index)
    {
        if (!Equals(oldIndex, index))
        {
            buffers.Changes.Add(new SubjectChild(subject, index));
        }
        else if (buffers.ChangedSubjects.Count > 0 && buffers.ChangedSubjects.Contains(subject))
        {
            // The old instance, since an equal key counts as unchanged.
            buffers.Changes.Add(new SubjectChild(subject, oldIndex));
        }
    }

    // Finds the subjects that gained or lost a key or position, which a subject held more than once can do while its
    // first one stays. Keys and positions are unique within a value, so each identifies one occurrence.
    private static void CollectSubjectsWithChangedPlaces(
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> oldSpan,
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> newSpan,
        Dictionary<object, IInterceptorSubject> oldPlaces,
        HashSet<IInterceptorSubject> changedSubjects)
    {
        foreach (var (subject, _, index) in oldSpan)
        {
            if (index is not null)
            {
                oldPlaces.TryAdd(index, subject);
            }
        }

        foreach (var (subject, _, index) in newSpan)
        {
            if (index is not null && oldPlaces.Remove(index, out var oldSubject))
            {
                if (ReferenceEquals(oldSubject, subject))
                    continue;

                changedSubjects.Add(oldSubject);
            }

            changedSubjects.Add(subject);
        }

        foreach (var oldSubject in oldPlaces.Values)
        {
            changedSubjects.Add(oldSubject);
        }
    }

    // A copy, or a write that changes one entry without shifting the others, leaves every other
    // child where it was, which spares the lookup.
    private static bool IsWhereItWas(
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> oldSpan,
        int position, IInterceptorSubject subject, object? index)
    {
        return position < oldSpan.Length &&
               ReferenceEquals(oldSpan[position].subject, subject) &&
               Equals(oldSpan[position].index, index);
    }

    private static void MapFirstIndices(
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> oldSpan,
        Dictionary<IInterceptorSubject, object?> oldIndices)
    {
        foreach (var (subject, _, index) in oldSpan)
        {
            oldIndices.TryAdd(subject, index);
        }
    }

    private sealed class Buffers
    {
        public List<SubjectChild> Changes { get; } = [];

        public Dictionary<IInterceptorSubject, object?> OldIndices { get; } = new(ReferenceEqualityComparer.Instance);

        public Dictionary<object, IInterceptorSubject> OldPlaces { get; } = [];

        public HashSet<IInterceptorSubject> ChangedSubjects { get; } = new(ReferenceEqualityComparer.Instance);
    }
}
