using System.Collections.Concurrent;
using System.Reflection;

#if NET8_0_OR_GREATER
using System.Collections.Frozen;
#endif

namespace Namotion.Interceptor;

/// <summary>
/// Provides cached property metadata for interceptor subject types.
/// Metadata is built once per type using reflection on first access.
/// </summary>
public static class SubjectPropertyMetadataCache
{
#if NET8_0_OR_GREATER
    private static readonly ConcurrentDictionary<Type, FrozenDictionary<string, SubjectPropertyMetadata>> Cache = new();
#else
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, SubjectPropertyMetadata>> Cache = new();
#endif

    /// <summary>
    /// Gets the cached property metadata dictionary for the specified type.
    /// </summary>
    /// <typeparam name="T">The interceptor subject type.</typeparam>
    /// <returns>A read-only dictionary mapping property names to their metadata.</returns>
    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> Get<T>()
        where T : IInterceptorSubject
    {
        return Cache.GetOrAdd(typeof(T), static type => BuildMetadata(type));
    }

#if NET8_0_OR_GREATER
    private static FrozenDictionary<string, SubjectPropertyMetadata> BuildMetadata(Type type)
#else
    private static IReadOnlyDictionary<string, SubjectPropertyMetadata> BuildMetadata(Type type)
#endif
    {
        var result = new Dictionary<string, SubjectPropertyMetadata>();

        // Walk inheritance chain (most derived first)
        var currentType = type;
        while (currentType != null && typeof(IInterceptorSubject).IsAssignableFrom(currentType))
        {
            foreach (var property in GetDeclaredProperties(currentType))
            {
                // Skip explicit interface implementations (they have '.' in the name)
                if (property.Name.Contains('.'))
                    continue;

                if (result.ContainsKey(property.Name))
                    continue; // Most derived wins

                var (getter, setter) = GetGeneratedAccessors(currentType, property.Name);
                if (getter == null && setter == null)
                {
                    (getter, setter) = CreateFallbackAccessors(property);
                }

                result[property.Name] = new SubjectPropertyMetadata(
                    property,
                    getter,
                    setter,
                    isIntercepted: property.GetCustomAttribute<Attributes.InterceptedAttribute>() != null,
                    isDynamic: false);
            }

            currentType = currentType.BaseType;
        }

        // Add interface default implementations
        AddInterfaceDefaultImplementations(type, result);

#if NET8_0_OR_GREATER
        return result.ToFrozenDictionary();
#else
        return result;
#endif
    }

    private static IEnumerable<PropertyInfo> GetDeclaredProperties(Type type)
    {
        return type.GetProperties(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly);
    }

    private static (Func<IInterceptorSubject, object?>?, Action<IInterceptorSubject, object?>?)
        GetGeneratedAccessors(Type type, string propertyName)
    {
        var method = type.GetMethod(
            "__GetPropertyAccessors",
            BindingFlags.NonPublic | BindingFlags.Static);

        if (method == null)
            return (null, null);

        return ((Func<IInterceptorSubject, object?>?, Action<IInterceptorSubject, object?>?))
            method.Invoke(null, new object[] { propertyName })!;
    }

    private static (Func<IInterceptorSubject, object?>?, Action<IInterceptorSubject, object?>?)
        CreateFallbackAccessors(PropertyInfo property)
    {
        return (CreateFallbackGetter(property), CreateFallbackSetter(property));
    }

    private static Func<IInterceptorSubject, object?>? CreateFallbackGetter(PropertyInfo property)
    {
        var getMethod = property.GetGetMethod(true);
        if (getMethod == null) return null;

        // Reflection-based fallback (works on all platforms including AOT)
        return subject => property.GetValue(subject);
    }

    private static Action<IInterceptorSubject, object?>? CreateFallbackSetter(PropertyInfo property)
    {
        var setMethod = property.GetSetMethod(true);
        if (setMethod == null) return null;

        // Reflection-based fallback (works on all platforms including AOT)
        return (subject, value) => property.SetValue(subject, value);
    }

    private static IEnumerable<Type> GetInterfacesSortedByDepth(Type type)
    {
        var interfaces = type.GetInterfaces();
        var interfaceSet = new HashSet<Type>(interfaces);

        // Most derived (highest depth) first - ensures shadowing works correctly
        return interfaces.OrderByDescending(iface =>
            iface.GetInterfaces().Count(i => interfaceSet.Contains(i)));
    }

    private static void AddInterfaceDefaultImplementations(
        Type type,
        Dictionary<string, SubjectPropertyMetadata> result)
    {
        foreach (var iface in GetInterfacesSortedByDepth(type))
        {
            var map = type.GetInterfaceMap(iface);

            foreach (var property in iface.GetProperties())
            {
                if (result.ContainsKey(property.Name))
                    continue;

                var getter = property.GetGetMethod();
                if (getter == null || getter.IsAbstract)
                    continue;

                var index = Array.IndexOf(map.InterfaceMethods, getter);
                if (index < 0 || map.TargetMethods[index].DeclaringType != iface)
                    continue;

                var (getterDelegate, setterDelegate) = GetGeneratedAccessors(type, property.Name);
                if (getterDelegate == null)
                {
                    (getterDelegate, setterDelegate) = CreateInterfaceFallbackAccessors(property, iface);
                }

                result[property.Name] = new SubjectPropertyMetadata(
                    property,
                    getterDelegate,
                    setterDelegate,
                    isIntercepted: false,
                    isDynamic: false);
            }
        }
    }

    private static (Func<IInterceptorSubject, object?>?, Action<IInterceptorSubject, object?>?)
        CreateInterfaceFallbackAccessors(PropertyInfo property, Type interfaceType)
    {
        var getter = CreateInterfaceFallbackGetter(property, interfaceType);
        var setter = CreateInterfaceFallbackSetter(property, interfaceType);
        return (getter, setter);
    }

    private static Func<IInterceptorSubject, object?>? CreateInterfaceFallbackGetter(PropertyInfo property, Type interfaceType)
    {
        var getMethod = property.GetGetMethod(true);
        if (getMethod == null) return null;

        // For interface default implementations, we need to invoke through the interface
        return subject => property.GetValue(subject);
    }

    private static Action<IInterceptorSubject, object?>? CreateInterfaceFallbackSetter(PropertyInfo property, Type interfaceType)
    {
        var setMethod = property.GetSetMethod(true);
        if (setMethod == null) return null;

        return (subject, value) => property.SetValue(subject, value);
    }
}
