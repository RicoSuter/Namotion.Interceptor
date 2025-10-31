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
    private IDisposable? _subscription;

    private ConcurrentDictionary<PropertyReference, bool> _scopeProperties = [];
    private ConcurrentDictionary<PropertyReference, bool> _properties = [];

    [Inject]
    public TSubject? Subject { get; set; }

    protected override void OnInitialized()
    {
        _subscription = Subject?
            .Context
            .GetPropertyChangeObservable()
            .Subscribe(change =>
            {
                if (_properties.TryGetValue(change.Property, out _))
                {
                    InvokeAsync(StateHasChanged);
                }
            });
        
        var field = typeof(ComponentBase).GetField("_renderFragment", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(this) is RenderFragment renderFragment)
        {
            void WrappedRenderFragment(RenderTreeBuilder builder)
            {
                using var recorderScope = ReadPropertyRecorder.Start(_scopeProperties);
                renderFragment(builder);
                (_properties, _scopeProperties) = (_scopeProperties, _properties);
            }

            field.SetValue(this, (RenderFragment)WrappedRenderFragment);
        }
    }

    public virtual void Dispose()
    {
        _subscription?.Dispose();
    }
}