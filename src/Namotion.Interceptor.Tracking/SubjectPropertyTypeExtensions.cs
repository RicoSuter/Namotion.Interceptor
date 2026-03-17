using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking;

/// <summary>
/// Cached type checks for determining whether a property type can contain interceptor subjects.
/// Used as a fast pre-filter to avoid unnecessary work on types that cannot hold subjects
/// (e.g., primitives, strings, simple value types).
/// </summary>
public static class SubjectPropertyTypeExtensions
{
    private static readonly ConcurrentDictionary<Type, bool> CanContainSubjectsCache = new();
    private static readonly ConcurrentDictionary<Type, bool> IsSubjectReferenceTypeCache = new();
    private static readonly ConcurrentDictionary<Type, bool> IsSubjectCollectionTypeCache = new();
    private static readonly ConcurrentDictionary<Type, bool> IsSubjectDictionaryTypeCache = new();

    /// <summary>
    /// Checks whether a property can contain subjects, using both a JIT-constant fast path
    /// via <typeparamref name="TProperty"/> and a runtime fallback via the declared
    /// <paramref name="type"/>. <typeparamref name="TProperty"/> is a hint — it may be
    /// <c>object</c> when values are boxed through non-generic paths (this applies to
    /// <c>TProperty</c> throughout the interceptor interfaces, not just here).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanContainSubjects<TProperty>(this Type type)
    {
        if (typeof(TProperty).IsPrimitive ||
            typeof(TProperty) == typeof(decimal) ||
            typeof(TProperty) == typeof(string) ||
            typeof(TProperty) == typeof(DateTime) ||
            typeof(TProperty) == typeof(DateTimeOffset) ||
            typeof(TProperty) == typeof(TimeSpan) ||
            typeof(TProperty) == typeof(Guid))
        {
            return false;
        }

        return type.CanContainSubjects();
    }

    /// <summary>
    /// Returns true if the given type could potentially contain or be an <see cref="IInterceptorSubject"/>.
    /// Results are cached per type for O(1) lookups after the first call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanContainSubjects(this Type type)
    {
        return CanContainSubjectsCache.TryGetValue(type, out var result)
            ? result
            : CanContainSubjectsSlow(type);
    }

    /// <summary>
    /// Returns true if the given type is a subject reference type (interface, object, or IInterceptorSubject).
    /// Results are cached per type for O(1) lookups after the first call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSubjectReferenceType(this Type type)
    {
        return IsSubjectReferenceTypeCache.TryGetValue(type, out var result)
            ? result
            : IsSubjectReferenceTypeSlow(type);
    }

    /// <summary>
    /// Returns true if the given type is a collection that can hold subject reference types.
    /// Uses generic type info when available for precise checks, falls back to non-generic
    /// ICollection check for legacy types (e.g. ArrayList).
    /// Results are cached per type for O(1) lookups after the first call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSubjectCollectionType(this Type type)
    {
        return IsSubjectCollectionTypeCache.TryGetValue(type, out var result)
            ? result
            : IsSubjectCollectionTypeSlow(type);
    }

    /// <summary>
    /// Returns true if the given type is a dictionary that can hold subject reference values.
    /// Uses generic type info when available for precise checks, falls back to non-generic
    /// IDictionary check for legacy types (e.g. Hashtable).
    /// Results are cached per type for O(1) lookups after the first call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSubjectDictionaryType(this Type type)
    {
        return IsSubjectDictionaryTypeCache.TryGetValue(type, out var result)
            ? result
            : IsSubjectDictionaryTypeSlow(type);
    }

    private static bool CanContainSubjectsSlow(Type type)
    {
        return CanContainSubjectsCache.GetOrAdd(type, static t =>
            t.IsSubjectReferenceType() ||
            t.IsSubjectCollectionType() ||
            t.IsSubjectDictionaryType());
    }

    private static bool IsSubjectReferenceTypeSlow(Type type)
    {
        return IsSubjectReferenceTypeCache.GetOrAdd(type, static t =>
            t.IsInterface ||
            t == typeof(object) ||
            typeof(IInterceptorSubject).IsAssignableFrom(t));
    }

    private static bool IsSubjectCollectionTypeSlow(Type type)
    {
        return IsSubjectCollectionTypeCache.GetOrAdd(type, static t =>
        {
            if (!typeof(IEnumerable).IsAssignableFrom(t))
                return false;

            var genericEnumerables = GetGenericEnumerableInterfaces(t);

            // If generic type info is available, use it for precise check
            if (genericEnumerables.Length > 0)
                return genericEnumerables.Any(static i => i.GenericTypeArguments[0].IsSubjectReferenceType());

            // No generic type info (e.g. ArrayList) — fall back to non-generic check
            return typeof(ICollection).IsAssignableFrom(t);
        });
    }

    private static bool IsSubjectDictionaryTypeSlow(Type type)
    {
        return IsSubjectDictionaryTypeCache.GetOrAdd(type, static t =>
        {
            if (!typeof(IEnumerable).IsAssignableFrom(t))
                return false;

            var genericEnumerables = GetGenericEnumerableInterfaces(t);

            // If generic type info is available, use it for precise check
            if (genericEnumerables.Length > 0)
            {
                return genericEnumerables.Any(static i =>
                    i.GenericTypeArguments[0] is { IsGenericType: true } kvType &&
                    kvType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) &&
                    kvType.GenericTypeArguments[1].IsSubjectReferenceType());
            }

            // No generic type info (e.g. Hashtable) — fall back to non-generic check
            return typeof(IDictionary).IsAssignableFrom(t);
        });
    }

    private static Type[] GetGenericEnumerableInterfaces(Type type)
    {
        return Array.FindAll(type.GetInterfaces(),
            static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
    }
}
