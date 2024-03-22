using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking;

internal class PropertyChangedCallbackHandler : IProxyChangedHandler
{
    private readonly Action<ProxyChangedContext> _callback;

    public PropertyChangedCallbackHandler(Action<ProxyChangedContext> callback)
    {
        _callback = callback;
    }

    public void RaisePropertyChanged(ProxyChangedContext context)
    {
        _callback.Invoke(context);
    }
}