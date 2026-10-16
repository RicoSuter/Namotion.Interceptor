using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Namotion.Interceptor.Tracking.Recorder;

// Reads the frames of a render to find the content passed to components that are not a ComponentBase.
#pragma warning disable BL0006

namespace Namotion.Interceptor.Blazor.ComponentTracking;

/// <summary>
/// Records the property reads of one component's renders, and of the content it passes to components that are not
/// a <see cref="ComponentBase"/>, and renders the component again when one of them changes.
/// </summary>
/// <remarks>Accessed on the renderer's dispatcher only.</remarks>
internal sealed class ComponentReadTracker : IReadPropertyRecorderTarget
{
    private readonly ComponentBase _component;
    private readonly RenderFragment _renderFragment;
    private readonly ComponentTrackingDispatcher _dispatcher;

    // The reads since the start of the last render, and those of the period before it. A property is acquired from
    // the dispatcher while it is in either set, so that reads of content rendered after this component's own render
    // (by components that are not a ComponentBase) stay acquired until the next render reads them again.
    private HashSet<PropertyReference>? _previousReads;
    private HashSet<PropertyReference>? _currentReads;
    private bool? _isInteractive;
    private Renderer? _renderer;

    public ComponentReadTracker(ComponentBase component, RenderFragment renderFragment, ComponentTrackingDispatcher dispatcher)
    {
        _component = component;
        _renderFragment = renderFragment;
        _dispatcher = dispatcher;
        Render = RenderCore;
    }

    public RenderFragment Render { get; }

    internal bool IsRenderRequested { get; set; }

    internal int ReadCount => (_previousReads?.Count ?? 0) + (_currentReads?.Count ?? 0);

    /// <summary>Gets a value indicating whether the renderer has not disposed the component yet.</summary>
    public bool IsAlive => _renderer is null || ComponentBaseAccessors.GetComponentState(_renderer, _component) is not null;

    public void OnRead(PropertyReference property)
    {
        _currentReads ??= [];
        if (_currentReads.Add(property) && _previousReads?.Contains(property) != true)
        {
            // Before the value is read, so that a change written after the read is delivered.
            _dispatcher.Acquire(property, this);
        }
    }

    public void RequestRender()
    {
        IsRenderRequested = false;
        // As StateHasChanged does, but without asking ShouldRender: the component's own reasons not to render cannot
        // know that a value its render read has changed.
        ref var hasPendingQueuedRender = ref ComponentBaseAccessors.HasPendingQueuedRenderOf(_component);
        if (hasPendingQueuedRender)
        {
            return;
        }

        hasPendingQueuedRender = true;
        try
        {
            ComponentBaseAccessors.RenderHandleOf(_component).Render(ComponentBaseAccessors.RenderFragmentOf(_component));
        }
        catch
        {
            hasPendingQueuedRender = false;
            throw;
        }
    }

    public void ReleaseAll()
    {
        Release(_previousReads, keep: null);
        Release(_currentReads, keep: null);
        _previousReads = null;
        _currentReads = null;
    }

    private void RenderCore(RenderTreeBuilder builder)
    {
        if (!IsInteractive())
        {
            using (ReadPropertyRecorder.RecordOnCurrentThread(DiscardingTarget.Instance))
            {
                _renderFragment(builder);
            }

            return;
        }

        _dispatcher.OnRenderStarting(this);
        Rotate();

        var start = builder.GetFrames().Count;
        using (ReadPropertyRecorder.RecordOnCurrentThread(this))
        {
            _renderFragment(builder);
        }

        FragmentOwnership.TakeOwnership(builder, start, this);
    }

    private void Rotate()
    {
        if (_previousReads is null && _currentReads is null)
        {
            return;
        }

        Release(_previousReads, keep: _currentReads);
        _previousReads?.Clear();
        (_previousReads, _currentReads) = (_currentReads, _previousReads);
    }

    private void Release(HashSet<PropertyReference>? reads, HashSet<PropertyReference>? keep)
    {
        if (reads is null)
        {
            return;
        }

        foreach (var property in reads)
        {
            if (keep?.Contains(property) != true)
            {
                _dispatcher.Release(property, this);
            }
        }
    }

    private bool IsInteractive()
    {
        if (_isInteractive is null)
        {
            ref var renderHandle = ref ComponentBaseAccessors.RenderHandleOf(_component);
            _isInteractive = renderHandle.RendererInfo.IsInteractive;
            _renderer = ComponentBaseAccessors.RendererOf(ref renderHandle);
            _dispatcher.Attach(renderHandle.Dispatcher);
        }

        return _isInteractive == true;
    }

    private sealed class DiscardingTarget : IReadPropertyRecorderTarget
    {
        public static readonly DiscardingTarget Instance = new();

        public void OnRead(PropertyReference property)
        {
        }
    }
}
