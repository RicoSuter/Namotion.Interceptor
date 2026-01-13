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
    /// Finds the first ancestor of the specified type by traversing the parent hierarchy (BFS).
    /// </summary>
    /// <typeparam name="T">The type to search for.</typeparam>
    /// <param name="subject">The subject to start searching from.</param>
    /// <returns>The first matching ancestor, or null if not found.</returns>
    public static T? TryFindFirstParent<T>(this IInterceptorSubject subject)
        where T : class
    {
        var visited = new HashSet<IInterceptorSubject>();
        var queue = new Queue<IInterceptorSubject>();
        queue.Enqueue(subject);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (!visited.Add(current))
            {
                continue;
            }

            if (current is T match && !ReferenceEquals(current, subject))
            {
                return match;
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
    /// Thread-safe collection with O(1) writes and zero-allocation reads via cached ImmutableArray.
    /// </summary>
    private sealed class ParentsSet
    {
        private readonly Lock _lock = new();
        private readonly HashSet<SubjectParent> _set = [];
        private volatile ImmutableArray<SubjectParent>[]? _cache; // Box in array for volatile

        public bool Add(SubjectParent parent)
        {
            lock (_lock)
            {
                if (_set.Add(parent))
                {
                    _cache = null; // Invalidate cache
                    return true;
                }
                return false;
            }
        }

        public bool Remove(SubjectParent parent)
        {
            lock (_lock)
            {
                if (_set.Remove(parent))
                {
                    _cache = null; // Invalidate cache
                    return true;
                }
                return false;
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
