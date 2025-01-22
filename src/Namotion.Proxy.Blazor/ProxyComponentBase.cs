using Microsoft.AspNetCore.Components;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Abstractions;

namespace Namotion.Proxy.Blazor
{
    public class ProxyComponentBase<TProxy> : ComponentBase, IDisposable
        where TProxy : IInterceptorSubject
    {
        private IDisposable? _subscription;
        private ReadPropertyRecorderScope? _recorder;
        
        public PropertyReference[]? _properties;

        [Inject]
        public IObservable<PropertyChangedContext>? ProxyPropertyChanges { get; set; }

        [Inject]
        public TProxy? Proxy { get; set; }
        
        [Inject]
        public ReadPropertyRecorder? Recorder { get; set; }

        protected override void OnInitialized()
        {
            _subscription = ProxyPropertyChanges!
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
}
