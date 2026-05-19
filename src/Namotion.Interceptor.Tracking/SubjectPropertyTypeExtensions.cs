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
    /// <paramref name="type"/>. <typeparamref name="TProperty"/> is a hint: it may be
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
    /// Returns true if the given type is a single subject reference (interface, object, or IInterceptorSubject).
    /// Mutually exclusive with <see cref="IsSubjectCollectionType"/> and <see cref="IsSubjectDictionaryType"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSubjectReferenceType(this Type type)
    {
        return IsSubjectReferenceTypeCache.TryGetValue(type, out var result)
            ? result
            : IsSubjectReferenceTypeSlow(type);
    }

    /// <summary>
    /// Returns true if the given type is a collection of subject references.
    /// Mutually exclusive with <see cref="IsSubjectReferenceType"/> and <see cref="IsSubjectDictionaryType"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSubjectCollectionType(this Type type)
    {
        return IsSubjectCollectionTypeCache.TryGetValue(type, out var result)
            ? result
            : IsSubjectCollectionTypeSlow(type);
    }

    /// <summary>
    /// Returns true if the given type is a dictionary with subject reference values.
    /// Mutually exclusive with <see cref="IsSubjectReferenceType"/> and <see cref="IsSubjectCollectionType"/>.
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
        {
            // A type that explicitly implements IInterceptorSubject is a reference
            // even if it also implements container interfaces; the explicit subject
            // declaration trumps any incidental enumerable nature.
            if (typeof(IInterceptorSubject).IsAssignableFrom(t))
            {
                return !t.IsSubjectDictionaryType() && !t.IsSubjectCollectionType();
            }

            // For plain interfaces and `object`, defer to the element-style check so
            // generic interfaces over non-subject content (e.g. IList<int>,
            // IEnumerable<int>) are correctly rejected as references at the property
            // level too, mirroring how they would be rejected as element types.
            return IsElementSubjectReference(t) &&
                   !t.IsSubjectDictionaryType() &&
                   !t.IsSubjectCollectionType();
        });
    }

    private static bool IsSubjectCollectionTypeSlow(Type type)
    {
        return IsSubjectCollectionTypeCache.GetOrAdd(type, static t =>
        {
            if (t.IsSubjectDictionaryType())
                return false;

            if (!typeof(IEnumerable).IsAssignableFrom(t))
                return false;

            var genericEnumerables = GetEnumerablesIncludingSelf(t);

            // If generic type info is available, use it for precise check.
            if (genericEnumerables.Length > 0)
                return genericEnumerables.Any(static i => IsElementSubjectReference(i.GenericTypeArguments[0]));

            // No generic type info (e.g. ArrayList): fall back to non-generic check.
            return typeof(ICollection).IsAssignableFrom(t);
        });
    }

    private static bool IsSubjectDictionaryTypeSlow(Type type)
    {
        return IsSubjectDictionaryTypeCache.GetOrAdd(type, static t =>
        {
            if (!typeof(IEnumerable).IsAssignableFrom(t))
                return false;

            var genericEnumerables = GetEnumerablesIncludingSelf(t);

            // If generic type info is available, use it for precise check.
            if (genericEnumerables.Length > 0)
            {
                return genericEnumerables.Any(static i =>
                    i.GenericTypeArguments[0] is { IsGenericType: true } kvType &&
                    kvType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) &&
                    IsElementSubjectReference(kvType.GenericTypeArguments[1]));
            }

            // No generic type info (e.g. Hashtable): fall back to non-generic check.
            return typeof(IDictionary).IsAssignableFrom(t);
        });
    }

    // Non-recursive subject-reference predicate used as the leaf check.
    private static bool IsSubjectReferenceCandidate(Type t) =>
        t.IsInterface ||
        t == typeof(object) ||
        typeof(IInterceptorSubject).IsAssignableFrom(t);

    // Non-recursive leaf check so self-referential types do not blow the stack.
    private static bool IsElementSubjectReference(Type element)
    {
        if (!IsSubjectReferenceCandidate(element))
            return false;

        if (!typeof(IEnumerable).IsAssignableFrom(element))
            return true;

        foreach (var i in GetEnumerablesIncludingSelf(element))
        {
            var nestedArg = i.GenericTypeArguments[0];
            if (nestedArg is { IsGenericType: true } kv &&
                kv.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) &&
                IsSubjectReferenceCandidate(kv.GenericTypeArguments[1]))
            {
                return false;
            }
            if (IsSubjectReferenceCandidate(nestedArg))
            {
                return false;
            }
        }

        // Enumerable with no candidate content (e.g. IList<int>): a container, not a reference.
        return false;
    }

    private static Type[] GetGenericEnumerableInterfaces(Type type)
    {
        return Array.FindAll(type.GetInterfaces(),
            static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
    }

    // GetInterfaces() never returns the type itself; for a bare IEnumerable<X> we
    // include `type` explicitly so element probing covers that case.
    private static Type[] GetEnumerablesIncludingSelf(Type type)
    {
        var fromInterfaces = GetGenericEnumerableInterfaces(type);
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(IEnumerable<>))
        {
            return fromInterfaces;
        }

        var enriched = new Type[fromInterfaces.Length + 1];
        Array.Copy(fromInterfaces, enriched, fromInterfaces.Length);
        enriched[fromInterfaces.Length] = type;
        return enriched;
    }
}
