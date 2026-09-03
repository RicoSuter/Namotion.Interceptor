using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

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
    private static readonly ConcurrentDictionary<Type, Func<object, object, object?>?> DictionaryLookupCache = new();

    private static readonly Func<Type, Func<object, object, object?>?> BuildDictionaryLookup = static type =>
    {
        var interfaces = type.GetInterfaces();
        var dictionaryInterface =
            Array.Find(interfaces, static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)) ??
            Array.Find(interfaces, static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));

        if (dictionaryInterface is null)
            return null;

        var keyType = dictionaryInterface.GenericTypeArguments[0];
        var valueType = dictionaryInterface.GenericTypeArguments[1];

        var dictionaryParameter = Expression.Parameter(typeof(object), "dictionary");
        var keyParameter = Expression.Parameter(typeof(object), "key");
        var resultVariable = Expression.Variable(valueType, "result");

        // key is TKey && ((IDictionary<TKey, TValue>)dictionary).TryGetValue((TKey)key, out result)
        //     ? (object)result : null
        var body = Expression.Block(
            [resultVariable],
            Expression.Condition(
                Expression.AndAlso(
                    Expression.TypeIs(keyParameter, keyType),
                    Expression.Call(
                        Expression.Convert(dictionaryParameter, dictionaryInterface),
                        dictionaryInterface.GetMethod(nameof(IDictionary<int, int>.TryGetValue))!,
                        Expression.Convert(keyParameter, keyType),
                        resultVariable)),
                Expression.Convert(resultVariable, typeof(object)),
                Expression.Constant(null, typeof(object))));

        return Expression.Lambda<Func<object, object, object?>>(body, dictionaryParameter, keyParameter).Compile();
    };

    /// <summary>
    /// Finds a single subject at the given <paramref name="index"/> inside
    /// a collection <paramref name="value"/>, using <see cref="IList"/>
    /// fast path with <see cref="IEnumerable"/> fallback.
    /// </summary>
    /// <remarks>
    /// The IList fast path is split into its own tiny method body so the JIT can inline
    /// it at every call site. The IEnumerable fallback is extracted into a separate
    /// non-inlined method to keep the entry point under the inlining size budget.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IInterceptorSubject? FindSubjectInCollection(object value, int index)
    {
        if (value is IList list)
            return list[index] as IInterceptorSubject;

        return FindSubjectInCollectionSlow(value, index);
    }

    private static IInterceptorSubject? FindSubjectInCollectionSlow(object value, int index)
    {
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
    /// a dictionary <paramref name="value"/>. Never throws for a key that is absent, of the wrong
    /// type, or null: all three answer <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Generic dictionaries are read through a compiled <c>TryGetValue</c> delegate cached per
    /// runtime type rather than through <see cref="IDictionary"/>'s object indexer, because that
    /// indexer is not uniformly tolerant: <c>ImmutableDictionary</c> and
    /// <c>ImmutableSortedDictionary</c> implement it as an unchecked cast onto the throwing typed
    /// indexer, so they raise <see cref="InvalidCastException"/> for a wrong-typed key and
    /// <see cref="KeyNotFoundException"/> for an absent one where the rest of the BCL answers null.
    /// The indexer is still used for dictionaries with no generic dictionary interface, whose keys
    /// are typed <see cref="object"/> and therefore cannot be rejected by type.
    /// Not force-inlined: the body performs a cache lookup rather than a single type test, so
    /// inlining it into every call site would spend the callers' budget without removing work.
    /// </remarks>
    public static IInterceptorSubject? FindSubjectInDictionary(object value, object key)
    {
        var lookup = DictionaryLookupCache.GetOrAdd(value.GetType(), BuildDictionaryLookup);
        return lookup is not null
            ? lookup(value, key) as IInterceptorSubject
            : FindSubjectInDictionaryUntyped(value, key);
    }

    private static IInterceptorSubject? FindSubjectInDictionaryUntyped(object value, object key)
    {
        // A null key cannot be of the generic path's key type, so that path answers null for it.
        // The object-keyed indexer throws instead, and is guarded to keep the two symmetric.
        if (value is IDictionary dictionary)
            return key is not null ? dictionary[key] as IInterceptorSubject : null;

        // Custom shapes that expose entries only as KeyValuePair sequences.
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
    /// Reflects <c>KeyValuePair&lt;,&gt;</c> shape for dictionary-like values that expose no
    /// dictionary interface at all and can only be read as a sequence of key value pairs.
    /// A single compiled expression-tree delegate per closed KVP type extracts both Key and Value
    /// in one call (one unbox, one indirect call) instead of two separate delegates.
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
