using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// Finds FRITZ!Box routers and repeaters over SSDP and at <c>fritz.box</c>.
/// </summary>
public sealed class FritzBoxDiscoverer : SubjectDiscoverer<FritzBox, FritzBoxDiscoveryResult>
{
    private static readonly TimeSpan DescriptionTimeout = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    protected override async IAsyncEnumerable<FritzBoxDiscoveryResult> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var result in await FritzBoxDiscovery.DiscoverAsync(scan.Ssdp, DescriptionTimeout, cancellationToken))
        {
            yield return result;
        }
    }

    /// <inheritdoc />
    protected override bool Matches(FritzBox existing, FritzBoxDiscoveryResult result) => IsSameRouter(existing, result);

    // The UDN recognizes a router that moved to another address; the host address one that was added without
    // discovery, and a router that replaced it at the same address, which a stale stored UDN must not hide.
    internal static bool IsSameRouter(FritzBox existing, FritzBoxDiscoveryResult result) =>
        (result.Udn is not null && string.Equals(existing.Udn?.Trim(), result.Udn, StringComparison.OrdinalIgnoreCase)) ||
        string.Equals(existing.HostAddress?.Trim(), result.HostAddress, StringComparison.OrdinalIgnoreCase);
}
