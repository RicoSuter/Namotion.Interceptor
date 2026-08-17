using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Parent;

[RunsBefore(typeof(ContextInheritanceHandler))]
public class ParentTrackingHandler : ILifecycleHandler, IPropertyRelationshipHandler
{
    private readonly Lock _relationshipReconciliationGate = new();
    private readonly Dictionary<PropertyReference, IInterceptorSubject[]> _childrenByProperty =
        new(PropertyReference.Comparer);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (!change.Property.HasValue)
        {
            return;
        }

        // Add parent on attach or reference added
        if (change.IsContextAttach || change.IsPropertyReferenceAdded)
        {
            var relationship = change.Relationship ?? new SubjectPropertyRelationship(
                change.Property.Value,
                change.Subject,
                change.Index);

            lock (_relationshipReconciliationGate)
            {
                change.Subject.AddParent(relationship);
            }

            return;
        }

        // Remove parent on reference removed
        if (change.IsPropertyReferenceRemoved)
        {
            lock (_relationshipReconciliationGate)
            {
                change.Subject.RemoveParent(change.Property.Value);
            }
        }
    }

    public void ReconcileChildRelationships(
        PropertyReference property,
        ReadOnlySpan<SubjectPropertyRelationship> relationships)
    {
        lock (_relationshipReconciliationGate)
        {
            var groupIndexes = new Dictionary<IInterceptorSubject, int>(ReferenceEqualityComparer.Instance);
            var groups = new List<RelationshipGroup>();
            foreach (var relationship in relationships)
            {
                if (!groupIndexes.TryGetValue(relationship.Child, out var groupIndex))
                {
                    groupIndex = groups.Count;
                    groupIndexes.Add(relationship.Child, groupIndex);
                    groups.Add(new RelationshipGroup(relationship.Child));
                }

                groups[groupIndex].Relationships.Add(relationship);
            }

            if (_childrenByProperty.TryGetValue(property, out var previousChildren))
            {
                foreach (var previousChild in previousChildren)
                {
                    if (!groupIndexes.ContainsKey(previousChild))
                    {
                        previousChild.RemoveParent(property);
                    }
                }
            }

            foreach (var group in groups)
            {
                group.Child.ReplaceParentGroup(property, [.. group.Relationships]);
            }

            if (groups.Count == 0)
            {
                _childrenByProperty.Remove(property);
            }
            else
            {
                var currentChildren = new IInterceptorSubject[groups.Count];
                for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
                {
                    currentChildren[groupIndex] = groups[groupIndex].Child;
                }

                _childrenByProperty[property] = currentChildren;
            }
        }
    }

    private sealed class RelationshipGroup(IInterceptorSubject child)
    {
        public IInterceptorSubject Child { get; } = child;

        public List<SubjectPropertyRelationship> Relationships { get; } = [];
    }
}
