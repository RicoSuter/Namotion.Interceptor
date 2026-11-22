using System.Collections.Concurrent;
using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Services.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class SubjectDiscoveryServiceTests
{
    private static SubjectDiscoveryService CreateService(TimeProvider timeProvider, params ISubjectDiscoverer[] discoverers) =>
        new(() => discoverers, new FakeSsdpSearch(), new FakeMulticastDnsBrowser(), timeProvider, NullLogger<SubjectDiscoveryService>.Instance);

    [Fact]
    public async Task WhenDiscoverersYieldResults_ThenEveryResultIsReported()
    {
        // Arrange
        var service = CreateService(
            TimeProvider.System,
            new LambdaDiscoverer<DiscoveryTestSubject>((_, _) => TestScans.Yield(new TestDiscoveryResult("a"), new TestDiscoveryResult("b"))),
            new LambdaDiscoverer<OtherDiscoveryTestSubject>((_, _) => TestScans.Yield(new TestDiscoveryResult("c"))));
        var results = new ConcurrentQueue<DiscoveredSubject>();

        // Act
        await service.ScanAsync(
            [typeof(DiscoveryTestSubject), typeof(OtherDiscoveryTestSubject)], [], results.Enqueue, _ => { }, CancellationToken.None);

        // Assert
        Assert.Equal(["a", "b", "c"], results.Select(result => result.Result.Key).Order());
    }

    [Fact]
    public async Task WhenADiscovererThrows_ThenItsFailureIsReportedAndTheOthersCarryOn()
    {
        // Arrange
        var service = CreateService(
            TimeProvider.System,
            new LambdaDiscoverer<DiscoveryTestSubject>((_, _) => TestScans.YieldThenThrow(new TestDiscoveryResult("a"))),
            new LambdaDiscoverer<OtherDiscoveryTestSubject>((_, _) => TestScans.Yield(new TestDiscoveryResult("c"))));
        var results = new ConcurrentQueue<DiscoveredSubject>();
        var failures = new ConcurrentQueue<Type>();

        // Act
        await service.ScanAsync(
            [typeof(DiscoveryTestSubject), typeof(OtherDiscoveryTestSubject)], [], results.Enqueue, failures.Enqueue, CancellationToken.None);

        // Assert
        Assert.Equal(["a", "c"], results.Select(result => result.Result.Key).Order());
        Assert.Equal([typeof(DiscoveryTestSubject)], failures);
    }

    [Fact]
    public async Task WhenTheTimeLimitPasses_ThenAHangingDiscovererEndsWithoutAFailure()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var service = CreateService(timeProvider, new LambdaDiscoverer<DiscoveryTestSubject>((_, cancellationToken) => TestScans.Hang(cancellationToken)));
        var failures = new ConcurrentQueue<Type>();
        var scan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], _ => { }, failures.Enqueue, CancellationToken.None);

        // Act
        timeProvider.Advance(SubjectDiscoveryService.ScanTimeout);
        await scan.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Empty(failures);
    }

    [Fact]
    public async Task WhenTheCallerCancels_ThenTheScanIsCanceled()
    {
        // Arrange
        var service = CreateService(TimeProvider.System, new LambdaDiscoverer<DiscoveryTestSubject>((_, cancellationToken) => TestScans.Hang(cancellationToken)));
        using var cancellation = new CancellationTokenSource();
        var scan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], _ => { }, _ => { }, cancellation.Token);

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(TestTimeouts.Default));
    }

    [Fact]
    public async Task WhenATypeIsNotAsked_ThenItsDiscovererDoesNotRun()
    {
        // Arrange
        var service = CreateService(
            TimeProvider.System,
            new LambdaDiscoverer<OtherDiscoveryTestSubject>((_, _) => TestScans.Yield(new TestDiscoveryResult("c"))));
        var results = new ConcurrentQueue<DiscoveredSubject>();

        // Act
        await service.ScanAsync([typeof(DiscoveryTestSubject)], [], results.Enqueue, _ => { }, CancellationToken.None);

        // Assert
        Assert.Empty(results);
        Assert.False(service.HasDiscoverers([typeof(DiscoveryTestSubject)]));
        Assert.True(service.HasDiscoverers([typeof(OtherDiscoveryTestSubject)]));
    }
}
