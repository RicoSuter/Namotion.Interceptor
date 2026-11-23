using System.Net;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Ecowitt;

/// <summary>
/// Finds Ecowitt gateways with the broadcast request of their local API, sent to UDP port 46000 of the local network.
/// </summary>
public sealed class EcowittGatewayDiscoverer : SubjectDiscovery<EcowittGateway, EcowittDiscoveredGateway>
{
    internal static readonly byte[] BroadcastRequest = [0xFF, 0xFF, EcowittBroadcastReply.BroadcastCommand, 0x00, 0x04, 0x16];

    private const int BroadcastPort = 46000;
    private static readonly TimeSpan ListeningWindow = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    protected override async IAsyncEnumerable<EcowittDiscoveredGateway> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var datagram in scan.Udp.BroadcastAsync(BroadcastPort, BroadcastRequest, ListeningWindow, cancellationToken))
        {
            var reply = EcowittBroadcastReply.TryParse(datagram.Payload.Span);
            if (reply is not null)
            {
                yield return ToResult(reply, datagram.SenderAddress);
            }
        }
    }

    /// <inheritdoc />
    protected override bool Matches(EcowittGateway existing, EcowittDiscoveredGateway result) => IsSameGateway(existing, result);

    /// <param name="reply">The reply of the gateway.</param>
    /// <param name="senderAddress">
    /// The address the reply came from, which is the host address, as for the other UDP discoverers, rather than the
    /// address the reply names.
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

    internal static bool IsSameGateway(EcowittGateway existing, EcowittDiscoveredGateway result) =>
        IsSameMacAddress(existing.MacAddress, result.MacAddress) ||
        string.Equals(existing.HostAddress?.Trim(), result.HostAddress, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Compares two MAC addresses, ignoring case and the separators <c>:</c> and <c>-</c>.
    /// </summary>
    internal static bool IsSameMacAddress(string? macAddress, string otherMacAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
        {
            return false;
        }

        var index = 0;
        var otherIndex = 0;
        while (true)
        {
            while (index < macAddress.Length && IsSeparator(macAddress[index]))
            {
                index++;
            }

            while (otherIndex < otherMacAddress.Length && IsSeparator(otherMacAddress[otherIndex]))
            {
                otherIndex++;
            }

            if (index == macAddress.Length || otherIndex == otherMacAddress.Length)
            {
                return index == macAddress.Length && otherIndex == otherMacAddress.Length;
            }

            if (char.ToUpperInvariant(macAddress[index]) != char.ToUpperInvariant(otherMacAddress[otherIndex]))
            {
                return false;
            }

            index++;
            otherIndex++;
        }
    }

    private static bool IsSeparator(char character) => character is ':' or '-';
}
