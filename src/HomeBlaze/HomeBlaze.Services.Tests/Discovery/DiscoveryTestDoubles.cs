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
/// A subject discovery whose scan and matching the test passes in.
/// </summary>
internal sealed class LambdaDiscovery<TSubject> : SubjectDiscovery<TSubject, TestDiscoveryResult>
    where TSubject : class, IInterceptorSubject
{
    private readonly Func<CancellationToken, IAsyncEnumerable<TestDiscoveryResult>> _scan;
    private readonly Func<TSubject, TestDiscoveryResult, bool> _matches;

    public LambdaDiscovery(
        Func<CancellationToken, IAsyncEnumerable<TestDiscoveryResult>> scan,
        Func<TSubject, TestDiscoveryResult, bool>? matches = null)
    {
        _scan = scan;
        _matches = matches ?? ((_, _) => false);
    }

    protected override IAsyncEnumerable<TestDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) =>
        _scan(cancellationToken);

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

    /// <summary>
    /// Ignores cancellation: waits for <paramref name="release"/>, yields <paramref name="result"/>, and completes
    /// <paramref name="ended"/> once the consumer stops enumerating or the enumeration ends.
    /// </summary>
    public static async IAsyncEnumerable<TestDiscoveryResult> IgnoreCancellation(
        Task release, TaskCompletionSource ended, TestDiscoveryResult result)
    {
        try
        {
            await release;
            yield return result;
        }
        finally
        {
            ended.TrySetResult();
        }
    }

    /// <summary>
    /// Ignores cancellation: waits for <paramref name="release"/>, then throws.
    /// </summary>
    public static async IAsyncEnumerable<TestDiscoveryResult> ThrowIgnoringCancellation(Task release)
    {
        await release;
        throw new InvalidOperationException("The search failed.");
#pragma warning disable CS0162 // The unreachable yield makes this an iterator.
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>
    /// Throws an <see cref="ObjectDisposedException"/> instead of an <see cref="OperationCanceledException"/> once
    /// canceled, as a library does that disposes its socket on cancellation.
    /// </summary>
    public static async IAsyncEnumerable<TestDiscoveryResult> ThrowDisposedWhenCanceled(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new ObjectDisposedException("socket");
        }

        yield break;
    }
}
