namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// Browses the local network over mDNS.
/// </summary>
public interface IMulticastDnsBrowser
{
    /// <summary>
    /// Asks every IPv4 adapter for the instances of <paramref name="serviceType"/>, such as <c>_hue._tcp</c>, and
    /// returns those that answer within a few seconds. Returns no instances instead of throwing when no adapter can
    /// ask.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task<IReadOnlyList<MulticastDnsService>> BrowseAsync(string serviceType, CancellationToken cancellationToken);
}
