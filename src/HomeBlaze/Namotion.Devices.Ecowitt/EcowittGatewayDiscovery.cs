using System.Net;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Ecowitt;

/// <summary>
/// Finds Ecowitt gateways with the broadcast request of their local API, sent to UDP port 46000 of the local network.
/// </summary>
public sealed class EcowittGatewayDiscovery(IUdpDiscovery udpDiscovery) : SubjectDiscovery<EcowittGateway, EcowittDiscoveredGateway>
{
    internal static readonly byte[] BroadcastRequest = [0xFF, 0xFF, EcowittBroadcastReply.BroadcastCommand, 0x00, 0x04, 0x16];

    private const int BroadcastPort = 46000;
    private static readonly TimeSpan ListeningWindow = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    protected override async IAsyncEnumerable<EcowittDiscoveredGateway> DiscoverAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var datagram in udpDiscovery.BroadcastAsync(BroadcastPort, BroadcastRequest, ListeningWindow, cancellationToken))
        {
            var reply = EcowittBroadcastReply.TryParse(datagram.Payload.Span);
            if (reply is not null)
            {
                yield return ToResult(reply, datagram.SenderAddress);
            }
        }
    }

    /// <param name="reply">The reply of the gateway.</param>
    /// <param name="senderAddress">
    /// The address the reply came from, which is the host address, as for the other UDP subject discoveries, rather
    /// than the address the reply names.
    /// </param>
    internal static EcowittDiscoveredGateway ToResult(EcowittBroadcastReply reply, IPAddress senderAddress)
    {
        var name = reply.Name;
        var separatorIndex = name.IndexOf(' ');
        var deviceName = separatorIndex < 0 ? name : name[..separatorIndex];
        var firmwareVersion = separatorIndex < 0 ? string.Empty : name[(separatorIndex + 1)..].Trim();

        return new EcowittDiscoveredGateway(
            reply.MacAddress,
            senderAddress.ToString(),
            deviceName.Length == 0 ? null : deviceName,
            firmwareVersion.Length == 0 ? null : firmwareVersion);
    }
}
