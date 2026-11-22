using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Discovery;

[InterceptorSubject]
public partial class DiscoveryTestSubject
{
    public partial string? Address { get; set; }
}

[InterceptorSubject]
public partial class OtherDiscoveryTestSubject
{
    public partial string? Address { get; set; }
}

public sealed record TestDiscoveryResult(string Key, string DisplayName = "Device", string? Detail = null) : IDiscoveryResult;

/// <summary>
/// A discoverer whose scan and matching the test passes in.
/// </summary>
internal sealed class LambdaDiscoverer<TSubject> : SubjectDiscoverer<TSubject, TestDiscoveryResult>
    where TSubject : class, IInterceptorSubject
{
    private readonly Func<DiscoveryScan, CancellationToken, IAsyncEnumerable<TestDiscoveryResult>> _scan;
    private readonly Func<TSubject, TestDiscoveryResult, bool> _matches;

    public LambdaDiscoverer(
        Func<DiscoveryScan, CancellationToken, IAsyncEnumerable<TestDiscoveryResult>> scan,
        Func<TSubject, TestDiscoveryResult, bool>? matches = null)
    {
        _scan = scan;
        _matches = matches ?? ((_, _) => false);
    }

    protected override IAsyncEnumerable<TestDiscoveryResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken) =>
        _scan(scan, cancellationToken);

    protected override bool Matches(TSubject existing, TestDiscoveryResult result) => _matches(existing, result);
}

internal static class TestScans
{
    public static async IAsyncEnumerable<TestDiscoveryResult> Yield(params TestDiscoveryResult[] results)
    {
        foreach (var result in results)
        {
            await Task.Yield();
            yield return result;
        }
    }

    public static async IAsyncEnumerable<TestDiscoveryResult> YieldThenThrow(TestDiscoveryResult result)
    {
        await Task.Yield();
        yield return result;
        throw new InvalidOperationException("The search failed.");
    }

    public static async IAsyncEnumerable<TestDiscoveryResult> Hang([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    public static DiscoveryScan CreateScan() => new(new FakeSsdpSearch(), new FakeMulticastDnsBrowser(), CancellationToken.None);
}

internal sealed class FakeSsdpSearch : ISsdpSearch
{
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult<IReadOnlyList<SsdpResponse>>([]);
    }
}

/// <summary>
/// Answers every browse with the services passed to <see cref="Answer"/>, once the test calls it.
/// </summary>
internal sealed class FakeMulticastDnsBrowser : IMulticastDnsBrowser
{
    private readonly TaskCompletionSource<IReadOnlyList<MulticastDnsService>> _answer =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public void Answer(params MulticastDnsService[] services) => _answer.TrySetResult(services);

    public Task<IReadOnlyList<MulticastDnsService>> BrowseAsync(string serviceType, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return _answer.Task.WaitAsync(cancellationToken);
    }
}
