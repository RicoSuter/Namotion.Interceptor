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

/// <summary>
/// A result that matches the subjects for which <see cref="IsMatch"/> is true, of any type, and none without it.
/// </summary>
public sealed record TestDiscoveryResult(string Key, string DisplayName = "Device", string? Detail = null)
    : IDiscoveryResult<IInterceptorSubject>
{
    public Func<IInterceptorSubject, bool>? IsMatch { get; init; }

    public bool Matches(IInterceptorSubject subject) => IsMatch?.Invoke(subject) ?? false;
}

/// <summary>
/// A result that matches a <see cref="DiscoveryTestSubject"/> with its address.
/// </summary>
public sealed record AddressDiscoveryResult(string Address) : IDiscoveryResult<DiscoveryTestSubject>
{
    public string Key => Address;

    public string DisplayName => Address;

    public string? Detail => null;

    public bool Matches(DiscoveryTestSubject subject) => subject.Address == Address;
}

/// <summary>
/// A subject discovery whose scan the test passes in.
/// </summary>
internal sealed class LambdaDiscovery<TSubject>(Func<CancellationToken, IAsyncEnumerable<TestDiscoveryResult>> scan)
    : SubjectDiscovery<TSubject, TestDiscoveryResult>
    where TSubject : class, IInterceptorSubject
{
    protected override IAsyncEnumerable<TestDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) =>
        scan(cancellationToken);
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
