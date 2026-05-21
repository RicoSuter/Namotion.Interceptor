using System.Collections;
using System.Collections.Immutable;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

internal static class SubjectValueConvert
{
    // Read-only singleton for the defensive "value is neither IDictionary nor a non-string
    // IEnumerable" branch in ToSubjectDictionary. Mutation attempts throw, which is safer than
    // sharing a mutable empty Dictionary.
    private static readonly IDictionary EmptyDictionary = ImmutableDictionary<object, IInterceptorSubject>.Empty;

    internal static IReadOnlyList<IInterceptorSubject> ToSubjectList(object value)
    {
        if (value is IReadOnlyList<IInterceptorSubject> readOnlyList)
            return readOnlyList;

        if (value is IEnumerable<IInterceptorSubject> typedEnumerable)
            return typedEnumerable.ToList();

        return CollectSubjects(value) ?? (IReadOnlyList<IInterceptorSubject>)[];
    }

    internal static List<IInterceptorSubject> ToSubjectMutableList(object? value)
    {
        if (value is null)
            return [];

        if (value is IEnumerable<IInterceptorSubject> typedEnumerable)
            return typedEnumerable.ToList();

        return CollectSubjects(value) ?? [];
    }

    /// <summary>
    /// Returns the value as an <see cref="IDictionary"/> for keyed subject access.
    /// Fast path: the value is already an <see cref="IDictionary"/> and is returned as-is (zero allocation).
    /// Slow path: read-only types that implement only <see cref="IEnumerable{T}"/> of <c>KeyValuePair</c>
    /// are materialized into a fresh <see cref="Dictionary{TKey,TValue}"/>. Callers must filter values
    /// against <see cref="IInterceptorSubject"/> inline since the returned dictionary may contain
    /// non-subject entries on the passthrough path.
    /// </summary>
    internal static IDictionary ToSubjectDictionary(object value)
    {
        if (value is IDictionary dict)
            return dict;

        if (value is not IEnumerable enumerable || value is string)
            return EmptyDictionary;

        var result = new Dictionary<object, IInterceptorSubject>();
        foreach (var item in enumerable)
        {
            if (item is null) continue;
            if (SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subject))
                result[key!] = subject;
        }
        return result;
    }

    // Fallback subject collector for collection-classified properties whose runtime value
    // doesn't satisfy IEnumerable<IInterceptorSubject> covariance (e.g. ArrayList, or a
    // List<object> whose elements happen to be subjects). Callers always reach this through
    // collection-classified property paths, so a bare IInterceptorSubject value cannot arrive
    // here: the type system would reject the assignment, and hybrid IIS+IEnumerable<Subject>
    // values are caught by the earlier IEnumerable<IInterceptorSubject> check.
    private static List<IInterceptorSubject>? CollectSubjects(object value)
    {
        List<IInterceptorSubject>? list = null;

        if (value is not string && value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is IInterceptorSubject subjectItem)
                {
                    list ??= [];
                    list.Add(subjectItem);
                }
            }
        }

        return list;
    }
}
