using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;

// Reads the frames of a render to find the content passed to components that are not a ComponentBase.
#pragma warning disable BL0006

namespace Namotion.Interceptor.Blazor.ComponentTracking;

/// <summary>
/// Accesses private framework members without reflection. Each member is verified once by <see cref="IsSupported"/>.
/// </summary>
internal static class ComponentBaseAccessors
{
    private static readonly Lazy<bool> _isSupported = new(Verify);

    public static bool IsSupported => _isSupported.Value;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_renderFragment")]
    public static extern ref RenderFragment RenderFragmentOf(ComponentBase component);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_renderHandle")]
    public static extern ref RenderHandle RenderHandleOf(ComponentBase component);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_hasPendingQueuedRender")]
    public static extern ref bool HasPendingQueuedRenderOf(ComponentBase component);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_renderer")]
    public static extern ref Renderer RendererOf(ref RenderHandle renderHandle);

    // Returns null once the renderer has disposed the component.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetComponentState")]
    public static extern Microsoft.AspNetCore.Components.Rendering.ComponentState? GetComponentState(Renderer renderer, IComponent component);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "AttributeValueField")]
    public static extern ref object AttributeValueOf(ref RenderTreeFrame frame);

    private static bool Verify()
    {
        try
        {
            var component = new ProbeComponent();
            _ = RenderFragmentOf(component);
            _ = HasPendingQueuedRenderOf(component);
            var renderHandle = RenderHandleOf(component);
            _ = RendererOf(ref renderHandle);
            var frame = default(RenderTreeFrame);
            _ = AttributeValueOf(ref frame);
            try
            {
                _ = GetComponentState(null!, component);
            }
            catch (NullReferenceException)
            {
                // The method exists: it was called on the null renderer of the probe.
            }

            return true;
        }
        catch (MissingMemberException)
        {
            return false;
        }
    }

    private sealed class ProbeComponent : ComponentBase;
}
