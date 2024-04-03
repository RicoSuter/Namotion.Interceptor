namespace Namotion.Proxy.Abstractions
{
    public interface IProxyPropertyChangedHandler : IObservable<ProxyPropertyChanged>, IProxyHandler
    {
        internal void RaisePropertyChanged(ProxyPropertyChanged changedContext);
    }
}