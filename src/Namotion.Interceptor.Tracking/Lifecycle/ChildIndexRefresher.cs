using System.Runtime.CompilerServices;
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

    private const int AllPlacesMatched = -1;
    private const int PlacesDiffer = -2;

    /// <summary>
    /// Reports every subject both values hold whose keys or positions differ between them, with the first of them in
    /// the new value.
    /// </summary>
    /// <param name="property">The written property.</param>
    /// <param name="oldChildren">The subjects the old value held, as the lifecycle interceptor scanned them.</param>
    /// <param name="newChildren">The subjects the new value holds, scanned the same way.</param>
    /// <param name="oldSubjects">The distinct subjects in <paramref name="oldChildren"/>.</param>
    /// <param name="newSubjects">The distinct subjects in <paramref name="newChildren"/>.</param>
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
            buffers.Cursors.Clear();
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
        // by place cannot tell from a later one, and its later ones can change while the first stays.
        if (oldSubjects.Count != oldSpan.Length || newSubjects.Count != newSpan.Length)
        {
            CollectSubjectsWithChangedPlaces(oldSpan, newSpan, buffers);
            return;
        }

        var oldIndices = buffers.OldIndices;
        for (var i = 0; i < newSpan.Length; i++)
        {
            var (subject, _, index) = newSpan[i];
            if (IsWhereItWas(oldSpan, i, subject, index))
                continue;

            // A subject this write attached already carries its index.
            if (!oldSubjects.Contains(subject))
                continue;

            if (oldIndices.Count == 0)
            {
                MapFirstIndices(oldSpan, oldIndices);
            }

            if (!Equals(oldIndices[subject], index))
            {
                buffers.Changes.Add(new SubjectChild(subject, index));
            }
        }
    }

    // Compares the keys or positions of each kept subject in order, the n-th in the new value with the n-th in the old
    // one, and reports the subject with its first one when they differ.
    private static void CollectSubjectsWithChangedPlaces(
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> oldSpan,
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> newSpan,
        Buffers buffers)
    {
        // Each subject of the old value has a cursor on its next place in oldSpan to compare, and nextPlaces links
        // each place to the next one of the same subject.
        var cursors = buffers.Cursors;
        var nextPlaces = buffers.NextPlaces;
        CollectionsMarshal.SetCount(nextPlaces, oldSpan.Length);
        var next = CollectionsMarshal.AsSpan(nextPlaces);
        for (var i = oldSpan.Length - 1; i >= 0; i--)
        {
            ref var cursor = ref CollectionsMarshal.GetValueRefOrAddDefault(cursors, oldSpan[i].subject, out var exists);
            next[i] = exists ? cursor : AllPlacesMatched;
            cursor = i;
        }

        // A subject this write attached has no cursor, since its attach carries its index.
        foreach (var (subject, _, index) in newSpan)
        {
            ref var cursor = ref CollectionsMarshal.GetValueRefOrNullRef(cursors, subject);
            if (!Unsafe.IsNullRef(ref cursor))
            {
                cursor = cursor >= 0 && Equals(oldSpan[cursor].index, index) ? next[cursor] : PlacesDiffer;
            }
        }

        // Each subject whose places differed, or whose cursor is still on a place the new value lost, is reported once,
        // at its first place.
        foreach (var (subject, _, index) in newSpan)
        {
            ref var cursor = ref CollectionsMarshal.GetValueRefOrNullRef(cursors, subject);
            if (!Unsafe.IsNullRef(ref cursor) && cursor != AllPlacesMatched)
            {
                buffers.Changes.Add(new SubjectChild(subject, index));
                cursor = AllPlacesMatched;
            }
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

        public Dictionary<IInterceptorSubject, int> Cursors { get; } = new(ReferenceEqualityComparer.Instance);

        public List<int> NextPlaces { get; } = [];
    }
}
