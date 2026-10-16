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
    private readonly ComponentTrackingActivator _activator;
    private ComponentTrackingDispatcher? _dispatcher;

    [ThreadStatic]
    private static ComponentReadTracker? _current;

    // The wrappers of content that this component passes to components that are not a ComponentBase, by the content
    // they wrap, for this render and the one before. A wrapper is reused while its content is equal, so that a
    // component that compares or keys by its content, as SectionOutlet does, sees the same delegate.
    private Dictionary<RenderFragment, RenderFragment>? _contentWrappers;
    private Dictionary<RenderFragment, RenderFragment>? _previousContentWrappers;

    // The properties acquired from the dispatcher, the reads of the render in progress or the last one, and the reads of
    // content that components which are not a ComponentBase rendered for this one after its last render, and before it.
    // Content rendered after the render reads after it, so its reads stay acquired until the render after the next one.
    private HashSet<PropertyReference>? _acquired;
    private HashSet<PropertyReference>? _renderReads;
    private HashSet<PropertyReference>? _contentReads;
    private HashSet<PropertyReference>? _previousContentReads;
    private bool? _isInteractive;
    private Renderer? _renderer;

    public ComponentReadTracker(ComponentBase component, RenderFragment renderFragment, ComponentTrackingActivator activator)
    {
        _component = component;
        _renderFragment = renderFragment;
        _activator = activator;
        Render = RenderCore;
    }

    public RenderFragment Render { get; }

    /// <summary>Gets the tracker whose component renders on the current thread, if any.</summary>
    internal static ComponentReadTracker? Current => _current;

    /// <summary>Gets the wrapper of the given content that records into this component, reusing the one of the last render.</summary>
    internal RenderFragment GetContentWrapper(RenderFragment content)
    {
        if (_contentWrappers?.TryGetValue(content, out var wrapper) == true)
        {
            return wrapper;
        }

        if (_previousContentWrappers is null || !_previousContentWrappers.TryGetValue(content, out wrapper))
        {
            wrapper = FragmentOwnership.Wrap(this, content);
        }

        (_contentWrappers ??= [])[content] = wrapper;
        return wrapper;
    }

    internal bool IsRenderRequested { get; set; }

    /// <summary>Gets a value indicating whether the renderer has not disposed the component yet.</summary>
    public bool IsAlive => _renderer is null || ComponentBaseAccessors.GetComponentState(_renderer, _component) is not null;

    public void OnRead(PropertyReference property)
    {
        (_renderReads ??= []).Add(property);
        Acquire(property);
    }

    /// <summary>Records a read of content that this component passed to a component that is not a ComponentBase.</summary>
    public void OnContentRead(PropertyReference property)
    {
        (_contentReads ??= []).Add(property);
        Acquire(property);
    }

    private void Acquire(PropertyReference property)
    {
        if ((_acquired ??= []).Add(property))
        {
            // Before the value is read, so that a change written after the read is delivered.
            _dispatcher!.Acquire(property, this);
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
        if (_acquired is not null)
        {
            foreach (var property in _acquired)
            {
                _dispatcher!.Release(property, this);
            }
        }

        _acquired = null;
        _renderReads = null;
        _contentReads = null;
        _previousContentReads = null;
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

        _dispatcher!.OnRenderStarting(this);
        _renderReads?.Clear();
        (_previousContentReads, _contentReads) = (_contentReads, _previousContentReads);
        _contentReads?.Clear();
        (_previousContentWrappers, _contentWrappers) = (_contentWrappers, _previousContentWrappers);
        _contentWrappers?.Clear();

        var start = builder.GetFrames().Count;
        var previous = _current;
        _current = this;
        try
        {
            using (ReadPropertyRecorder.RecordOnCurrentThread(this))
            {
                _renderFragment(builder);
            }
        }
        finally
        {
            _current = previous;
        }

        // Not reached by a render that throws, which keeps what it acquired for the output that the renderer keeps.
        FragmentOwnership.TakeOwnership(builder, start, this);
        ReleaseUnread();
    }

    // Releases what neither this render nor the content rendered after the last one read.
    private void ReleaseUnread()
    {
        if (_acquired is null)
        {
            return;
        }

        List<PropertyReference>? unread = null;
        foreach (var property in _acquired)
        {
            if (_renderReads?.Contains(property) != true && _previousContentReads?.Contains(property) != true)
            {
                (unread ??= []).Add(property);
            }
        }

        if (unread is not null)
        {
            foreach (var property in unread)
            {
                _acquired.Remove(property);
                _dispatcher!.Release(property, this);
            }
        }
    }

    private bool IsInteractive()
    {
        if (_isInteractive is null)
        {
            ref var renderHandle = ref ComponentBaseAccessors.RenderHandleOf(_component);
            _isInteractive = renderHandle.RendererInfo.IsInteractive;
            if (_isInteractive == true)
            {
                _renderer = ComponentBaseAccessors.RendererOf(ref renderHandle);
                _dispatcher = _activator.Dispatcher;
                _dispatcher.Attach(renderHandle.Dispatcher);
            }
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
