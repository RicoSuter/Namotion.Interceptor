using System.Collections;
using System.Collections.Immutable;

namespace Namotion.Interceptor.Tracking.Lifecycle;

internal static class SubjectPropertyRelationshipReconciler
{
    public static StagedPropertyReconciliation Stage(
        PropertyReference property,
        object? value,
        ProcessedPropertyState? previousState,
        bool materializeRelationships)
    {
        var descriptors = new List<SubjectOccurrenceDescriptor>();
        EnumerateDescriptors(property, value, descriptors);

        previousState ??= ProcessedPropertyState.Empty;
        ImmutableArray<SubjectPropertyRelationship> relationships;
        ImmutableArray<RelationshipIndexKind> relationshipKinds;
        ImmutableArray<SubjectPropertyRelationship> removedRelationships;
        if (materializeRelationships)
        {
            relationships = ReconcileRelationships(
                property,
                descriptors,
                previousState,
                out relationshipKinds,
                out removedRelationships);
        }
        else
        {
            relationships = ImmutableArray<SubjectPropertyRelationship>.Empty;
            relationshipKinds = ImmutableArray<RelationshipIndexKind>.Empty;
            removedRelationships = previousState.Relationships;
        }

        var membershipBuilders = new List<ProcessedSubjectMembershipBuilder>();
        var membershipIndexes = new Dictionary<IInterceptorSubject, int>(ReferenceEqualityComparer.Instance);
        for (var descriptorIndex = 0; descriptorIndex < descriptors.Count; descriptorIndex++)
        {
            var descriptor = descriptors[descriptorIndex];
            if (membershipIndexes.TryGetValue(descriptor.Subject, out var membershipIndex))
            {
                var builder = membershipBuilders[membershipIndex];
                builder.LastIndex = descriptor.Index;
                builder.LastOccurrenceOrdinal = descriptorIndex;
                builder.LastRelationship = materializeRelationships ? relationships[descriptorIndex] : null;
                membershipBuilders[membershipIndex] = builder;
            }
            else
            {
                membershipIndexes.Add(descriptor.Subject, membershipBuilders.Count);
                membershipBuilders.Add(new ProcessedSubjectMembershipBuilder
                {
                    Subject = descriptor.Subject,
                    FirstIndex = descriptor.Index,
                    LastIndex = descriptor.Index,
                    FirstOccurrenceOrdinal = descriptorIndex,
                    LastOccurrenceOrdinal = descriptorIndex,
                    FirstRelationship = materializeRelationships ? relationships[descriptorIndex] : null,
                    LastRelationship = materializeRelationships ? relationships[descriptorIndex] : null
                });
            }
        }

        var memberships = ImmutableArray.CreateBuilder<ProcessedSubjectMembership>(membershipBuilders.Count);
        for (var index = 0; index < membershipBuilders.Count; index++)
        {
            var builder = membershipBuilders[index];
            memberships.Add(new ProcessedSubjectMembership(
                builder.Subject,
                builder.FirstIndex,
                builder.LastIndex,
                builder.FirstOccurrenceOrdinal,
                builder.LastOccurrenceOrdinal,
                builder.FirstRelationship,
                builder.LastRelationship));
        }

        var state = new ProcessedPropertyState(
            memberships.MoveToImmutable(),
            relationships,
            relationshipKinds,
            descriptors.Count);

        var additions = ImmutableArray.CreateBuilder<ProcessedSubjectMembership>();
        var newSubjects = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);
        foreach (var membership in state.Memberships)
        {
            newSubjects.Add(membership.Subject);
        }

        var oldSubjects = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);
        foreach (var membership in previousState.Memberships)
        {
            oldSubjects.Add(membership.Subject);
        }

        foreach (var membership in state.Memberships)
        {
            if (!oldSubjects.Contains(membership.Subject))
            {
                additions.Add(membership);
            }
        }

        var removalsByLastOccurrence = new ProcessedSubjectMembership?[previousState.OccurrenceCount];
        foreach (var membership in previousState.Memberships)
        {
            if (!newSubjects.Contains(membership.Subject))
            {
                removalsByLastOccurrence[membership.LastOccurrenceOrdinal] = membership;
            }
        }

        var removals = ImmutableArray.CreateBuilder<ProcessedSubjectMembership>();
        for (var index = removalsByLastOccurrence.Length - 1; index >= 0; index--)
        {
            if (removalsByLastOccurrence[index] is { } membership)
            {
                removals.Add(membership);
            }
        }

        return new StagedPropertyReconciliation(
            state,
            removals.ToImmutable(),
            additions.ToImmutable(),
            removedRelationships);
    }

    private static ImmutableArray<SubjectPropertyRelationship> ReconcileRelationships(
        PropertyReference property,
        List<SubjectOccurrenceDescriptor> descriptors,
        ProcessedPropertyState previousState,
        out ImmutableArray<RelationshipIndexKind> relationshipKinds,
        out ImmutableArray<SubjectPropertyRelationship> removedRelationships)
    {
        var oldOccurrenceIndexes = new Dictionary<IInterceptorSubject, List<int>>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < previousState.Relationships.Length; index++)
        {
            var child = previousState.Relationships[index].Child;
            if (!oldOccurrenceIndexes.TryGetValue(child, out var indexes))
            {
                indexes = [];
                oldOccurrenceIndexes.Add(child, indexes);
            }

            indexes.Add(index);
        }

        var matchedOccurrenceCounts = new Dictionary<IInterceptorSubject, int>(ReferenceEqualityComparer.Instance);
        var reusedOldRelationships = new bool[previousState.Relationships.Length];
        var relationships = ImmutableArray.CreateBuilder<SubjectPropertyRelationship>(descriptors.Count);
        var kinds = ImmutableArray.CreateBuilder<RelationshipIndexKind>(descriptors.Count);

        foreach (var descriptor in descriptors)
        {
            SubjectPropertyRelationship? relationship = null;
            if (oldOccurrenceIndexes.TryGetValue(descriptor.Subject, out var oldIndexes))
            {
                matchedOccurrenceCounts.TryGetValue(descriptor.Subject, out var matchedOccurrenceCount);
                if (matchedOccurrenceCount < oldIndexes.Count)
                {
                    var oldIndex = oldIndexes[matchedOccurrenceCount];
                    matchedOccurrenceCounts[descriptor.Subject] = matchedOccurrenceCount + 1;

                    var oldRelationship = previousState.Relationships[oldIndex];
                    var oldKind = previousState.RelationshipKinds[oldIndex];
                    if (CanReuse(oldRelationship, oldKind, descriptor))
                    {
                        relationship = oldRelationship;
                        reusedOldRelationships[oldIndex] = true;
                    }
                }
            }

            relationships.Add(relationship ?? new SubjectPropertyRelationship(property, descriptor.Subject, descriptor.Index));
            kinds.Add(descriptor.Kind);
        }

        var removed = ImmutableArray.CreateBuilder<SubjectPropertyRelationship>();
        for (var index = 0; index < previousState.Relationships.Length; index++)
        {
            if (!reusedOldRelationships[index])
            {
                removed.Add(previousState.Relationships[index]);
            }
        }

        relationshipKinds = kinds.MoveToImmutable();
        removedRelationships = removed.ToImmutable();
        return relationships.MoveToImmutable();
    }

    private static bool CanReuse(
        SubjectPropertyRelationship relationship,
        RelationshipIndexKind oldKind,
        SubjectOccurrenceDescriptor descriptor)
    {
        if (oldKind != descriptor.Kind)
        {
            return false;
        }

        return descriptor.Kind switch
        {
            RelationshipIndexKind.Direct => relationship.Index is null && descriptor.Index is null,
            RelationshipIndexKind.Position => relationship.Index is int oldPosition &&
                                              descriptor.Index is int newPosition &&
                                              oldPosition == newPosition,
            RelationshipIndexKind.DictionaryKey => ReferenceEquals(relationship.Index, descriptor.Index),
            _ => false
        };
    }

    private static void EnumerateDescriptors(
        PropertyReference property,
        object? value,
        List<SubjectOccurrenceDescriptor> descriptors)
    {
        switch (value)
        {
            case null:
                return;

            case IInterceptorSubject subject:
                descriptors.Add(new SubjectOccurrenceDescriptor(subject, null, RelationshipIndexKind.Direct));
                return;

            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is IInterceptorSubject child)
                    {
                        descriptors.Add(new SubjectOccurrenceDescriptor(
                            child,
                            entry.Key,
                            RelationshipIndexKind.DictionaryKey));
                    }
                }
                return;

            case string:
                return;

            case IEnumerable enumerable when property.Metadata.Type.IsSubjectDictionaryType():
                foreach (var item in enumerable)
                {
                    if (item is not null &&
                        SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var child))
                    {
                        descriptors.Add(new SubjectOccurrenceDescriptor(
                            child,
                            key,
                            RelationshipIndexKind.DictionaryKey));
                    }
                }
                return;

            case ICollection collection:
            {
                var position = 0;
                foreach (var item in collection)
                {
                    if (item is IInterceptorSubject child)
                    {
                        descriptors.Add(new SubjectOccurrenceDescriptor(
                            child,
                            position,
                            RelationshipIndexKind.Position));
                    }

                    position++;
                }
                return;
            }

            case IEnumerable enumerable:
            {
                var position = 0;
                foreach (var item in enumerable)
                {
                    if (item is IInterceptorSubject child)
                    {
                        descriptors.Add(new SubjectOccurrenceDescriptor(
                            child,
                            position,
                            RelationshipIndexKind.Position));
                    }

                    position++;
                }
                return;
            }
        }
    }

    private readonly record struct SubjectOccurrenceDescriptor(
        IInterceptorSubject Subject,
        object? Index,
        RelationshipIndexKind Kind);

    private struct ProcessedSubjectMembershipBuilder
    {
        public required IInterceptorSubject Subject;
        public object? FirstIndex;
        public object? LastIndex;
        public int FirstOccurrenceOrdinal;
        public int LastOccurrenceOrdinal;
        public SubjectPropertyRelationship? FirstRelationship;
        public SubjectPropertyRelationship? LastRelationship;
    }
}

internal enum RelationshipIndexKind : byte
{
    Direct,
    Position,
    DictionaryKey
}

internal readonly record struct ProcessedSubjectMembership(
    IInterceptorSubject Subject,
    object? FirstIndex,
    object? LastIndex,
    int FirstOccurrenceOrdinal,
    int LastOccurrenceOrdinal,
    SubjectPropertyRelationship? FirstRelationship,
    SubjectPropertyRelationship? LastRelationship);

internal sealed class ProcessedPropertyState
{
    public static ProcessedPropertyState Empty { get; } = new(
        ImmutableArray<ProcessedSubjectMembership>.Empty,
        ImmutableArray<SubjectPropertyRelationship>.Empty,
        ImmutableArray<RelationshipIndexKind>.Empty,
        0);

    public ProcessedPropertyState(
        ImmutableArray<ProcessedSubjectMembership> memberships,
        ImmutableArray<SubjectPropertyRelationship> relationships,
        ImmutableArray<RelationshipIndexKind> relationshipKinds,
        int occurrenceCount)
    {
        Memberships = memberships;
        Relationships = relationships;
        RelationshipKinds = relationshipKinds;
        OccurrenceCount = occurrenceCount;
    }

    public ImmutableArray<ProcessedSubjectMembership> Memberships { get; }

    public ImmutableArray<SubjectPropertyRelationship> Relationships { get; }

    public ImmutableArray<RelationshipIndexKind> RelationshipKinds { get; }

    public int OccurrenceCount { get; }
}

internal sealed class StagedPropertyReconciliation
{
    public StagedPropertyReconciliation(
        ProcessedPropertyState state,
        ImmutableArray<ProcessedSubjectMembership> membershipRemovals,
        ImmutableArray<ProcessedSubjectMembership> membershipAdditions,
        ImmutableArray<SubjectPropertyRelationship> removedRelationships)
    {
        State = state;
        MembershipRemovals = membershipRemovals;
        MembershipAdditions = membershipAdditions;
        RemovedRelationships = removedRelationships;
    }

    public ProcessedPropertyState State { get; }

    public ImmutableArray<ProcessedSubjectMembership> MembershipRemovals { get; }

    public ImmutableArray<ProcessedSubjectMembership> MembershipAdditions { get; }

    public ImmutableArray<SubjectPropertyRelationship> RemovedRelationships { get; }

    public bool HasRelationshipGeneration =>
        State.OccurrenceCount > 0 ||
        MembershipRemovals.Length > 0;
}
