using System.Collections;
using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>Materializes the subject occurrences exposed by one structural property value.</summary>
internal static class StructuralSnapshotBuilder
{
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

    internal static void CaptureComponent(
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

        CapturePending(context, graphState, visited, discovered, snapshots, propertyNames, includeAttached, pending);
    }

    internal static void CaptureComponent(
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
        CapturePending(context, graphState, visited, discovered, snapshots, propertyNames, true, pending);
    }

    private static void CapturePending(
        IInterceptorSubjectContext context,
        OwnershipGraph.GraphState graphState,
        HashSet<IInterceptorSubject> visited,
        List<IInterceptorSubject> discovered,
        Dictionary<PropertyReference, StructuralSnapshot> snapshots,
        Dictionary<IInterceptorSubject, ImmutableArray<string>> propertyNames,
        bool includeAttached,
        Stack<IInterceptorSubject> pending)
    {
        try
        {
            while (pending.Count > 0)
            {
                var subject = pending.Pop();
                if (!visited.Add(subject))
                {
                    continue;
                }

                var attachedContext = subject.Executor.AttachedContext;
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

                if (graphState.Owned.TryGetValue(subject, out var ownership))
                {
                    propertyNames.TryAdd(subject, ownership.PropertyNames);
                    continue;
                }

                var executor = (InterceptorExecutor)subject.Executor;
                var revision = executor.CurrentRevision;
                var names = ImmutableArray.CreateBuilder<string>(subject.Properties.Count);
                foreach (var entry in subject.Properties)
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

                if (executor.CurrentRevision != revision)
                {
                    throw LifecycleConflictException.Retryable(subject);
                }

                propertyNames.Add(subject, names.MoveToImmutable());
            }
        }
        finally
        {
            LifecycleScratch.Return(pending);
        }
    }
}
