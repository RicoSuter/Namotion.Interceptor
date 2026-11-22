using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// Finds two fixed devices, so that the Discover page has results without a network: a device, and a hub that
/// includes two items.
/// </summary>
public sealed class DiscoverableTestDiscoverer : SubjectDiscoverer<DiscoverableTestSubject, DiscoverableTestResult>
{
    public const string DeviceName = "Discoverable test device 1";

    // From TEST-NET-1 (RFC 5737), which no real device uses.
    public const string HostAddress = "192.0.2.17";

    public const string HubName = "Discoverable test hub";

    public const string HubHostAddress = "192.0.2.18";

    public static readonly IReadOnlyList<DiscoveredItem> HubItems =
    [
        new("Kitchen speaker", "Test model, 192.0.2.19"),
        new("Office speaker", null)
    ];

    protected override async IAsyncEnumerable<DiscoverableTestResult> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new DiscoverableTestResult(DeviceName, HostAddress);
        yield return new DiscoverableTestResult(HubName, HubHostAddress) { IncludedItems = HubItems };
    }

    protected override bool Matches(DiscoverableTestSubject existing, DiscoverableTestResult result) =>
        existing.HostAddress == result.HostAddress;
}

public sealed record DiscoverableTestResult(string DisplayName, string HostAddress) : IDiscoveryResult
{
    public string Key => HostAddress;

    public string? Detail => HostAddress;

    public IReadOnlyList<DiscoveredItem> IncludedItems { get; init; } = [];
}
