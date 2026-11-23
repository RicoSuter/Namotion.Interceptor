using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// Finds Hue bridges over mDNS, without asking the Hue cloud.
/// </summary>
public sealed class HueBridgeDiscovery(IMulticastDnsBrowser multicastDnsBrowser) : SubjectDiscovery<HueBridge, HueDiscoveredBridge>
{
    internal const string ServiceType = "_hue._tcp";

    /// <inheritdoc />
    protected override async IAsyncEnumerable<HueDiscoveredBridge> DiscoverAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var instance in await multicastDnsBrowser.BrowseAsync(ServiceType, cancellationToken))
        {
            if (instance.Properties.TryGetValue("bridgeid", out var bridgeId) && !string.IsNullOrWhiteSpace(bridgeId))
            {
                var normalizedBridgeId = bridgeId.Trim().ToLowerInvariant();
                var name = string.IsNullOrWhiteSpace(instance.InstanceName) ? normalizedBridgeId : instance.InstanceName.Trim();
                yield return new HueDiscoveredBridge(normalizedBridgeId, instance.Addresses[0].ToString(), name);
            }
        }
    }
}
