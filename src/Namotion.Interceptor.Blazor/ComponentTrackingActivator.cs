using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Blazor;

/// <summary>
/// Creates components as the default activator does and makes each <see cref="ComponentBase"/> record the property
/// reads of its own render.
/// </summary>
internal sealed class ComponentTrackingActivator : IComponentActivator
{
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
    /// Gets the dispatcher of the scope, resolved on first access.
    /// </summary>
    // Resolved lazily, on the first interactive render, so that a renderer that never renders interactively, such as an
    // HtmlRenderer on the root service provider, never resolves the scoped service.
    internal ComponentTrackingDispatcher Dispatcher =>
        _dispatcher ??= _serviceProvider.GetRequiredService<ComponentTrackingDispatcher>();

    public IComponent CreateInstance([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type componentType)
    {
        if (!typeof(IComponent).IsAssignableFrom(componentType))
        {
            throw new ArgumentException($"The type {componentType.FullName} does not implement {nameof(IComponent)}.", nameof(componentType));
        }

        var factory = ComponentFactoryCache.Get(componentType);
        var component = (IComponent)factory(_serviceProvider, []);
        if (component is ComponentBase componentBase && ComponentBaseAccessors.IsSupported)
        {
            ref var renderFragment = ref ComponentBaseAccessors.RenderFragmentOf(componentBase);
            renderFragment = new ComponentReadTracker(componentBase, renderFragment, this).Render;
        }

        return component;
    }
}
