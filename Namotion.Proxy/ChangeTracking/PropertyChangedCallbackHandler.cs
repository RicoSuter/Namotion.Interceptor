using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking;

internal class PropertyChangedCallbackHandler : IProxyChangedHandler
{
    private readonly Action<ProxyChangedHandlerContext> _callback;

    public PropertyChangedCallbackHandler(Action<ProxyChangedHandlerContext> callback)
    {
        _callback = callback;
    }

    public void RaisePropertyChanged(ProxyChangedHandlerContext context)
    {
        _callback.Invoke(context);
    }
}