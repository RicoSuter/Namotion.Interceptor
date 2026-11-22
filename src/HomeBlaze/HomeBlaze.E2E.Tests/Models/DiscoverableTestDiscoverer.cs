using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// Finds one fixed device, so that the Discover page has a result without a network.
/// </summary>
public sealed class DiscoverableTestDiscoverer : SubjectDiscoverer<DiscoverableTestSubject, DiscoverableTestResult>
{
    public const string DeviceName = "Discoverable test device 1";

    // From TEST-NET-1 (RFC 5737), which no real device uses.
    public const string HostAddress = "192.0.2.17";

    protected override async IAsyncEnumerable<DiscoverableTestResult> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new DiscoverableTestResult(DeviceName, HostAddress);
    }

    protected override bool Matches(DiscoverableTestSubject existing, DiscoverableTestResult result) =>
        existing.HostAddress == result.HostAddress;
}

public sealed record DiscoverableTestResult(string DisplayName, string HostAddress) : IDiscoveryResult
{
    public string Key => HostAddress;

    public string? Detail => HostAddress;
}
