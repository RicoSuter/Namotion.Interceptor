using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking;

/// <summary>
/// Static helpers for looking up subjects inside opaque property values. Used by
/// <c>PathExtensions</c> for keyed lookups and by the lifecycle/connector paths for
/// read-only-dictionary KVP extraction. Hot paths in <c>LifecycleInterceptor</c> and
/// <c>RegisteredSubjectProperty</c> inline the dispatch switch directly for best codegen
/// rather than going through this class.
/// </summary>
public static class SubjectValueLookup
{
    private static readonly ConcurrentDictionary<Type, (Func<object, object?> getKey, Func<object, object?> getValue)?> KvpAccessorCache = new();

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

    /// <summary>
    /// Reflects <c>KeyValuePair&lt;,&gt;</c> shape for read-only dictionary fallbacks where
    /// <see cref="IDictionary"/> isn't implemented (custom <see cref="IReadOnlyDictionary{TKey,TValue}"/>
    /// wrappers that opt out of the non-generic dict interface). Accessor delegates are cached
    /// per closed KVP type.
    /// </summary>
    /// <remarks>
    /// TODO(perf): reflection-based PropertyInfo.GetValue is the slow path. If a real workload
    /// shows hot read-only-dict iteration, replace the lambdas with compiled expression-tree
    /// accessors per closed <c>KeyValuePair&lt;K,V&gt;</c> type.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetKvpSubjectEntry(object item, out object? key, [NotNullWhen(true)] out IInterceptorSubject? subject)
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
        subject = null;
        return false;
    }
}
