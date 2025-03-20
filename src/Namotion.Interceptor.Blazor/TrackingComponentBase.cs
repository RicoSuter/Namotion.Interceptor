using Microsoft.AspNetCore.Components;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Recorder;

namespace Namotion.Interceptor.Blazor;

public class TrackingComponentBase<TSubject> : ComponentBase, IDisposable
    where TSubject : IInterceptorSubject
{
    private IDisposable? _subscription;
    private ReadPropertyRecorderScope? _recorder;
        
    private PropertyReference[]? _properties;

    [Inject]
    public TSubject? Subject { get; set; }
        
    [Inject]
    public ReadPropertyRecorder? Recorder { get; set; }

    protected override void OnInitialized()
    {
        _subscription = Subject?
            .Context
            .GetPropertyChangedObservable()
            .Subscribe(change =>
            {
                if (_properties?.Any(p => p == change.Property) != false)
                {
                    InvokeAsync(StateHasChanged);
                }
            });
    }

    protected override bool ShouldRender()
    {
        var result = base.ShouldRender();
        if (result)
        {
            _recorder = Recorder?.StartPropertyAccessRecording();
        }

        return result;
    }

    protected override void OnAfterRender(bool firstRender)
    {
        _properties = _recorder?.GetPropertiesAndDispose();
    }

    public virtual void Dispose()
    {
        _subscription?.Dispose();
        _recorder?.Dispose();
    }
}