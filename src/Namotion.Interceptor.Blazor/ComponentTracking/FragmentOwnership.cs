using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Namotion.Interceptor.Tracking.Recorder;

// Reads the frames of a render to find the content passed to components that are not a ComponentBase.
#pragma warning disable BL0006

namespace Namotion.Interceptor.Blazor.ComponentTracking;

/// <summary>
/// Makes the content that a render passes to a component that is not a <see cref="ComponentBase"/>, such as a
/// cascading value or section content, record into the component that created it. That component renders again on
/// a change and passes the content again.
/// </summary>
internal static class FragmentOwnership
{
    private static readonly ConcurrentDictionary<Type, bool> _isTracked = new();

    public static void TakeOwnership(RenderTreeBuilder builder, int start, ComponentReadTracker owner)
    {
        var frames = builder.GetFrames();
        var array = frames.Array;
        for (var index = start; index < frames.Count; index++)
        {
            if (array[index].FrameType != RenderTreeFrameType.Component || IsTracked(array[index].ComponentType))
            {
                continue;
            }

            for (var attribute = index + 1; attribute < frames.Count && array[attribute].FrameType == RenderTreeFrameType.Attribute; attribute++)
            {
                if (array[attribute].AttributeValue is RenderFragment fragment && fragment.Target is not OwnedFragment)
                {
                    ComponentBaseAccessors.AttributeValueOf(ref array[attribute]) = owner.GetContentWrapper(fragment);
                }
            }
        }
    }

    private static bool IsTracked(Type componentType) =>
        _isTracked.GetOrAdd(componentType, static type => typeof(ComponentBase).IsAssignableFrom(type));

    /// <summary>Wraps content so that it records into the given component where no tracked component renders it.</summary>
    internal static RenderFragment Wrap(ComponentReadTracker owner, RenderFragment content) => new OwnedFragment(owner, content).Render;

    private sealed class OwnedFragment(ComponentReadTracker owner, RenderFragment fragment) : IReadPropertyRecorderTarget
    {
        public void OnRead(PropertyReference property) => owner.OnContentRead(property);

        public void Render(RenderTreeBuilder builder)
        {
            if (ComponentReadTracker.Current is not null)
            {
                // A tracked component renders the content, for example a cascaded fragment or the body of a layout. It
                // records the reads itself, renders again on their change, and takes ownership of nested content.
                fragment(builder);
                return;
            }

            var start = builder.GetFrames().Count;
            using (ReadPropertyRecorder.RecordOnCurrentThread(this))
            {
                fragment(builder);
            }

            TakeOwnership(builder, start, owner);
        }
    }
}
