using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// Finds three fixed devices, so that the Discover page has results without a network: two devices, and a hub that
/// includes two items.
/// </summary>
public sealed class DiscoverableTestDiscoverer : SubjectDiscoverer<DiscoverableTestSubject, DiscoverableTestResult>
{
    public const string DeviceName = "Discoverable test device 1";

    // From TEST-NET-1 (RFC 5737), which no real device uses.
    public const string HostAddress = "192.0.2.17";

    public const string SecondDeviceName = "Discoverable test device 2";

    public const string SecondHostAddress = "192.0.2.18";

    public const string HubName = "Discoverable test hub";

    private const string HubHostAddress = "192.0.2.19";

    private static readonly IReadOnlyList<DiscoveredItem> HubItems =
    [
        new("Kitchen speaker", "Test model, 192.0.2.20"),
        new("Office speaker", null)
    ];

    private static readonly ConcurrentQueue<string> MatchedHostAddressQueue = new();
    private static int _scanCount;

    /// <summary>
    /// How many scans have asked this discoverer so far, in any test of the process.
    /// </summary>
    public static int ScanCount => Volatile.Read(ref _scanCount);

    /// <summary>
    /// The host addresses of the found devices that matched an existing subject, in the order of the matches, in any
    /// test of the process.
    /// </summary>
    public static IReadOnlyCollection<string> MatchedHostAddresses => MatchedHostAddressQueue;

    protected override async IAsyncEnumerable<DiscoverableTestResult> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _scanCount);
        await Task.Yield();
        yield return new DiscoverableTestResult(DeviceName, HostAddress);
        yield return new DiscoverableTestResult(SecondDeviceName, SecondHostAddress);
        yield return new DiscoverableTestResult(HubName, HubHostAddress) { IncludedItems = HubItems };
    }

    protected override bool Matches(DiscoverableTestSubject existing, DiscoverableTestResult result)
    {
        var isMatch = existing.HostAddress == result.HostAddress;
        if (isMatch)
        {
            MatchedHostAddressQueue.Enqueue(result.HostAddress);
        }

        return isMatch;
    }
}

public sealed record DiscoverableTestResult(string DisplayName, string HostAddress) : IDiscoveryResult
{
    public string Key => HostAddress;

    public string? Detail => HostAddress;

    public IReadOnlyList<DiscoveredItem> IncludedItems { get; init; } = [];
}
