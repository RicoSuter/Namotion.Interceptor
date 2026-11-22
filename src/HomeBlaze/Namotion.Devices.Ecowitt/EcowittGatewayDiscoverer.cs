using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Ecowitt;

/// <summary>
/// Finds Ecowitt gateways with the broadcast request of their local API, sent to UDP port 46000 of the local network.
/// </summary>
public sealed class EcowittGatewayDiscoverer : SubjectDiscoverer<EcowittGateway, EcowittDiscoveredGateway>
{
    private readonly Func<CancellationToken, IAsyncEnumerable<EcowittBroadcastReply>> _probe;

    public EcowittGatewayDiscoverer(ILogger<EcowittGatewayDiscoverer> logger)
        : this(cancellationToken => EcowittBroadcastProbe.ProbeAsync(logger, cancellationToken))
    {
    }

    internal EcowittGatewayDiscoverer(Func<CancellationToken, IAsyncEnumerable<EcowittBroadcastReply>> probe)
    {
        _probe = probe;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<EcowittDiscoveredGateway> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var reply in _probe(cancellationToken))
        {
            yield return ToResult(reply);
        }
    }

    /// <inheritdoc />
    protected override bool Matches(EcowittGateway existing, EcowittDiscoveredGateway result) => IsSameGateway(existing, result);

    internal static EcowittDiscoveredGateway ToResult(EcowittBroadcastReply reply)
    {
        var name = reply.Name;
        var separatorIndex = name.IndexOf(' ');
        var deviceName = separatorIndex < 0 ? name : name[..separatorIndex];
        var firmwareVersion = separatorIndex < 0 ? string.Empty : name[(separatorIndex + 1)..].Trim();

        return new EcowittDiscoveredGateway(
            reply.MacAddress,
            reply.IpAddress.ToString(),
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
