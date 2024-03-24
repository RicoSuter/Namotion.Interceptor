using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking
{
    public interface IProxyPropertyChangedHandler : IObservable<ProxyPropertyChanged>, IProxyHandler
    {
        internal void RaisePropertyChanged(ProxyPropertyChanged changedContext);
    }
}