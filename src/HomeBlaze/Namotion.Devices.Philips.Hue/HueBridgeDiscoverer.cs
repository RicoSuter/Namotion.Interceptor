using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// Finds Hue bridges over mDNS, without asking the Hue cloud.
/// </summary>
public sealed class HueBridgeDiscoverer : SubjectDiscoverer<HueBridge, HueDiscoveredBridge>
{
    internal const string ServiceType = "_hue._tcp";

    /// <inheritdoc />
    protected override async IAsyncEnumerable<HueDiscoveredBridge> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var service in await scan.MulticastDns.BrowseAsync(ServiceType, cancellationToken))
        {
            if (service.Properties.TryGetValue("bridgeid", out var bridgeId) && !string.IsNullOrWhiteSpace(bridgeId))
            {
                var normalizedBridgeId = bridgeId.Trim().ToLowerInvariant();
                var name = string.IsNullOrWhiteSpace(service.InstanceName) ? normalizedBridgeId : service.InstanceName.Trim();
                yield return new HueDiscoveredBridge(normalizedBridgeId, service.Addresses[0].ToString(), name);
            }
        }
    }

    /// <inheritdoc />
    protected override bool Matches(HueBridge existing, HueDiscoveredBridge result) =>
        string.Equals(existing.BridgeId, result.BridgeId, StringComparison.OrdinalIgnoreCase);
}
