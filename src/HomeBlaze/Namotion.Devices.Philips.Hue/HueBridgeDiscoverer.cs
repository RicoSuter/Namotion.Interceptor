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
                yield return new HueDiscoveredBridge(
                    bridgeId.Trim().ToLowerInvariant(), service.Addresses[0].ToString(), service.InstanceName);
            }
        }
    }

    /// <inheritdoc />
    protected override bool Matches(HueBridge existing, HueDiscoveredBridge result) =>
        string.Equals(existing.BridgeId, result.BridgeId, StringComparison.OrdinalIgnoreCase);
}
