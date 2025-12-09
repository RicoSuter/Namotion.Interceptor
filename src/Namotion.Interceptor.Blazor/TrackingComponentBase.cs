using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Recorder;

namespace Namotion.Interceptor.Blazor;

/// <summary>
/// A component base that automatically tracks property reads during rendering
/// and only re-renders when tracked properties change.
/// </summary>
/// <remarks>
/// Requires ReadPropertyRecorder to be registered in the context at startup
/// via WithReadPropertyRecorder().
/// </remarks>
public abstract class TrackingComponentBase : ComponentBase, IDisposable
{
    private IDisposable? _subscription;
    private ReadPropertyRecorderScope? _currentScope;

    private ConcurrentDictionary<PropertyReference, bool> _collectingProperties = [];
    private ConcurrentDictionary<PropertyReference, bool> _properties = [];

    /// <summary>
    /// The context to track for property changes.
    /// Implement this property to provide the context from your specific source.
    /// </summary>
    protected abstract IInterceptorSubjectContext? TrackingContext { get; }

    /// <summary>
    /// Gets the properties that were accessed during the last render.
    /// Useful for debugging tracking behavior.
    /// </summary>
    protected IEnumerable<string> TrackedPropertyNames =>
        _properties.Keys.Select(p => $"{p.Subject.GetType().Name}.{p.Name}");

    /// <summary>
    /// Gets the dictionary used for collecting property reads during the current render cycle.
    /// Use this with RegisteredSubjectProperty.GetValueAndRecord() for explicit property tracking
    /// in RenderFragment callbacks where ambient context doesn't flow.
    /// </summary>
    protected ConcurrentDictionary<PropertyReference, bool> PropertyRecorder => _collectingProperties;

    protected override void OnInitialized()
    {
        _subscription = TrackingContext?
            .GetPropertyChangeObservable()
            .Subscribe(change =>
            {
                if (_properties.TryGetValue(change.Property, out _))
                {
                    InvokeAsync(StateHasChanged);
                }
            });
    }

    protected override void OnParametersSet()
    {
        StartRecording();
        base.OnParametersSet();
    }

    protected override bool ShouldRender()
    {
        StartRecording();
        return base.ShouldRender();
    }

    private void StartRecording()
    {
        if (_currentScope == null && TrackingContext != null)
        {
            _collectingProperties.Clear();
            _currentScope = ReadPropertyRecorder.Start(TrackingContext, _collectingProperties);
        }
    }

    protected override void OnAfterRender(bool firstRender)
    {
        StopRecording();

        // Only swap if we collected properties during this render.
        // This prevents losing tracking when a spurious render happens without executing RenderFragments.
        if (_collectingProperties.Count > 0)
        {
            (_properties, _collectingProperties) = (_collectingProperties, _properties);
            _collectingProperties.Clear();
        }

        base.OnAfterRender(firstRender);
    }

    private void StopRecording()
    {
        if (_currentScope != null)
        {
            _currentScope.Dispose();
            _currentScope = null;
        }
    }

    public virtual void Dispose()
    {
        StopRecording();
        _subscription?.Dispose();
        _subscription = null;
        _properties.Clear();
        _collectingProperties.Clear();
    }
}
