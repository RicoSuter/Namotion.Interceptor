namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// One scan for devices. Its services send each distinct query once and give every discoverer that asks the same
/// question the same answer.
/// </summary>
public sealed class DiscoveryScan
{
    /// <param name="ssdpSearch">The SSDP search to share.</param>
    /// <param name="multicastDnsBrowser">The mDNS browser to share.</param>
    /// <param name="cancellationToken">Ends the shared queries; canceled when the scan ends.</param>
    public DiscoveryScan(ISsdpSearch ssdpSearch, IMulticastDnsBrowser multicastDnsBrowser, CancellationToken cancellationToken)
    {
        Ssdp = new SharedSsdpSearch(new SharedQueries<IReadOnlyList<SsdpResponse>>(ssdpSearch.SearchAsync, cancellationToken, StringComparer.Ordinal));
        MulticastDns = new SharedMulticastDnsBrowser(
            new SharedQueries<IReadOnlyList<MulticastDnsService>>(multicastDnsBrowser.BrowseAsync, cancellationToken, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Searches over SSDP, once per search target and scan.
    /// </summary>
    public ISsdpSearch Ssdp { get; }

    /// <summary>
    /// Browses over mDNS, once per service type and scan.
    /// </summary>
    public IMulticastDnsBrowser MulticastDns { get; }

    private sealed class SharedSsdpSearch(SharedQueries<IReadOnlyList<SsdpResponse>> queries) : ISsdpSearch
    {
        public Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken) =>
            queries.GetAsync(searchTarget, cancellationToken);
    }

    private sealed class SharedMulticastDnsBrowser(SharedQueries<IReadOnlyList<MulticastDnsService>> queries) : IMulticastDnsBrowser
    {
        public Task<IReadOnlyList<MulticastDnsService>> BrowseAsync(string serviceType, CancellationToken cancellationToken) =>
            queries.GetAsync(serviceType, cancellationToken);
    }
}
