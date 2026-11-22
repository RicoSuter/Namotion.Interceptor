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

    internal static bool IsSameRouter(FritzBox existing, FritzBoxDiscoveryResult result) =>
        existing.Udn is { } udn && result.Udn is not null
            ? string.Equals(udn, result.Udn, StringComparison.OrdinalIgnoreCase)
            : string.Equals(existing.HostAddress?.Trim(), result.HostAddress, StringComparison.OrdinalIgnoreCase);
}
