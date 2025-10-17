using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Recorder;

namespace Namotion.Interceptor.Blazor;

public class TrackingComponentBase<TSubject> : ComponentBase, IDisposable
    where TSubject : IInterceptorSubject
{
    private readonly Lock _lock = new();

    private IDisposable? _subscription;

    private HashSet<PropertyReference> _scopeProperties = [];
    private HashSet<PropertyReference> _properties = [];
    private ReadPropertyRecorder? _recorder;

    [Inject]
    public TSubject? Subject { get; set; }

    protected override void OnInitialized()
    {
        _subscription = Subject?
            .Context
            .GetPropertyChangedObservable()
            .Subscribe(change =>
            {
                if (_properties?.Contains(change.Property) == true)
                {
                    InvokeAsync(StateHasChanged);
                }
            });
        
        _recorder = Subject?
            .Context
            .GetService<ReadPropertyRecorder>() ?? throw new InvalidOperationException("The ReadPropertyRecorder service not found.");

        var field = typeof(ComponentBase).GetField("_renderFragment", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(this) is RenderFragment renderFragment)
        {
            void WrappedRenderFragment(RenderTreeBuilder builder)
            {
                lock (_lock)
                {
                    var recorderScope = _recorder.StartPropertyAccessRecording(_scopeProperties);
            
                    renderFragment(builder);

                    var previousProperties = _properties;
                    _properties = recorderScope.GetPropertiesAndDispose();
                    _scopeProperties = previousProperties;
                }
            }

            field.SetValue(this, (RenderFragment)WrappedRenderFragment);
        }
    }

    public virtual void Dispose()
    {
        _subscription?.Dispose();
    }
}