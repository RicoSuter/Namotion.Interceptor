namespace Namotion.Proxy.Abstractions
{
    public interface IProxyDerivedPropertyChangedHandler : IProxyHandler
    {
        void OnDerivedPropertyChanged(ProxyPropertyChanged changedContext);
    }
}