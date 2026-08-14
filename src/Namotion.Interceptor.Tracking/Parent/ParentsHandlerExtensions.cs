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

    /// <summary>
    /// Moves this subject's parent entry for the given property to a new index, so that the tracked parents
    /// stay in step with the registry when a retained subject's position or key changes.
    /// </summary>
    internal static void UpdateParentIndex(this IInterceptorSubject subject, PropertyReference parent, object? newIndex)
    {
        if (subject.Data.TryGetValue((null, ParentsKey), out var existing))
        {
            ((ParentsSet)existing!).UpdateIndex(parent, newIndex);
        }
    }

    internal static void RemoveParent(this IInterceptorSubject subject, PropertyReference parent, object? index)
    {
        if (subject.Data.TryGetValue((null, ParentsKey), out var existing))
        {
            ((ParentsSet)existing!).Remove(new SubjectParent(parent, index));
        }
    }

    /// <summary>
    /// Tries to find the first parent of the specified type by traversing the parent hierarchy.
    /// Returns null if not found instead of throwing.
    /// </summary>
    public static TRoot? TryGetFirstParent<TRoot>(this IInterceptorSubject subject)
        where TRoot : class
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

                // Fall back to the property alone: attach adds at most one entry per property, so this is
                // unambiguous, and an index that somehow moved unnoticed would otherwise strand the entry.
                SubjectParent? stale = null;
                foreach (var entry in _set)
                {
                    if (entry.Property == parent.Property)
                    {
                        stale = entry;
                        break;
                    }
                }

                if (stale is null)
                {
                    return false;
                }

                _set.Remove(stale.Value);
                _cache = null; // Invalidate cache
                return true;
            }
        }

        public bool UpdateIndex(PropertyReference parent, object? newIndex)
        {
            lock (_lock)
            {
                SubjectParent? current = null;
                foreach (var entry in _set)
                {
                    if (entry.Property == parent)
                    {
                        current = entry;
                        break;
                    }
                }

                if (current is null || Equals(current.Value.Index, newIndex))
                {
                    return false;
                }

                _set.Remove(current.Value);
                _set.Add(new SubjectParent(parent, newIndex));
                _cache = null; // Invalidate cache
                return true;
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
