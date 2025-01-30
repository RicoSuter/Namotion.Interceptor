using Microsoft.AspNetCore.Components;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Recorder;

namespace Namotion.Interceptor.Blazor;

public class ProxyComponentBase<TSubject> : ComponentBase, IDisposable
    where TSubject : IInterceptorSubject
{
    private IDisposable? _subscription;
    private ReadPropertyRecorderScope? _recorder;
        
    private PropertyReference[]? _properties;

    [Inject]
    public IObservable<PropertyChangedContext>? PropertyChangedObservable { get; set; }

    [Inject]
    public TSubject? Subject { get; set; }
        
    [Inject]
    public ReadPropertyRecorder? Recorder { get; set; }

    protected override void OnInitialized()
    {
        _subscription = PropertyChangedObservable!
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
            _recorder = Recorder?.StartRecordingPropertyReadCalls();
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