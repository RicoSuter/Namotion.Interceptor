using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Namotion.Interceptor.Tracking.Parent;

public static class ParentsHandlerExtensions
{
    private const string ParentsKey = "Namotion.Interceptor.Tracking.Parents";

    internal static void AddParent(this IInterceptorSubject subject, PropertyReference parent, object? index)
    {
        var parentsSet = (ParentsSet)subject.Data.GetOrAdd((null, ParentsKey), _ => new ParentsSet())!;
        parentsSet.Add(parent, index);
    }

    internal static void RemoveParent(this IInterceptorSubject subject, PropertyReference parent)
    {
        if (subject.Data.TryGetValue((null, ParentsKey), out var existing))
        {
            ((ParentsSet)existing!).Remove(parent);
        }
    }

    internal static void UpdateParentIndex(this IInterceptorSubject subject, PropertyReference parent, object? index)
    {
        if (subject.Data.TryGetValue((null, ParentsKey), out var existing))
        {
            ((ParentsSet)existing!).UpdateIndex(parent, index);
        }
    }

    /// <summary>
    /// Tries to find the first parent of the specified type by traversing the parent hierarchy.
    /// Returns null if not found instead of throwing.
    /// </summary>
    public static TRoot? TryGetFirstParent<TRoot>(this IInterceptorSubject subject)
        where TRoot : class
    {
        var visited = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);
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

        // Keyed by the property alone, because the lifecycle attaches a subject to a property
        // once, however many keys or positions of it hold the subject.
        private readonly Dictionary<PropertyReference, object?> _indices = [];
        private volatile ImmutableArray<SubjectParent>[]? _cache; // Box in array for volatile

        public void Add(PropertyReference parent, object? index)
        {
            lock (_lock)
            {
                if (_indices.TryAdd(parent, index))
                {
                    _cache = null; // Invalidate cache
                }
            }
        }

        public void Remove(PropertyReference parent)
        {
            lock (_lock)
            {
                if (_indices.Remove(parent))
                {
                    _cache = null; // Invalidate cache
                }
            }
        }

        public void UpdateIndex(PropertyReference parent, object? index)
        {
            lock (_lock)
            {
                ref var current = ref CollectionsMarshal.GetValueRefOrNullRef(_indices, parent);
                if (Unsafe.IsNullRef(ref current) || Equals(current, index))
                {
                    return;
                }

                current = index;
                _cache = null; // Invalidate cache
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
                if (_indices.Count == 0)
                {
                    _cache = [ImmutableArray<SubjectParent>.Empty];
                    return ImmutableArray<SubjectParent>.Empty;
                }

                var parents = new SubjectParent[_indices.Count];
                var position = 0;
                foreach (var (parent, index) in _indices)
                {
                    parents[position++] = new SubjectParent(parent, index);
                }

                var array = ImmutableCollectionsMarshal.AsImmutableArray(parents);
                _cache = [array];
                return array;
            }
        }
    }
}
