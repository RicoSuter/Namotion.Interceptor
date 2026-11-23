using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>
/// Finds two fixed devices, so that the Discover page has results without a network.
/// </summary>
public sealed class DiscoverableTestDiscovery : SubjectDiscovery<DiscoverableTestSubject, DiscoverableTestResult>
{
    public const string DeviceName = "Discoverable test device 1";

    // From TEST-NET-1 (RFC 5737), which no real device uses.
    public const string HostAddress = "192.0.2.17";

    public const string SecondDeviceName = "Discoverable test device 2";

    public const string SecondHostAddress = "192.0.2.18";

    private static readonly ConcurrentQueue<string> MatchedHostAddressQueue = new();
    private static int _scanCount;

    /// <summary>
    /// How many scans have asked this subject discovery so far, in any test of the process.
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
}
