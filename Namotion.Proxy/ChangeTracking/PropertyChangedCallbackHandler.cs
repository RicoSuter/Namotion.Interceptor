using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking;

internal class PropertyChangedCallbackHandler : IProxyChangedHandler
{
    private readonly Action<ProxyChanged> _callback;

    public PropertyChangedCallbackHandler(Action<ProxyChanged> callback)
    {
        _callback = callback;
    }

    public void RaisePropertyChanged(ProxyChanged context)
    {
        _callback.Invoke(context);
    }
}