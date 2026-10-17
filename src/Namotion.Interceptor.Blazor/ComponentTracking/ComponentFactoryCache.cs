using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using Microsoft.Extensions.DependencyInjection;

[assembly: MetadataUpdateHandler(typeof(Namotion.Interceptor.Blazor.ComponentTracking.ComponentFactoryCache.HotReloadHandler))]

namespace Namotion.Interceptor.Blazor.ComponentTracking;

/// <summary>
/// The factories that create components, one per component type, as the default activator keeps them.
/// </summary>
internal static class ComponentFactoryCache
{
    private static readonly ConcurrentDictionary<Type, ObjectFactory> _factories = new();

    public static ObjectFactory Get([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type componentType)
    {
        if (!_factories.TryGetValue(componentType, out var factory))
        {
            factory = ActivatorUtilities.CreateFactory(componentType, Type.EmptyTypes);
            _factories.TryAdd(componentType, factory);
        }

        return factory;
    }

    // A type of its own, because hot reload reaches the handler through reflection, which must not reach the annotated Get.
    internal static class HotReloadHandler
    {
        /// <summary>Called by hot reload, after which a component type can have other constructors.</summary>
        public static void ClearCache(Type[]? updatedTypes) => _factories.Clear();
    }
}
