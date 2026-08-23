using System.Collections;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>One subject occurrence inside a structural property value, with its ordinal or key.</summary>
internal readonly struct SubjectOccurrence(IInterceptorSubject subject, object? index)
{
    public readonly IInterceptorSubject Subject = subject;
    public readonly object? Index = index;
}

/// <summary>
/// Reads structural property values: turns one into its subject occurrences, and answers whether it
/// still contains a given subject. The whole ownership model is defined over these occurrences, so
/// every reader (reconcile, attach seeding, release descent, committed-edge validation) goes
/// through this one interpretation of a value's shape.
/// </summary>
internal static class StructuralValueScanner
{
    /// <summary>
    /// Appends every subject occurrence of the value, in enumeration order, with the ordinal or key
    /// that identifies it.
    /// </summary>
    /// <remarks>
    /// Hot paths (<see cref="IDictionary"/>, <see cref="ICollection"/>) come before the
    /// string/<see cref="IEnumerable"/> arms so common writes do not pay extra type checks. The
    /// trailing <see cref="IEnumerable"/> arm handles read-only types that implement neither
    /// interface (custom read-only list or dictionary wrappers), which is why it has to fall back
    /// to the declared property shape to tell a keyed value from an ordinal one.
    /// </remarks>
    public static void CollectOccurrences(PropertyReference property, object? value, List<SubjectOccurrence> occurrences)
    {
        switch (value)
        {
            case null:
                return;

            case IInterceptorSubject subject:
                occurrences.Add(new SubjectOccurrence(subject, null));
                return;

            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is IInterceptorSubject subjectItem)
                    {
                        occurrences.Add(new SubjectOccurrence(subjectItem, entry.Key));
                    }
                }

                return;

            case ICollection collection:
            {
                var index = 0;
                foreach (var item in collection)
                {
                    if (item is IInterceptorSubject subjectItem)
                    {
                        occurrences.Add(new SubjectOccurrence(subjectItem, index));
                    }

                    index++;
                }

                return;
            }

            case string:
                return;

            case IEnumerable enumerable:
                if (property.Metadata.Type.IsSubjectDictionaryType())
                {
                    foreach (var item in enumerable)
                    {
                        if (item is not null &&
                            SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subjectItem))
                        {
                            occurrences.Add(new SubjectOccurrence(subjectItem, key));
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
                            occurrences.Add(new SubjectOccurrence(subjectItem, index));
                        }

                        index++;
                    }
                }

                return;
        }
    }

    /// <summary>
    /// Whether occurrences of this value are identified by a key rather than by an ordinal. Keys are
    /// stable identities, so a reorder never invalidates them and matching goes by key; ordinals
    /// shift on every insertion, so matching goes by occurrence count and the indices are refreshed.
    /// </summary>
    public static bool HasKeyedOccurrences(PropertyReference property, object? value)
    {
        return value is IDictionary || (value is not (null or string or ICollection) && property.Metadata.Type.IsSubjectDictionaryType());
    }

    /// <summary>Whether the value still contains the subject at all, at any occurrence.</summary>
    public static bool Contains(PropertyReference property, object? value, IInterceptorSubject target)
    {
        switch (value)
        {
            case null:
                return false;

            case IInterceptorSubject subject:
                return ReferenceEquals(subject, target);

            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (ReferenceEquals(entry.Value, target))
                    {
                        return true;
                    }
                }

                return false;

            case ICollection collection:
                foreach (var item in collection)
                {
                    if (ReferenceEquals(item, target))
                    {
                        return true;
                    }
                }

                return false;

            case string:
                return false;

            case IEnumerable enumerable:
                if (property.Metadata.Type.IsSubjectDictionaryType())
                {
                    foreach (var item in enumerable)
                    {
                        if (item is not null &&
                            SubjectLookup.TryGetSubjectFromKeyValuePair(item, out _, out var subjectItem) &&
                            ReferenceEquals(subjectItem, target))
                        {
                            return true;
                        }
                    }
                }
                else
                {
                    foreach (var item in enumerable)
                    {
                        if (ReferenceEquals(item, target))
                        {
                            return true;
                        }
                    }
                }

                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Whether the value could hold subjects at all. Mirrors the check the reconcile short circuit
    /// uses to skip values that are neither a subject nor a container.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanHoldSubjects(object? value)
    {
        return value is null or IInterceptorSubject or IEnumerable && value is not string;
    }
}
