using Microsoft.AspNetCore.Components;

using Namotion.Proxy.Abstractions;
using Namotion.Proxy.ChangeTracking;

namespace Namotion.Proxy.Blazor
{
    public class TrackingComponentBase : ComponentBase, IDisposable
    {
        private IDisposable? _subscription;
        private PropertyChangeRecorderScope? _recorder;
        public ProxyPropertyReference[]? _properties;

        [Inject]
        public IObservable<ProxyChangedContext>? ProxyPropertyChanges { get; set; }

        [Inject]
        public IProxyContext? ProxyContext { get; set; }

        protected override void OnInitialized()
        {
            _subscription = ProxyPropertyChanges!
                .Subscribe(change =>
                {
                    if (_properties?.Any(p =>
                        p.Proxy == change.Proxy &&
                        p.PropertyName == change.PropertyName) != false)
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
                _recorder = ProxyContext?.BeginPropertyChangedRecording();
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
