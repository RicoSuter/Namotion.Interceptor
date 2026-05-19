using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking;

/// <summary>
/// Visitor interface for zero-allocation subject iteration. Implement as a struct
/// so the JIT monomorphizes the generic <see cref="SubjectValueVisitor.VisitCollectionSubjects{TVisitor}"/> call.
/// </summary>
public interface ISubjectValueVisitor
{
    void OnSubject(IInterceptorSubject subject, object? indexOrKey);
}

/// <summary>
/// Shared tiered dispatch for discovering subjects inside property values.
/// Fast paths (<see cref="IDictionary"/>, <see cref="ICollection"/>, <see cref="IList"/>)
/// are checked first; <see cref="IEnumerable"/> with cached KVP extraction is the fallback
/// for read-only types.
/// </summary>
public static class SubjectValueVisitor
{
    private static readonly ConcurrentDictionary<Type, (Func<object, object?> getKey, Func<object, object?> getValue)?> KvpAccessorCache = new();

    /// <summary>
    /// Visits all subjects found in a collection <paramref name="value"/> using tiered dispatch.
    /// The visitor receives the zero-based integer index as <c>indexOrKey</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void VisitCollectionSubjects<TVisitor>(object value, ref TVisitor visitor)
        where TVisitor : struct, ISubjectValueVisitor
    {
        switch (value)
        {
            case IInterceptorSubject subject:
                visitor.OnSubject(subject, null);
                break;

            case string:
                break;

            case ICollection collection:
            {
                var i = 0;
                foreach (var item in collection)
                {
                    if (item is IInterceptorSubject subjectItem)
                        visitor.OnSubject(subjectItem, i);
                    i++;
                }
                break;
            }

            case IEnumerable enumerable:
            {
                var j = 0;
                foreach (var item in enumerable)
                {
                    if (item is IInterceptorSubject subjectItem)
                        visitor.OnSubject(subjectItem, j);
                    j++;
                }
                break;
            }
        }
    }

    /// <summary>
    /// Visits all subjects found in a dictionary <paramref name="value"/> using tiered dispatch.
    /// Items are treated as <c>KeyValuePair&lt;K,V&gt;</c> and the visitor receives the
    /// dictionary key as <c>indexOrKey</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void VisitDictionarySubjects<TVisitor>(object value, ref TVisitor visitor)
        where TVisitor : struct, ISubjectValueVisitor
    {
        switch (value)
        {
            case IInterceptorSubject subject:
                visitor.OnSubject(subject, null);
                break;

            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is IInterceptorSubject subjectItem)
                        visitor.OnSubject(subjectItem, entry.Key);
                }
                break;

            case string:
                break;

            case ICollection collection:
            {
                var i = 0;
                foreach (var item in collection)
                {
                    if (item is IInterceptorSubject subjectItem)
                        visitor.OnSubject(subjectItem, i);
                    i++;
                }
                break;
            }

            case IEnumerable enumerable:
                foreach (var item in enumerable)
                {
                    if (item is null) continue;
                    if (TryGetKvpSubjectEntry(item, out var key, out var subject))
                        visitor.OnSubject(subject, key);
                }
                break;
        }
    }

    /// <summary>
    /// Finds a single subject at the given <paramref name="index"/> inside
    /// a collection <paramref name="value"/>, using <see cref="IList"/>
    /// fast path with <see cref="IEnumerable"/> fallback.
    /// </summary>
    public static IInterceptorSubject? FindCollectionSubjectAt(object value, int index)
    {
        if (value is IList list)
            return list[index] as IInterceptorSubject;

        if (value is IEnumerable enumerable)
        {
            var i = 0;
            foreach (var item in enumerable)
            {
                if (i == index)
                    return item as IInterceptorSubject;
                i++;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a single subject at the given <paramref name="key"/> inside
    /// a dictionary <paramref name="value"/>, using <see cref="IDictionary"/>
    /// fast path with <see cref="IEnumerable"/> KVP extraction fallback.
    /// </summary>
    public static IInterceptorSubject? FindDictionarySubjectAt(object value, object key)
    {
        if (value is IDictionary dictionary)
            return dictionary[key] as IInterceptorSubject;

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is null) continue;
                if (TryGetKvpSubjectEntry(item, out var itemKey, out var subject) && Equals(itemKey, key))
                    return subject;
            }
        }

        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetKvpSubjectEntry(object item, out object? key, out IInterceptorSubject subject)
    {
        var accessors = KvpAccessorCache.GetOrAdd(item.GetType(), static t =>
        {
            if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(KeyValuePair<,>))
                return null;

            var keyProp = t.GetProperty(nameof(KeyValuePair<int, int>.Key))!;
            var valueProp = t.GetProperty(nameof(KeyValuePair<int, int>.Value))!;
            return (
                getKey: (Func<object, object?>)(obj => keyProp.GetValue(obj)),
                getValue: (Func<object, object?>)(obj => valueProp.GetValue(obj))
            );
        });

        if (accessors is not null && accessors.Value.getValue(item) is IInterceptorSubject s)
        {
            key = accessors.Value.getKey(item);
            subject = s;
            return true;
        }

        key = null;
        subject = null!;
        return false;
    }
}
