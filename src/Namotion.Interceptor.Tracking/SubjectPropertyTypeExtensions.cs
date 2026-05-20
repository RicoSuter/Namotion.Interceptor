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
    // Factories transitively invoke sibling caches; concurrent GetOrAdd races are safe
    // because the classifiers are pure functions of Type.
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
    /// Returns true if the given type is a single subject reference: an <see cref="IInterceptorSubject"/>,
    /// <see cref="object"/>, or a plain interface that could carry a subject. Generic interfaces over
    /// non-subject content (e.g. <c>IList&lt;int&gt;</c>) and enumerable subjects are excluded.
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
            if (typeof(IInterceptorSubject).IsAssignableFrom(t))
            {
                return true;
            }

            return CanDirectlyHoldSubject(t) &&
                   !t.IsSubjectDictionaryType() &&
                   !t.IsSubjectCollectionType();
        });
    }

    private static bool IsSubjectCollectionTypeSlow(Type type)
    {
        return IsSubjectCollectionTypeCache.GetOrAdd(type, static t =>
        {
            if (typeof(IInterceptorSubject).IsAssignableFrom(t))
                return false;

            if (t.IsSubjectDictionaryType())
                return false;

            if (!typeof(IEnumerable).IsAssignableFrom(t))
                return false;

            var genericEnumerables = GetEnumerablesIncludingSelf(t);

            if (genericEnumerables.Length > 0)
                return genericEnumerables.Any(static i => CanDirectlyHoldSubject(i.GenericTypeArguments[0]));

            // No generic type info (e.g. ArrayList)
            return typeof(ICollection).IsAssignableFrom(t);
        });
    }

    private static bool IsSubjectDictionaryTypeSlow(Type type)
    {
        return IsSubjectDictionaryTypeCache.GetOrAdd(type, static t =>
        {
            if (typeof(IInterceptorSubject).IsAssignableFrom(t))
                return false;

            // Require a real dictionary interface. Bare IEnumerable<KeyValuePair<,>> is not
            // classified as dict because the runtime handler dispatches via IDictionary; without
            // an actual dict interface a value like List<KVP<K, Subject>> would silently be
            // treated as a plain collection (master parity). Classifier and handler must agree.
            if (!typeof(IDictionary).IsAssignableFrom(t) &&
                !ImplementsGenericInterfaceDefinition(t, typeof(IDictionary<,>)) &&
                !ImplementsGenericInterfaceDefinition(t, typeof(IReadOnlyDictionary<,>)))
            {
                return false;
            }

            var genericEnumerables = GetEnumerablesIncludingSelf(t);

            if (genericEnumerables.Length > 0)
            {
                return genericEnumerables.Any(static i =>
                    i.GenericTypeArguments[0] is { IsGenericType: true } kvType &&
                    kvType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) &&
                    CanDirectlyHoldSubject(kvType.GenericTypeArguments[1]));
            }

            // No generic type info (e.g. Hashtable)
            return typeof(IDictionary).IsAssignableFrom(t);
        });
    }

    private static bool CanDirectlyHoldSubject(Type t) =>
        (t.IsInterface || t == typeof(object) || typeof(IInterceptorSubject).IsAssignableFrom(t)) &&
        !typeof(IEnumerable).IsAssignableFrom(t);

    private static bool ImplementsGenericInterfaceDefinition(Type type, Type genericInterfaceDefinition)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == genericInterfaceDefinition)
            return true;

        foreach (var i in type.GetInterfaces())
        {
            if (i.IsGenericType && i.GetGenericTypeDefinition() == genericInterfaceDefinition)
                return true;
        }

        return false;
    }

    private static Type[] GetGenericEnumerableInterfaces(Type type)
    {
        return Array.FindAll(type.GetInterfaces(),
            static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
    }

    // GetInterfaces() does not return the type itself, so for a bare IEnumerable<X>
    // property type we include it explicitly.
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
