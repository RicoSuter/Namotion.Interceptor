using Microsoft.AspNetCore.Components;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Recorder;

namespace Namotion.Interceptor.Blazor;

public class TrackingComponentBase<TSubject> : ComponentBase, IDisposable
    where TSubject : IInterceptorSubject
{
    private IDisposable? _subscription;
    private ReadPropertyRecorderScope? _recorderScope;
        
    private PropertyReference[]? _properties;
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
            .GetService<ReadPropertyRecorder>();
    }

    protected override bool ShouldRender()
    {
        var result = base.ShouldRender();
        if (result)
        {
            _recorderScope = _recorder?.StartPropertyAccessRecording();
        }

        return result;
    }

    protected override void OnAfterRender(bool firstRender)
    {
        _properties = _recorderScope?.GetPropertiesAndDispose();
    }

    public virtual void Dispose()
    {
        _subscription?.Dispose();
        _recorderScope?.Dispose();
    }
}