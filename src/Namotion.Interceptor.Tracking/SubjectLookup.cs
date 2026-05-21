using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Namotion.Interceptor.Tracking;

/// <summary>
/// Static helpers for looking up subjects inside opaque property values. Used by
/// <c>PathExtensions</c> for keyed lookups and by the lifecycle/connector paths for
/// read-only-dictionary KVP extraction. Hot paths in <c>LifecycleInterceptor</c> and
/// <c>RegisteredSubjectProperty</c> inline the dispatch switch directly for best codegen
/// rather than going through this class.
/// </summary>
public static class SubjectLookup
{
    private static readonly ConcurrentDictionary<Type, Func<object, (object? key, object? value)>?> KvpAccessorCache = new();

    /// <summary>
    /// Finds a single subject at the given <paramref name="index"/> inside
    /// a collection <paramref name="value"/>, using <see cref="IList"/>
    /// fast path with <see cref="IEnumerable"/> fallback.
    /// </summary>
    public static IInterceptorSubject? FindSubjectInCollection(object value, int index)
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
    public static IInterceptorSubject? FindSubjectInDictionary(object value, object key)
    {
        if (value is IDictionary dictionary)
            return dictionary[key] as IInterceptorSubject;

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is null) continue;
                if (TryGetSubjectFromKeyValuePair(item, out var itemKey, out var subject) && Equals(itemKey, key))
                    return subject;
            }
        }

        return null;
    }

    /// <summary>
    /// Reflects <c>KeyValuePair&lt;,&gt;</c> shape for read-only dictionary fallbacks where
    /// <see cref="IDictionary"/> isn't implemented (custom <see cref="IReadOnlyDictionary{TKey,TValue}"/>
    /// wrappers that opt out of the non-generic dict interface). A single compiled expression-tree
    /// delegate per closed KVP type extracts both Key and Value in one call (one unbox, one
    /// indirect call) instead of two separate delegates.
    /// </summary>
    public static bool TryGetSubjectFromKeyValuePair(object keyValuePair, out object? key, [NotNullWhen(true)] out IInterceptorSubject? subject)
    {
        var accessor = KvpAccessorCache.GetOrAdd(keyValuePair.GetType(), static t =>
        {
            if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(KeyValuePair<,>))
                return null;

            var param = Expression.Parameter(typeof(object), "obj");
            var typed = Expression.Convert(param, t);

            var keyExpression = Expression.Convert(
                Expression.Property(typed, t.GetProperty(nameof(KeyValuePair<int, int>.Key))!),
                typeof(object));
            var valueExpression = Expression.Convert(
                Expression.Property(typed, t.GetProperty(nameof(KeyValuePair<int, int>.Value))!),
                typeof(object));

            var tupleConstructor = typeof(ValueTuple<object?, object?>).GetConstructor([typeof(object), typeof(object)])!;
            var body = Expression.New(tupleConstructor, keyExpression, valueExpression);

            return Expression.Lambda<Func<object, (object? key, object? value)>>(body, param).Compile();
        });

        if (accessor is not null)
        {
            var (k, v) = accessor(keyValuePair);
            if (v is IInterceptorSubject s)
            {
                key = k;
                subject = s;
                return true;
            }
        }

        key = null;
        subject = null;
        return false;
    }
}
