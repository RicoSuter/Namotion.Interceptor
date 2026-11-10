using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Tells the <see cref="IChildIndexHandler"/> services of a context which children a structural
/// write left under other keys or positions, and which children an attach placed at more than one.
/// </summary>
internal static class ChildIndexRefresher
{
    // Taken out of its slot while in use: a handler, or the Equals override of a key, can start a
    // nested structural write on this thread, which then gets buffers of its own.
    [ThreadStatic]
    private static Buffers? _reusableBuffers;

    private const int AllPlacesMatched = -1;
    private const int PlacesDiffer = -2;
    private const int AttachedOnce = -3;
    private const int NoNextPlace = -1;

    /// <summary>
    /// Reports every subject both values hold whose keys or positions differ between them, and every subject the new
    /// value holds more than once that the write attached, with all of its keys or positions in the new value.
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
            ReportChangedChildren(property, buffers.Changes, handlers);
        }
        finally
        {
            ReturnBuffers(buffers);
        }
    }

    /// <summary>
    /// Reports every subject that the attach of a subject's properties attached at more than one key or position of a
    /// property, with all of them, since its attach carries only the first.
    /// </summary>
    /// <param name="children">The subjects the properties hold, as the lifecycle interceptor scanned them, grouped by property.</param>
    public static void RefreshRepeatedChildren(
        List<(IInterceptorSubject subject, PropertyReference property, object? index)> children)
    {
        var span = CollectionsMarshal.AsSpan(children);
        var handlers = span[0].property.Subject.Context.GetServices<IChildIndexHandler>();
        if (handlers.Length == 0)
        {
            return;
        }

        var buffers = _reusableBuffers ?? new Buffers();
        _reusableBuffers = null;
        try
        {
            var start = 0;
            while (start < span.Length)
            {
                var property = span[start].property;
                var end = start + 1;
                while (end < span.Length && span[end].property.Equals(property))
                {
                    end++;
                }

                if (end - start > 1)
                {
                    CollectSubjectsWithChangedPlaces([], span[start..end], buffers);
                    ReportChangedChildren(property, buffers.Changes, handlers);
                    buffers.Changes.Clear();
                    buffers.Cursors.Clear();
                }

                start = end;
            }
        }
        finally
        {
            ReturnBuffers(buffers);
        }
    }

    // Hands the children whose places changed, if any, to every handler.
    private static void ReportChangedChildren(
        PropertyReference property, List<SubjectChild> changedChildren, ImmutableArray<IChildIndexHandler> handlers)
    {
        if (changedChildren.Count == 0)
        {
            return;
        }

        var span = CollectionsMarshal.AsSpan(changedChildren);
        for (var i = 0; i < handlers.Length; i++)
        {
            handlers[i].HandleChildIndexChanges(property, span);
        }
    }

    private static void ReturnBuffers(Buffers buffers)
    {
        // Release references so subjects can be GC'd on idle threads
        buffers.Changes.Clear();
        buffers.OldIndices.Clear();
        buffers.Cursors.Clear();
        _reusableBuffers = buffers;
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

        // A subject held more than once has several places, which a comparison of first places cannot follow.
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
    // one, and reports each subject whose places differ, or which this write attached at more than one place, with all
    // of its places in the new value as one run, at its first place.
    private static void CollectSubjectsWithChangedPlaces(
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> oldSpan,
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> newSpan,
        Buffers buffers)
    {
        // The links of the old places, then those of the new places.
        var nextPlaces = buffers.NextPlaces;
        CollectionsMarshal.SetCount(nextPlaces, oldSpan.Length + newSpan.Length);
        var next = CollectionsMarshal.AsSpan(nextPlaces)[..oldSpan.Length];
        var nextNew = CollectionsMarshal.AsSpan(nextPlaces)[oldSpan.Length..];

        LinkOldPlaces(oldSpan, next, buffers.Cursors);
        MatchNewPlaces(oldSpan, newSpan, next, nextNew, buffers.Cursors);
        AddChangedSubjects(newSpan, nextNew, buffers);
    }

    // Each subject of the old value gets a cursor on its next place in oldSpan to compare, and next links
    // each place to the next one of the same subject.
    private static void LinkOldPlaces(
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> oldSpan,
        Span<int> next,
        Dictionary<IInterceptorSubject, PlaceCursor> cursors)
    {
        for (var i = oldSpan.Length - 1; i >= 0; i--)
        {
            ref var cursor = ref CollectionsMarshal.GetValueRefOrAddDefault(cursors, oldSpan[i].subject, out var exists);
            next[i] = exists ? cursor.Next : AllPlacesMatched;
            cursor.Next = i;
            cursor.LastNewPlace = NoNextPlace;
        }
    }

    // Advances the cursor of each subject along its places in the new value, and links each of them to the next one of
    // the same subject in nextNew, for the report.
    private static void MatchNewPlaces(
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> oldSpan,
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> newSpan,
        ReadOnlySpan<int> next,
        Span<int> nextNew,
        Dictionary<IInterceptorSubject, PlaceCursor> cursors)
    {
        for (var i = 0; i < newSpan.Length; i++)
        {
            var (subject, _, index) = newSpan[i];
            ref var cursor = ref CollectionsMarshal.GetValueRefOrAddDefault(cursors, subject, out var exists);
            if (!exists)
            {
                // Attached by this write: its attach carries the first place, so only a second one is reported.
                cursor.Next = AttachedOnce;
            }
            else
            {
                cursor.Next = cursor.Next >= 0 && Equals(oldSpan[cursor.Next].index, index) ? next[cursor.Next] : PlacesDiffer;
                if (cursor.LastNewPlace != NoNextPlace)
                {
                    nextNew[cursor.LastNewPlace] = i;
                }
            }

            nextNew[i] = NoNextPlace;
            cursor.LastNewPlace = i;
        }
    }

    // Each subject whose places differed, or whose cursor is still on a place the new value lost, is reported at its
    // first place with all of its places.
    private static void AddChangedSubjects(
        ReadOnlySpan<(IInterceptorSubject subject, PropertyReference property, object? index)> newSpan,
        ReadOnlySpan<int> nextNew,
        Buffers buffers)
    {
        var cursors = buffers.Cursors;
        for (var i = 0; i < newSpan.Length; i++)
        {
            var subject = newSpan[i].subject;
            ref var cursor = ref CollectionsMarshal.GetValueRefOrNullRef(cursors, subject);
            if (cursor.Next is AllPlacesMatched or AttachedOnce)
            {
                continue;
            }

            for (var place = i; place != NoNextPlace; place = nextNew[place])
            {
                buffers.Changes.Add(new SubjectChild(subject, newSpan[place].index));
            }

            cursor.Next = AllPlacesMatched;
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

    private struct PlaceCursor
    {
        // The next place in the old value to compare, or one of AllPlacesMatched, PlacesDiffer and AttachedOnce.
        public int Next;

        // The last place of the subject in the new value seen so far, or NoNextPlace.
        public int LastNewPlace;
    }

    private sealed class Buffers
    {
        public List<SubjectChild> Changes { get; } = [];

        public Dictionary<IInterceptorSubject, object?> OldIndices { get; } = new(ReferenceEqualityComparer.Instance);

        public Dictionary<IInterceptorSubject, PlaceCursor> Cursors { get; } = new(ReferenceEqualityComparer.Instance);

        public List<int> NextPlaces { get; } = [];
    }
}
