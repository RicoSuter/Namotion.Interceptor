using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Namotion.Interceptor;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Cached type checks for determining whether a property type can contain interceptor subjects.
/// Used as a fast pre-filter to avoid unnecessary work on types that cannot hold subjects
/// (e.g., primitives, strings, simple value types).
/// </summary>
internal static class SubjectPropertyTypeExtensions
{
    private static readonly ConcurrentDictionary<Type, bool> _canContainSubjects = new();

    /// <summary>
    /// Returns true if the given type could potentially contain or be an <see cref="IInterceptorSubject"/>.
    /// Results are cached per type for O(1) lookups after the first call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool CanContainSubjects(this Type type)
    {
        return _canContainSubjects.TryGetValue(type, out var result)
            ? result
            : CanContainSubjectsSlow(type);
    }

    private static bool CanContainSubjectsSlow(Type type)
    {
        return _canContainSubjects.GetOrAdd(type, static t =>
            t.IsInterface ||
            typeof(IInterceptorSubject).IsAssignableFrom(t) ||
            typeof(ICollection).IsAssignableFrom(t) ||
            typeof(IDictionary).IsAssignableFrom(t) ||
            t == typeof(object) ||
            HasSubjectEnumerableElements(t));
    }

    private static bool HasSubjectEnumerableElements(Type type)
    {
        if (!typeof(IEnumerable).IsAssignableFrom(type))
        {
            return false;
        }

        return type.GetInterfaces().Any(static iface =>
            iface.IsGenericType &&
            iface.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
            IsSubjectElementType(iface.GenericTypeArguments[0]));
    }

    private static bool IsSubjectElementType(Type type)
    {
        return type.IsInterface ||
               type == typeof(object) ||
               typeof(IInterceptorSubject).IsAssignableFrom(type);
    }
}
