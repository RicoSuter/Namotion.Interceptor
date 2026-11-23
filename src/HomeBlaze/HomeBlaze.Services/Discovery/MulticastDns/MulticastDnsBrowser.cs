using System.Net;
using System.Net.Sockets;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Services.Discovery.MulticastDns;

/// <summary>
/// Browses over mDNS with legacy unicast queries (RFC 6762 section 6.7): queries leave from an ephemeral port, so
/// responders answer that port directly and port 5353, which Avahi holds on most Linux hosts, is not bound.
/// </summary>
internal sealed class MulticastDnsBrowser : IMulticastDnsBrowser
{
    private static readonly IPEndPoint MulticastEndPoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    // RFC 6762 section 5.2 spaces repeated queries at least one second apart, doubling each time.
    private static readonly TimeSpan[] RepeatDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];
    private static readonly TimeSpan ListeningWindow = TimeSpan.FromSeconds(4);
    private const int MaximumMessagesPerAdapter = 512;

    private readonly ILogger<MulticastDnsBrowser> _logger;

    public MulticastDnsBrowser(ILogger<MulticastDnsBrowser> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<MulticastDnsServiceInstance>> BrowseAsync(string serviceType, CancellationToken cancellationToken)
    {
        var query = DnsMessage.CreatePointerQuery(MulticastDnsServiceCollector.GetServiceName(serviceType));
        var collector = new MulticastDnsServiceCollector();

        var adapterAddresses = NetworkAdapters.GetMulticastIPv4Addresses();
        if (adapterAddresses.Count == 0)
        {
            _logger.LogDebug("No IPv4 adapter supports multicast; the mDNS browse for {ServiceType} is skipped.", serviceType);
            return [];
        }

        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(ListeningWindow);
        await Task.WhenAll(adapterAddresses
            .Select(address => QueryAdapterAsync(address, query, collector, window.Token)));

        cancellationToken.ThrowIfCancellationRequested();
        return collector.GetServices(serviceType);
    }

    private async Task QueryAdapterAsync(
        IPAddress localAddress, byte[] query, MulticastDnsServiceCollector collector, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new UdpClient(new IPEndPoint(localAddress, 0));
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());

            // RFC 6762 section 11: the time to live of a multicast query is 255.
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

            _ = SendQueriesAsync(client, query, cancellationToken);
            for (var messageCount = 0; messageCount < MaximumMessagesPerAdapter; messageCount++)
            {
                var received = await client.ReceiveAsync(cancellationToken);
                if (DnsMessage.TryParse(received.Buffer, out var records))
                {
                    collector.Add(records, received.RemoteEndPoint.Address);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The listening window ended, or the caller canceled, which BrowseAsync rethrows.
        }
        catch (SocketException exception)
        {
            _logger.LogDebug(exception, "The mDNS query on {Address} failed.", localAddress);
        }
    }

    private static async Task SendQueriesAsync(UdpClient client, byte[] query, CancellationToken cancellationToken)
    {
        // Runs unobserved next to the receive loop, which disposes the client when the window ends, so every
        // failure ends it quietly.
        try
        {
            await client.SendAsync(query, MulticastEndPoint, cancellationToken);
            foreach (var delay in RepeatDelays)
            {
                await Task.Delay(delay, cancellationToken);
                await client.SendAsync(query, MulticastEndPoint, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }
}
