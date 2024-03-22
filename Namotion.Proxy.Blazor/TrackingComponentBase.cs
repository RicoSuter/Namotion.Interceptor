using Microsoft.AspNetCore.Components;

using Namotion.Proxy.Abstractions;
using Namotion.Proxy.ChangeTracking;

namespace Namotion.Proxy.Blazor
{
    public class TrackingComponentBase : ComponentBase, IDisposable
    {
        private IDisposable? _subscription;
        private PropertyChangeRecorderScope? _recorder;
        private ProxyPropertyReference[]? _properties;

        [Inject]
        public IObservable<ProxyChangedContext>? ProxyPropertyChanges { get; set; }

        [Inject]
        public IProxyContext? ProxyContext { get; set; }

        protected override void OnInitialized()
        {
            _recorder = ProxyContext!.BeginPropertyChangedRecording();
            _subscription = ProxyPropertyChanges!
                .Subscribe(change =>
                {
                    // TODO: Find a way to make this work
                    //if (_properties?.Any(p =>
                    //    p.Proxy == change.Proxy &&
                    //    p.PropertyName == change.PropertyName) != false)
                    {
                        InvokeAsync(StateHasChanged);
                    }
                });
        }

        protected override void OnAfterRender(bool firstRender)
        {
            _properties = _recorder?.GetAndReset();
        }

        public virtual void Dispose()
        {
            _subscription?.Dispose();
            _recorder?.Dispose();
        }
    }
}
