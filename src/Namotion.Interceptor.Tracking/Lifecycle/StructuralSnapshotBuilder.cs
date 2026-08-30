using System.Collections;
using System.Collections.Immutable;

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
}
