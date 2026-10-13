using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Parent;

public static class ParentsHandlerExtensions
{
    private const string ParentsKey = "ni.parents";

    internal static void AddParent(this IInterceptorSubject subject, PropertyReference parent, object? index)
    {
        var parentsSet = (ParentsSet)subject.Data.GetOrAdd((null, ParentsKey), _ => new ParentsSet())!;
        parentsSet.Add(new SubjectParent(parent, index));
    }

    internal static void RemoveParent(this IInterceptorSubject subject, PropertyReference parent, object? index)
    {
        if (subject.Data.TryGetValue((null, ParentsKey), out var existing))
        {
            ((ParentsSet)existing!).Remove(new SubjectParent(parent, index));
        }
    }

    /// <summary>
    /// Tries to find the nearest parent of the specified type by traversing the parent hierarchy.
    /// The subject itself is never returned. Returns null if not found instead of throwing.
    /// </summary>
    public static TRoot? TryGetFirstParent<TRoot>(this IInterceptorSubject subject)
        where TRoot : class
    {
        // The subjects found so far, in the order they were found: the queue of the breadth first search
        // and its visited set in one. It lives on the stack, and a search that finds more subjects than
        // it holds continues on the heap.
        var buffer = new InlineArray16<IInterceptorSubject>();
        Span<IInterceptorSubject> found = buffer;
        found[0] = subject;
        var count = 1;

        for (var next = 0; next < count; next++)
        {
            var parents = found[next].GetParents();
            for (var i = 0; i < parents.Length; i++)
            {
                // An entry without a subject does not come from tracking. It is skipped instead of ending
                // the search in an exception.
                var parent = parents[i].Property.Subject;
                if (parent is null || Contains(found[..count], parent))
                {
                    continue;
                }

                if (parent is TRoot root)
                {
                    return root;
                }

                if (count == found.Length)
                {
                    return FindFirstParentOnHeap<TRoot>(found, next, parents, i);
                }

                found[count++] = parent;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the subject itself when it is of the specified type, otherwise its nearest parent of that
    /// type. Returns null if not found instead of throwing.
    /// </summary>
    public static TRoot? TryGetFirstParentOrSelf<TRoot>(this IInterceptorSubject subject)
        where TRoot : class
    {
        return subject as TRoot ?? subject.TryGetFirstParent<TRoot>();
    }

    private static bool Contains(ReadOnlySpan<IInterceptorSubject> found, IInterceptorSubject subject)
    {
        foreach (var foundSubject in found)
        {
            if (ReferenceEquals(foundSubject, subject))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Continues the search of <see cref="TryGetFirstParent{TRoot}"/> when its buffer is full: at the parent
    /// <paramref name="parentIndex"/> of the subject <paramref name="next"/> in <paramref name="found"/>.
    /// </summary>
    // Kept out of line: inlined into a hot caller, it made searches past the buffer about 10 % slower.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TRoot? FindFirstParentOnHeap<TRoot>(
        ReadOnlySpan<IInterceptorSubject> found, int next, ImmutableArray<SubjectParent> parents, int parentIndex)
        where TRoot : class
    {
        var visited = new HashSet<IInterceptorSubject>(found.Length + 1, ReferenceEqualityComparer.Instance);
        var queue = new Queue<IInterceptorSubject>();
        for (var i = 0; i < found.Length; i++)
        {
            visited.Add(found[i]);
            if (i > next)
            {
                queue.Enqueue(found[i]);
            }
        }

        while (true)
        {
            if (EnqueueNewParents<TRoot>(parents, parentIndex, visited, queue) is { } match)
            {
                return match;
            }

            if (!queue.TryDequeue(out var current))
            {
                return null;
            }

            parents = current.GetParents();
            parentIndex = 0;
        }
    }

    private static TRoot? EnqueueNewParents<TRoot>(
        ImmutableArray<SubjectParent> parents, int start, HashSet<IInterceptorSubject> visited, Queue<IInterceptorSubject> queue)
        where TRoot : class
    {
        for (var i = start; i < parents.Length; i++)
        {
            var parent = parents[i].Property.Subject;
            if (parent is null || !visited.Add(parent))
            {
                continue;
            }

            if (parent is TRoot root)
            {
                return root;
            }

            queue.Enqueue(parent);
        }

        return null;
    }

    /// <summary>
    /// Gets the parents of the subject as an immutable array.
    /// This is the preferred method for accessing parents with zero-allocation enumeration.
    /// </summary>
    public static ImmutableArray<SubjectParent> GetParents(this IInterceptorSubject subject)
    {
        if (subject.Data.TryGetValue((null, ParentsKey), out var parents))
        {
            return ((ParentsSet)parents!).ToImmutableArray();
        }
        return [];
    }

    /// <summary>
    /// Thread-safe collection with O(1) writes and zero-allocation reads via cached ImmutableArray.
    /// </summary>
    private sealed class ParentsSet
    {
        private readonly Lock _lock = new();
        private readonly HashSet<SubjectParent> _set = [];
        private volatile ImmutableArray<SubjectParent>[]? _cache; // Box in array for volatile

        public void Add(SubjectParent parent)
        {
            lock (_lock)
            {
                if (_set.Add(parent))
                {
                    _cache = null; // Invalidate cache
                }
            }
        }

        public void Remove(SubjectParent parent)
        {
            lock (_lock)
            {
                if (_set.Remove(parent))
                {
                    _cache = null; // Invalidate cache
                }
            }
        }

        public ImmutableArray<SubjectParent> ToImmutableArray()
        {
            var cached = _cache;
            if (cached is not null)
            {
                return cached[0];
            }

            lock (_lock)
            {
                cached = _cache;
                if (cached is not null)
                {
                    return cached[0];
                }

                // Fast path: avoid allocation for empty set
                if (_set.Count == 0)
                {
                    _cache = [ImmutableArray<SubjectParent>.Empty];
                    return ImmutableArray<SubjectParent>.Empty;
                }

                ImmutableArray<SubjectParent> array = [.. _set];
                _cache = [array];
                return array;
            }
        }
    }
}
