using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// Finds FRITZ!Box routers and repeaters over SSDP and at <c>fritz.box</c>, and lists the repeaters under the router
/// when it is the only one found.
/// </summary>
public sealed class FritzBoxDiscoverer : SubjectDiscoverer<FritzBox, FritzBoxDiscoveryResult>
{
    private static readonly TimeSpan DescriptionTimeout = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    protected override async IAsyncEnumerable<FritzBoxDiscoveryResult> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var result in IncludeRepeaters(await FritzBoxDiscovery.DiscoverAsync(scan.Ssdp, DescriptionTimeout, cancellationToken)))
        {
            yield return result;
        }
    }

    /// <summary>
    /// Lists the repeaters as included items of the router when exactly one router was found, and leaves the results
    /// as they are otherwise. Discovery cannot see which mesh a repeater belongs to, so the single router is assumed
    /// to be its mesh master, whose subject lists the clients of the repeaters; with several routers or none, each
    /// repeater stays a result of its own.
    /// </summary>
    internal static IReadOnlyList<FritzBoxDiscoveryResult> IncludeRepeaters(IReadOnlyList<FritzBoxDiscoveryResult> results)
    {
        FritzBoxDiscoveryResult? router = null;
        var repeaters = new List<DiscoveredItem>();
        foreach (var result in results)
        {
            if (result.IsRepeater)
            {
                repeaters.Add(new DiscoveredItem(result.DisplayName, result.HostAddress));
            }
            else if (router is null)
            {
                router = result;
            }
            else
            {
                return results;
            }
        }

        if (router is null || repeaters.Count == 0)
        {
            return results;
        }

        // The devices answer in any order, and the list should look the same in every scan.
        repeaters.Sort(static (first, second) =>
        {
            var byName = StringComparer.OrdinalIgnoreCase.Compare(first.DisplayName, second.DisplayName);
            return byName != 0 ? byName : StringComparer.Ordinal.Compare(first.Detail, second.Detail);
        });

        return [router with { IncludedItems = repeaters }];
    }

    /// <inheritdoc />
    protected override bool Matches(FritzBox existing, FritzBoxDiscoveryResult result) => IsSameRouter(existing, result);

    // The UDN recognizes a router that moved to another address; the host address one that was added without
    // discovery, and a router that replaced it at the same address, which a stale stored UDN must not hide.
    internal static bool IsSameRouter(FritzBox existing, FritzBoxDiscoveryResult result) =>
        (result.Udn is not null && string.Equals(existing.Udn?.Trim(), result.Udn, StringComparison.OrdinalIgnoreCase)) ||
        string.Equals(existing.HostAddress?.Trim(), result.HostAddress, StringComparison.OrdinalIgnoreCase);
}
