using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

/// <summary>
/// Cached type checks for determining whether a property type can contain interceptor subjects.
/// </summary>
public static class SubjectPropertyTypeClassifier
{
    private static readonly ConcurrentDictionary<Type, bool> CanContainSubjectsCache = new();
    private static readonly ConcurrentDictionary<Type, bool> IsSubjectReferenceTypeCache = new();
    private static readonly ConcurrentDictionary<Type, bool> IsSubjectCollectionTypeCache = new();
    private static readonly ConcurrentDictionary<Type, bool> IsSubjectDictionaryTypeCache = new();

    /// <summary>
    /// Returns true if the given type can contain interceptor subjects.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanContainSubjects(Type type)
    {
        return CanContainSubjectsCache.TryGetValue(type, out var result)
            ? result
            : CanContainSubjectsSlow(type);
    }

    /// <summary>
    /// Returns true if the given type is a single subject reference.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSubjectReferenceType(Type type)
    {
        return IsSubjectReferenceTypeCache.TryGetValue(type, out var result)
            ? result
            : IsSubjectReferenceTypeSlow(type);
    }

    /// <summary>
    /// Returns true if the given type is a collection of subject references.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSubjectCollectionType(Type type)
    {
        return IsSubjectCollectionTypeCache.TryGetValue(type, out var result)
            ? result
            : IsSubjectCollectionTypeSlow(type);
    }

    /// <summary>
    /// Returns true if the given type is a dictionary with subject reference values.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSubjectDictionaryType(Type type)
    {
        return IsSubjectDictionaryTypeCache.TryGetValue(type, out var result)
            ? result
            : IsSubjectDictionaryTypeSlow(type);
    }

    private static bool CanContainSubjectsSlow(Type type)
    {
        return CanContainSubjectsCache.GetOrAdd(type, static candidate =>
            IsSubjectReferenceType(candidate) ||
            IsSubjectCollectionType(candidate) ||
            IsSubjectDictionaryType(candidate));
    }

    private static bool IsSubjectReferenceTypeSlow(Type type)
    {
        return IsSubjectReferenceTypeCache.GetOrAdd(type, static candidate =>
        {
            if (typeof(IInterceptorSubject).IsAssignableFrom(candidate))
            {
                return true;
            }

            return CanDirectlyHoldSubject(candidate) &&
                   !IsSubjectDictionaryType(candidate) &&
                   !IsSubjectCollectionType(candidate);
        });
    }

    private static bool IsSubjectCollectionTypeSlow(Type type)
    {
        return IsSubjectCollectionTypeCache.GetOrAdd(type, static candidate =>
        {
            if (typeof(IInterceptorSubject).IsAssignableFrom(candidate) ||
                IsSubjectDictionaryType(candidate) ||
                !typeof(IEnumerable).IsAssignableFrom(candidate))
            {
                return false;
            }

            var genericEnumerables = GetEnumerablesIncludingSelf(candidate);
            if (genericEnumerables.Length > 0)
            {
                return genericEnumerables.Any(static enumerable =>
                    IsCandidateElementType(enumerable.GenericTypeArguments[0]));
            }

            return typeof(ICollection).IsAssignableFrom(candidate);
        });
    }

    private static bool IsSubjectDictionaryTypeSlow(Type type)
    {
        return IsSubjectDictionaryTypeCache.GetOrAdd(type, static candidate =>
        {
            if (typeof(IInterceptorSubject).IsAssignableFrom(candidate))
            {
                return false;
            }

            if (!typeof(IDictionary).IsAssignableFrom(candidate) &&
                !ImplementsGenericInterfaceDefinition(candidate, typeof(IDictionary<,>)) &&
                !ImplementsGenericInterfaceDefinition(candidate, typeof(IReadOnlyDictionary<,>)))
            {
                return false;
            }

            var genericEnumerables = GetEnumerablesIncludingSelf(candidate);
            if (genericEnumerables.Length > 0)
            {
                return genericEnumerables.Any(static enumerable =>
                    enumerable.GenericTypeArguments[0] is { IsGenericType: true } keyValuePairType &&
                    keyValuePairType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) &&
                    IsCandidateElementType(keyValuePairType.GenericTypeArguments[1]));
            }

            return true;
        });
    }

    private static bool CanDirectlyHoldSubject(Type type) =>
        (type.IsInterface || type == typeof(object)) &&
        !typeof(IEnumerable).IsAssignableFrom(type);

    private static bool IsCandidateElementType(Type type) =>
        typeof(IInterceptorSubject).IsAssignableFrom(type) || CanDirectlyHoldSubject(type);

    private static bool ImplementsGenericInterfaceDefinition(Type type, Type genericInterfaceDefinition)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == genericInterfaceDefinition)
        {
            return true;
        }

        return type.GetInterfaces().Any(interfaceType =>
            interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == genericInterfaceDefinition);
    }

    private static Type[] GetEnumerablesIncludingSelf(Type type)
    {
        var fromInterfaces = Array.FindAll(type.GetInterfaces(), static interfaceType =>
            interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(IEnumerable<>))
        {
            return fromInterfaces;
        }

        if (fromInterfaces.Length == 0)
        {
            return [type];
        }

        var enriched = new Type[fromInterfaces.Length + 1];
        Array.Copy(fromInterfaces, enriched, fromInterfaces.Length);
        enriched[fromInterfaces.Length] = type;
        return enriched;
    }
}
