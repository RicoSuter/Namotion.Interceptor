using System.Collections;
using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>Materializes the subject occurrences exposed by one structural property value.</summary>
internal static class StructuralSnapshotBuilder
{
    internal readonly record struct CaptureParticipant(
        IInterceptorSubject Subject, InterceptorExecutor Executor, long Revision, long CaptureRevision,
        IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties,
        IInterceptorSubjectContext? AttachmentContext, long AttachmentRevision,
        SubjectOwnership? Ownership)
    {
        internal bool IsLocallyCurrent()
        {
            Executor.TryGetAttachment(out var context, out _, out var attachmentRevision);
            return Executor.CurrentRevision == Revision &&
                   Executor.IsCaptureRevisionCurrent(CaptureRevision) &&
                   ReferenceEquals(context, AttachmentContext) && attachmentRevision == AttachmentRevision;
        }

        internal bool TryRefreshAfterCapture(OwnershipGraph.GraphState state, out CaptureParticipant current)
        {
            current = this;
            Executor.TryGetAttachment(out var context, out _, out var attachmentRevision);
            if (!ReferenceEquals(context, AttachmentContext) || attachmentRevision != AttachmentRevision ||
                !Executor.TryRefreshCapture(CaptureRevision, out var captureRevision))
            {
                return false;
            }

            var revision = Executor.CurrentRevision;
            state.Owned.TryGetValue(Subject, out var ownership);
            if (!ReferenceEquals(ownership, Ownership) &&
                (ownership is null || Ownership is null || ownership.Edges != Ownership.Edges))
            {
                return false;
            }

            current = new CaptureParticipant(
                Subject, Executor, revision, captureRevision, Properties,
                AttachmentContext, AttachmentRevision, ownership);
            return true;
        }
    }

    public static StructuralSnapshot Build(Type declaredType, object? value, long sourceRevision)
    {
        if (value is null or string)
        {
            return new StructuralSnapshot(sourceRevision, []);
        }

        if (value is IInterceptorSubject subject)
        {
            return new StructuralSnapshot(sourceRevision, [new StructuralOccurrence(subject, 0, null)]);
        }

        var occurrences = LifecycleScratch.RentStructuralOccurrenceList();
        var subjectOrdinals = LifecycleScratch.RentSubjectCounter();

        void Add(IInterceptorSubject subject, object? index)
        {
            var subjectOrdinal = subjectOrdinals.GetValueOrDefault(subject);
            subjectOrdinals[subject] = subjectOrdinal + 1;
            occurrences.Add(new StructuralOccurrence(subject, subjectOrdinal, index));
        }

        try
        {
            switch (value)
            {
                case IDictionary dictionary:
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (entry.Value is IInterceptorSubject subjectItem)
                        {
                            Add(subjectItem, entry.Key);
                        }
                    }

                    break;

                case ICollection collection:
                {
                    var index = 0;
                    foreach (var item in collection)
                    {
                        if (item is IInterceptorSubject subjectItem)
                        {
                            Add(subjectItem, index);
                        }

                        index++;
                    }

                    break;
                }

                case IEnumerable enumerable:
                    if (HasKeyedEntries(declaredType, enumerable))
                    {
                        foreach (var item in enumerable)
                        {
                            if (item is not null &&
                                SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subjectItem))
                            {
                                Add(subjectItem, key);
                            }
                        }
                    }
                    else
                    {
                        var index = 0;
                        foreach (var item in enumerable)
                        {
                            if (item is IInterceptorSubject subjectItem)
                            {
                                Add(subjectItem, index);
                            }

                            index++;
                        }
                    }

                    break;
            }

            return new StructuralSnapshot(sourceRevision, occurrences.ToImmutableArray());
        }
        finally
        {
            LifecycleScratch.Return(occurrences);
            LifecycleScratch.Return(subjectOrdinals);
        }
    }

    private static bool HasKeyedEntries(Type declaredType, object value)
    {
        return declaredType.IsSubjectDictionaryType() || value.GetType().IsSubjectDictionaryType();
    }

    internal static ImmutableArray<CaptureParticipant> CaptureComponent(
        StructuralSnapshot roots,
        IInterceptorSubjectContext context,
        OwnershipGraph.GraphState graphState,
        HashSet<IInterceptorSubject> visited,
        List<IInterceptorSubject> discovered,
        Dictionary<PropertyReference, StructuralSnapshot> snapshots,
        Dictionary<IInterceptorSubject, ImmutableArray<string>> propertyNames,
        bool includeAttached = true)
    {
        var pending = LifecycleScratch.RentSubjectStack();
        foreach (var occurrence in roots.Occurrences)
        {
            pending.Push(occurrence.Subject);
        }

        return CapturePending(
            context, graphState, visited, discovered, snapshots, propertyNames, includeAttached, pending);
    }

    internal static ImmutableArray<CaptureParticipant> CaptureComponent(
        IInterceptorSubject root,
        IInterceptorSubjectContext context,
        OwnershipGraph.GraphState graphState,
        HashSet<IInterceptorSubject> visited,
        List<IInterceptorSubject> discovered,
        Dictionary<PropertyReference, StructuralSnapshot> snapshots,
        Dictionary<IInterceptorSubject, ImmutableArray<string>> propertyNames)
    {
        var pending = LifecycleScratch.RentSubjectStack();
        pending.Push(root);
        return CapturePending(context, graphState, visited, discovered, snapshots, propertyNames, true, pending);
    }

    internal static CaptureParticipant CaptureParticipantState(
        IInterceptorSubject subject,
        OwnershipGraph.GraphState graphState)
    {
        var executor = (InterceptorExecutor)subject.Executor;
        var revision = executor.CurrentRevision;
        var captureRevision = executor.CaptureRevision;
        var properties = subject.Properties;
        if (!executor.IsCaptureRevisionCurrent(captureRevision))
        {
            throw LifecycleConflictException.Retryable(subject);
        }

        executor.TryGetAttachment(out var attachmentContext, out _, out var attachmentRevision);
        graphState.Owned.TryGetValue(subject, out var ownership);
        return new CaptureParticipant(
            subject, executor, revision, captureRevision, properties,
            attachmentContext, attachmentRevision, ownership);
    }

    private static ImmutableArray<CaptureParticipant> CapturePending(
        IInterceptorSubjectContext context,
        OwnershipGraph.GraphState graphState,
        HashSet<IInterceptorSubject> visited,
        List<IInterceptorSubject> discovered,
        Dictionary<PropertyReference, StructuralSnapshot> snapshots,
        Dictionary<IInterceptorSubject, ImmutableArray<string>> propertyNames,
        bool includeAttached,
        Stack<IInterceptorSubject> pending)
    {
        var participants = ImmutableArray.CreateBuilder<CaptureParticipant>();
        try
        {
            while (pending.Count > 0)
            {
                var subject = pending.Pop();
                if (!visited.Add(subject))
                {
                    continue;
                }

                var participant = CaptureParticipantState(subject, graphState);
                var attachedContext = participant.AttachmentContext;
                if (attachedContext is not null && !ReferenceEquals(attachedContext, context))
                {
                    throw new InvalidOperationException(
                        $"The subject '{subject.GetType().Name}' is owned by a different context and cannot " +
                        "join this graph. Detach it from that context first.");
                }

                if (attachedContext is null || includeAttached)
                {
                    discovered.Add(subject);
                }

                if (participant.Ownership is { } ownership)
                {
                    propertyNames.TryAdd(subject, ownership.PropertyNames);
                    participants.Add(participant);
                    continue;
                }

                var names = ImmutableArray.CreateBuilder<string>(participant.Properties.Count);
                foreach (var entry in participant.Properties)
                {
                    names.Add(entry.Key);
                    if (!OwnershipGraph.IsStructural(entry.Value))
                    {
                        continue;
                    }

                    var snapshot = Build(entry.Value.Type, entry.Value.GetValue?.Invoke(subject), 0);
                    snapshots.Add(new PropertyReference(subject, entry.Key), snapshot);
                    foreach (var occurrence in snapshot.Occurrences)
                    {
                        pending.Push(occurrence.Subject);
                    }
                }

                if (participant.Executor.CurrentRevision != participant.Revision)
                {
                    throw LifecycleConflictException.Retryable(subject);
                }

                propertyNames.Add(subject, names.MoveToImmutable());
                participants.Add(participant);
            }

            return participants.ToImmutable();
        }
        finally
        {
            LifecycleScratch.Return(pending);
        }
    }
}
