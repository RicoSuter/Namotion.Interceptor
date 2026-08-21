using System.Net;
using System.Net.Sockets;
using System.Threading;
using TwinCAT.Ads;
using TwinCAT.Ads.Configuration;
using TwinCAT.Ads.TcpRouter;

namespace Namotion.Interceptor.Connectors.TwinCAT.Client;

/// <summary>Abstraction over the in-process AMS router so the pool's lifetime logic is unit-testable.</summary>
internal interface IEmbeddedAdsRouter : IDisposable
{
    int LoopbackPort { get; }
    AmsNetId LocalNetId { get; }
    void AddRoute(Route route);
}

/// <summary>
/// Reference-counted holder for a single process-wide in-process AMS router. The router owns the host's AMS
/// TCP port, so there can be only one; sources share it and add their own route. It starts on first lease and
/// stops when the last lease is disposed.
/// </summary>
internal sealed class EmbeddedRouterPool
{
    private readonly Func<AmsNetId, IEmbeddedAdsRouter> _factory;
    private readonly object _gate = new();
    private IEmbeddedAdsRouter? _router;
    private int _refCount;

    public EmbeddedRouterPool(Func<AmsNetId, IEmbeddedAdsRouter> factory) => _factory = factory;

    public Lease Acquire(AmsNetId localNetId, Route route)
    {
        lock (_gate)
        {
            _router ??= _factory(localNetId);
            _router.AddRoute(route);
            _refCount++;
            return new Lease(this, _router.LoopbackPort, _router.LocalNetId);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_refCount == 0) return;
            if (--_refCount == 0)
            {
                _router?.Dispose();
                _router = null;
            }
        }
    }

    public sealed class Lease : IDisposable
    {
        private readonly EmbeddedRouterPool _pool;
        private int _disposed;
        public int LoopbackPort { get; }
        public AmsNetId LocalNetId { get; }
        internal Lease(EmbeddedRouterPool pool, int loopbackPort, AmsNetId localNetId)
        {
            _pool = pool; LoopbackPort = loopbackPort; LocalNetId = localNetId;
        }
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _pool.Release(); }
    }
}

/// <summary>Process-wide shared embedded router and helpers.</summary>
internal static class AdsEmbeddedRouter
{
    private const int LoopbackPort = 48899;

    public static EmbeddedRouterPool Shared { get; } = new(localNetId => new BeckhoffEmbeddedRouter(localNetId, LoopbackPort));

    /// <summary>Local IP + ".1.1", the conventional net id for a route back from the PLC.</summary>
    public static AmsNetId DefaultLocalNetId()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect("8.8.8.8", 65530);
        var ip = ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        return AmsNetId.Parse($"{ip}.1.1");
    }

    private sealed class BeckhoffEmbeddedRouter : IEmbeddedAdsRouter
    {
        private const int AmsTcpPort = 48898;
        private readonly AmsTcpIpRouter _router;
        private readonly CancellationTokenSource _cts = new();
        public int LoopbackPort { get; }
        public AmsNetId LocalNetId { get; }

        public BeckhoffEmbeddedRouter(AmsNetId localNetId, int loopbackPort)
        {
            LocalNetId = localNetId;
            LoopbackPort = loopbackPort;
            _router = new AmsTcpIpRouter(localNetId, AmsTcpPort, IPAddress.Loopback, loopbackPort, new[] { IPAddress.Loopback }, 0, null);
            _ = _router.StartAsync(_cts.Token);
        }

        public void AddRoute(Route route) => _router.TryAddRoute(route);

        public void Dispose()
        {
            _cts.Cancel();
            _router.Stop();
            _cts.Dispose();
        }
    }
}
