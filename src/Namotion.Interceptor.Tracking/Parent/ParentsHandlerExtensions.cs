using System.Collections.Immutable;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Parent;

public static class ParentsHandlerExtensions
{
    private const string ParentsKey = "Namotion.Interceptor.Tracking.Parents";

    internal static void AddParent(this IInterceptorSubject subject, SubjectPropertyRelationship relationship)
    {
        var parentsSet = (ParentsSet)subject.Data.GetOrAdd((null, ParentsKey), _ => new ParentsSet())!;
        parentsSet.Add(relationship);
    }

    internal static void ReplaceParentGroup(
        this IInterceptorSubject subject,
        PropertyReference parent,
        ImmutableArray<SubjectPropertyRelationship> relationships)
    {
        var parentsSet = (ParentsSet)subject.Data.GetOrAdd((null, ParentsKey), _ => new ParentsSet())!;
        parentsSet.Replace(parent, relationships);
    }

    internal static void RemoveParent(this IInterceptorSubject subject, PropertyReference parent)
    {
        if (subject.Data.TryGetValue((null, ParentsKey), out var existing))
        {
            ((ParentsSet)existing!).Remove(parent);
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
                queue.Enqueue(parent.Property.Subject);
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
    /// Thread-safe ordered relationship groups with zero-allocation cached reads.
    /// </summary>
    private sealed class ParentsSet
    {
        private readonly Lock _lock = new();
        private RelationshipGroup? _firstGroup;
        private List<RelationshipGroup>? _additionalGroups;
        private ImmutableArray<SubjectParent>[]? _cache; // Box in array for volatile publication

        public bool Add(SubjectPropertyRelationship relationship)
        {
            lock (_lock)
            {
                if (FindGroup(relationship.Parent) >= 0)
                {
                    return false;
                }

                AddGroup(new RelationshipGroup(relationship.Parent, [relationship]));
                Volatile.Write(ref _cache, null);
                return true;
            }
        }

        public void Replace(
            PropertyReference parent,
            ImmutableArray<SubjectPropertyRelationship> relationships)
        {
            lock (_lock)
            {
                var group = new RelationshipGroup(parent, relationships);
                var groupIndex = FindGroup(parent);
                if (groupIndex == 0)
                {
                    _firstGroup = group;
                }
                else if (groupIndex > 0)
                {
                    _additionalGroups![groupIndex - 1] = group;
                }
                else
                {
                    AddGroup(group);
                }

                Volatile.Write(ref _cache, null);
            }
        }

        public bool Remove(PropertyReference parent)
        {
            lock (_lock)
            {
                var groupIndex = FindGroup(parent);
                if (groupIndex < 0)
                {
                    return false;
                }

                if (groupIndex == 0)
                {
                    if (_additionalGroups is { Count: > 0 } additionalGroups)
                    {
                        _firstGroup = additionalGroups[0];
                        additionalGroups.RemoveAt(0);
                        if (additionalGroups.Count == 0)
                        {
                            _additionalGroups = null;
                        }
                    }
                    else
                    {
                        _firstGroup = null;
                    }
                }
                else
                {
                    _additionalGroups!.RemoveAt(groupIndex - 1);
                    if (_additionalGroups.Count == 0)
                    {
                        _additionalGroups = null;
                    }
                }

                Volatile.Write(ref _cache, null);
                return true;
            }
        }

        public ImmutableArray<SubjectParent> ToImmutableArray()
        {
            var cached = Volatile.Read(ref _cache);
            if (cached is not null)
            {
                return cached[0];
            }

            lock (_lock)
            {
                cached = Volatile.Read(ref _cache);
                if (cached is not null)
                {
                    return cached[0];
                }

                ImmutableArray<SubjectParent> array;
                if (_firstGroup is null)
                {
                    array = ImmutableArray<SubjectParent>.Empty;
                }
                else
                {
                    var count = _firstGroup.Relationships.Length;
                    if (_additionalGroups is not null)
                    {
                        foreach (var group in _additionalGroups)
                        {
                            count += group.Relationships.Length;
                        }
                    }

                    var builder = ImmutableArray.CreateBuilder<SubjectParent>(count);
                    AddParents(builder, _firstGroup);
                    if (_additionalGroups is not null)
                    {
                        foreach (var group in _additionalGroups)
                        {
                            AddParents(builder, group);
                        }
                    }

                    array = builder.MoveToImmutable();
                }

                cached = [array];
                Volatile.Write(ref _cache, cached);
                return array;
            }
        }

        private int FindGroup(PropertyReference parent)
        {
            if (_firstGroup is null)
            {
                return -1;
            }

            if (PropertyReference.Comparer.Equals(_firstGroup.Parent, parent))
            {
                return 0;
            }

            if (_additionalGroups is not null)
            {
                for (var index = 0; index < _additionalGroups.Count; index++)
                {
                    if (PropertyReference.Comparer.Equals(_additionalGroups[index].Parent, parent))
                    {
                        return index + 1;
                    }
                }
            }

            return -1;
        }

        private void AddGroup(RelationshipGroup group)
        {
            if (_firstGroup is null)
            {
                _firstGroup = group;
                return;
            }

            _additionalGroups ??= [];
            _additionalGroups.Add(group);
        }

        private static void AddParents(
            ImmutableArray<SubjectParent>.Builder builder,
            RelationshipGroup group)
        {
            foreach (var relationship in group.Relationships)
            {
                builder.Add(new SubjectParent(relationship.Parent, relationship.Index));
            }
        }

        private sealed class RelationshipGroup(
            PropertyReference parent,
            ImmutableArray<SubjectPropertyRelationship> relationships)
        {
            public PropertyReference Parent { get; } = parent;

            public ImmutableArray<SubjectPropertyRelationship> Relationships { get; } = relationships;
        }
    }
}
