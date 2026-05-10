using System.Collections;

namespace Namotion.Interceptor.OpcUa;

/// <summary>
/// Shared helpers for structural change detection used by both the OPC UA server and client.
/// </summary>
internal static class OpcUaStructuralChangeHelper
{
    /// <summary>
    /// Extracts all <see cref="IInterceptorSubject"/> instances from a property value,
    /// handling single subjects, collections, and dictionaries.
    /// </summary>
    internal static List<(IInterceptorSubject Subject, object? Index)> ExtractSubjects(object? value)
    {
        var result = new List<(IInterceptorSubject, object?)>();

        switch (value)
        {
            case IInterceptorSubject subject:
                result.Add((subject, null));
                break;

            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is IInterceptorSubject s)
                    {
                        result.Add((s, entry.Key));
                    }
                }
                break;

            case ICollection collection:
                var i = 0;
                foreach (var item in collection)
                {
                    if (item is IInterceptorSubject s)
                    {
                        result.Add((s, i));
                    }
                    i++;
                }
                break;
        }

        return result;
    }

    /// <summary>
    /// Computes the subjects that were added and removed between two lists of subjects,
    /// using reference equality and O(1) HashSet lookups.
    /// </summary>
    internal static (List<(IInterceptorSubject Subject, object? Index)> Added, List<(IInterceptorSubject Subject, object? Index)> Removed) ComputeSubjectDiff(
        List<(IInterceptorSubject Subject, object? Index)> oldSubjects,
        List<(IInterceptorSubject Subject, object? Index)> newSubjects)
    {
        var oldSet = new HashSet<IInterceptorSubject>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var (subject, _) in oldSubjects)
        {
            oldSet.Add(subject);
        }

        var newSet = new HashSet<IInterceptorSubject>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var (subject, _) in newSubjects)
        {
            newSet.Add(subject);
        }

        var added = new List<(IInterceptorSubject Subject, object? Index)>();
        foreach (var (subject, index) in newSubjects)
        {
            if (!oldSet.Contains(subject))
            {
                added.Add((subject, index));
            }
        }

        var removed = new List<(IInterceptorSubject Subject, object? Index)>();
        foreach (var (subject, index) in oldSubjects)
        {
            if (!newSet.Contains(subject))
            {
                removed.Add((subject, index));
            }
        }

        return (added, removed);
    }
}
