using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Blazor.ComponentTracking;

/// <summary>
/// Creates components as the default activator does and makes each <see cref="ComponentBase"/> record the property
/// reads of its own render.
/// </summary>
internal sealed class ComponentTrackingActivator : IComponentActivator
{
    private static readonly ConcurrentDictionary<Type, ObjectFactory> _factories = new();

    private readonly IServiceProvider _serviceProvider;
    private readonly ComponentTrackingDispatcher _dispatcher;

    public ComponentTrackingActivator(IServiceProvider serviceProvider, ComponentTrackingDispatcher dispatcher, ILogger<ComponentTrackingActivator> logger)
    {
        _serviceProvider = serviceProvider;
        _dispatcher = dispatcher;
        if (!ComponentBaseAccessors.IsSupported)
        {
            logger.LogError(
                "Component tracking is not supported by this version of Microsoft.AspNetCore.Components: a private member of ComponentBase changed. Components render as without tracking.");
        }
    }

    public IComponent CreateInstance([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type componentType)
    {
        if (!typeof(IComponent).IsAssignableFrom(componentType))
        {
            throw new ArgumentException($"The type {componentType.FullName} does not implement {nameof(IComponent)}.", nameof(componentType));
        }

        var factory = _factories.GetOrAdd(componentType, static type => ActivatorUtilities.CreateFactory(type, Type.EmptyTypes));
        var component = (IComponent)factory(_serviceProvider, []);
        Track(component, _dispatcher);
        return component;
    }

    internal static void Track(IComponent component, ComponentTrackingDispatcher dispatcher)
    {
        if (component is ComponentBase componentBase && ComponentBaseAccessors.IsSupported)
        {
            ref var renderFragment = ref ComponentBaseAccessors.RenderFragmentOf(componentBase);
            renderFragment = new ComponentReadTracker(componentBase, renderFragment, dispatcher).Render;
        }
    }
}
