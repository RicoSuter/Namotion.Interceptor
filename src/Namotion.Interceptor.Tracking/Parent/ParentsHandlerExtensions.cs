using System.Collections.Immutable;

namespace Namotion.Interceptor.Tracking.Parent;

public static class ParentsHandlerExtensions
{
    private const string ParentsKey = "Namotion.Interceptor.Tracking.Parents";

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
        // Subjects with one parent each are walked without allocating. The checkpoint detects a cycle on
        // such a chain (Brent's algorithm): it is always a subject that was checked before, so meeting it
        // again means the walk passed every subject of the cycle.
        var current = subject;
        var checkpoint = subject;
        var stepsSinceCheckpoint = 0L;
        var stepsUntilCheckpoint = 1L;

        while (true)
        {
            var parents = current.GetParents();
            if (parents.Length == 0)
            {
                return null;
            }

            if (parents.Length > 1)
            {
                return FindFirstParentBreadthFirst<TRoot>(subject, current);
            }

            var parent = parents[0];
            if (parent.Equals(default))
            {
                return null;
            }

            current = parent.Property.Subject;
            if (ReferenceEquals(current, checkpoint))
            {
                return null;
            }

            if (current is TRoot root && !ReferenceEquals(current, subject))
            {
                return root;
            }

            stepsSinceCheckpoint++;
            if (stepsSinceCheckpoint == stepsUntilCheckpoint)
            {
                checkpoint = current;
                stepsSinceCheckpoint = 0;
                stepsUntilCheckpoint *= 2;
            }
        }
    }

    /// <summary>
    /// Searches breadth first from <paramref name="start"/>, which was reached from
    /// <paramref name="subject"/> over subjects with one parent each that are no match.
    /// </summary>
    private static TRoot? FindFirstParentBreadthFirst<TRoot>(IInterceptorSubject subject, IInterceptorSubject start)
        where TRoot : class
    {
        var visited = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<IInterceptorSubject>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (!visited.Add(current))
            {
                continue;
            }

            if (current is TRoot root && !ReferenceEquals(current, subject))
            {
                return root;
            }

            foreach (var parent in current.GetParents())
            {
                if (!parent.Equals(default))
                {
                    queue.Enqueue(parent.Property.Subject);
                }
            }
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
