namespace Namotion.Proxy.Abstractions
{
    public interface IProxyRegistry : IProxyHandler
    {
        IEnumerable<IProxy> KnownProxies { get; }
    }
}