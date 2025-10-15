using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Recorder;

namespace Namotion.Interceptor.Blazor;

public class TrackingComponentBase<TSubject> : ComponentBase, IDisposable
    where TSubject : IInterceptorSubject
{
    private IDisposable? _subscription;
    private ReadPropertyRecorderScope? _recorderScope;

    private HashSet<PropertyReference>? _properties;
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

        var field = typeof(ComponentBase).GetField("_renderFragment", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(this) is RenderFragment renderFragment)
        {
            void WrappedRenderFragment(RenderTreeBuilder builder)
            {
                _recorderScope?.Dispose();
                _recorderScope = _recorder?.StartPropertyAccessRecording();
                renderFragment(builder);
                _properties = (_recorderScope?.GetPropertiesAndDispose() ?? []).ToHashSet();
            }

            field.SetValue(this, (RenderFragment)WrappedRenderFragment);
        }
    }

    public virtual void Dispose()
    {
        _subscription?.Dispose();
        _recorderScope?.Dispose();
    }
}