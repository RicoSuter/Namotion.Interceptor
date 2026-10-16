using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: MetadataUpdateHandler(typeof(Namotion.Interceptor.Blazor.ComponentTracking.ComponentTrackingActivator))]

namespace Namotion.Interceptor.Blazor.ComponentTracking;

/// <summary>
/// Creates components as the default activator does and makes each <see cref="ComponentBase"/> record the property
/// reads of its own render.
/// </summary>
internal sealed class ComponentTrackingActivator : IComponentActivator
{
    private static readonly ConcurrentDictionary<Type, ObjectFactory> _factories = new();

    private readonly IServiceProvider _serviceProvider;
    private ComponentTrackingDispatcher? _dispatcher;

    public ComponentTrackingActivator(IServiceProvider serviceProvider, ILogger<ComponentTrackingActivator> logger)
    {
        _serviceProvider = serviceProvider;
        if (!ComponentBaseAccessors.IsSupported)
        {
            logger.LogError(
                "Component tracking is not supported by this version of Microsoft.AspNetCore.Components: a private member of ComponentBase changed. Components render as without tracking.");
        }
    }

    /// <summary>
    /// Gets the dispatcher of the scope, created on the first interactive render, so that a renderer that never renders
    /// interactively, such as an <c>HtmlRenderer</c> on the root service provider, never resolves the scoped service.
    /// </summary>
    internal ComponentTrackingDispatcher Dispatcher =>
        _dispatcher ??= _serviceProvider.GetRequiredService<ComponentTrackingDispatcher>();

    public IComponent CreateInstance([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type componentType)
    {
        if (!typeof(IComponent).IsAssignableFrom(componentType))
        {
            throw new ArgumentException($"The type {componentType.FullName} does not implement {nameof(IComponent)}.", nameof(componentType));
        }

        if (!_factories.TryGetValue(componentType, out var factory))
        {
            factory = ActivatorUtilities.CreateFactory(componentType, Type.EmptyTypes);
            _factories.TryAdd(componentType, factory);
        }

        var component = (IComponent)factory(_serviceProvider, []);
        if (component is ComponentBase componentBase && ComponentBaseAccessors.IsSupported)
        {
            ref var renderFragment = ref ComponentBaseAccessors.RenderFragmentOf(componentBase);
            renderFragment = new ComponentReadTracker(componentBase, renderFragment, this).Render;
        }

        return component;
    }

    /// <summary>Called by hot reload, after which a component type can have other constructors.</summary>
    public static void ClearCache(Type[]? updatedTypes) => _factories.Clear();
}
