using System.Collections.Concurrent;
using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Services.Discovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class SubjectDiscoveryServiceTests
{
    private static SubjectDiscoveryService CreateService(TimeProvider timeProvider, params ISubjectDiscovery[] discoveries) =>
        CreateService(timeProvider, NullLogger<SubjectDiscoveryService>.Instance, discoveries);

    private static SubjectDiscoveryService CreateService(
        TimeProvider timeProvider, ILogger<SubjectDiscoveryService> logger, params ISubjectDiscovery[] discoveries) =>
        new(() => discoveries, new FakeSsdpSearch(), new FakeMulticastDnsBrowser(), new EmptyUdpDiscovery(), timeProvider, logger);

    [Fact]
    public async Task WhenDiscoveriesYieldResults_ThenEveryResultIsReported()
    {
        // Arrange
        var service = CreateService(
            TimeProvider.System,
            new LambdaDiscovery<DiscoveryTestSubject>((_, _) => TestScans.Yield(new TestDiscoveryResult("a"), new TestDiscoveryResult("b"))),
            new LambdaDiscovery<OtherDiscoveryTestSubject>((_, _) => TestScans.Yield(new TestDiscoveryResult("c"))));
        var results = new ConcurrentQueue<DiscoveredSubject>();

        // Act
        await service.ScanAsync(
            [typeof(DiscoveryTestSubject), typeof(OtherDiscoveryTestSubject)], [], results.Enqueue, _ => { }, CancellationToken.None);

        // Assert
        Assert.Equal(["a", "b", "c"], results.Select(result => result.Result.Key).Order());
    }

    [Fact]
    public async Task WhenADiscoveryThrows_ThenItsFailureIsReportedAndTheOthersCarryOn()
    {
        // Arrange
        var service = CreateService(
            TimeProvider.System,
            new LambdaDiscovery<DiscoveryTestSubject>((_, _) => TestScans.YieldThenThrow(new TestDiscoveryResult("a"))),
            new LambdaDiscovery<OtherDiscoveryTestSubject>((_, _) => TestScans.Yield(new TestDiscoveryResult("c"))));
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
    public async Task WhenAllDiscoveriesComplete_ThenTheScanTokenIsCanceled()
    {
        // Arrange
        var receivedToken = CancellationToken.None;
        var service = CreateService(
            TimeProvider.System,
            new LambdaDiscovery<DiscoveryTestSubject>((_, cancellationToken) =>
            {
                receivedToken = cancellationToken;
                return TestScans.Yield(new TestDiscoveryResult("a"));
            }));

        // Act
        await service.ScanAsync([typeof(DiscoveryTestSubject)], [], _ => { }, _ => { }, CancellationToken.None);

        // Assert
        Assert.True(receivedToken.CanBeCanceled);
        Assert.True(receivedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task WhenTheTimeLimitPasses_ThenAHangingDiscoveryEndsWithoutAFailure()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var service = CreateService(timeProvider, new LambdaDiscovery<DiscoveryTestSubject>((_, cancellationToken) => TestScans.Hang(cancellationToken)));
        var failures = new ConcurrentQueue<Type>();
        var scan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], _ => { }, failures.Enqueue, CancellationToken.None);

        // Act
        timeProvider.Advance(SubjectDiscoveryService.ScanTimeout);
        await scan.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Empty(failures);
    }

    [Fact]
    public async Task WhenADiscoveryIgnoresCancellation_ThenTheScanEndsAtTheTimeLimitAndDropsItsLateReports()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateService(
            timeProvider,
            new LambdaDiscovery<DiscoveryTestSubject>((_, _) => TestScans.IgnoreCancellation(release.Task, ended, new TestDiscoveryResult("late"))));
        var results = new ConcurrentQueue<DiscoveredSubject>();
        var failures = new ConcurrentQueue<Type>();
        var scan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], results.Enqueue, failures.Enqueue, CancellationToken.None);

        // Act
        timeProvider.Advance(SubjectDiscoveryService.ScanTimeout);
        await scan.WaitAsync(TestTimeouts.Default);
        release.SetResult();
        await ended.Task.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Empty(results);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task WhenADiscoveryThrowsBecauseTheScanEnded_ThenNoFailureIsReported()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var service = CreateService(
            timeProvider,
            logger,
            new LambdaDiscovery<DiscoveryTestSubject>((_, cancellationToken) => TestScans.ThrowDisposedWhenCanceled(cancellationToken)));
        var failures = new ConcurrentQueue<Type>();
        var scan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], _ => { }, failures.Enqueue, CancellationToken.None);

        // Act
        timeProvider.Advance(SubjectDiscoveryService.ScanTimeout);
        await scan.WaitAsync(TestTimeouts.Default);
        await logger.Logged.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Empty(failures);
        Assert.Equal([LogLevel.Debug], logger.Levels);
    }

    [Fact]
    public async Task WhenAnAbandonedDiscoveryThrowsLate_ThenItIsLoggedAtDebugOnly()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new RecordingLogger();
        var service = CreateService(
            timeProvider,
            logger,
            new LambdaDiscovery<DiscoveryTestSubject>((_, _) => TestScans.ThrowIgnoringCancellation(release.Task)));
        var failures = new ConcurrentQueue<Type>();
        var scan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], _ => { }, failures.Enqueue, CancellationToken.None);

        // Act
        timeProvider.Advance(SubjectDiscoveryService.ScanTimeout);
        await scan.WaitAsync(TestTimeouts.Default);
        release.SetResult();
        await logger.Logged.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Empty(failures);
        Assert.Equal([LogLevel.Debug], logger.Levels);
    }

    [Fact]
    public async Task WhenTheCallerCancelsWhileADiscoveryIgnoresIt_ThenTheScanIsCanceled()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateService(
            TimeProvider.System,
            new LambdaDiscovery<DiscoveryTestSubject>((_, _) => TestScans.IgnoreCancellation(release.Task, ended, new TestDiscoveryResult("late"))));
        using var cancellation = new CancellationTokenSource();
        var scan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], _ => { }, _ => { }, cancellation.Token);

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(TestTimeouts.Default));
        release.SetResult();
        await ended.Task.WaitAsync(TestTimeouts.Default);
    }

    [Fact]
    public async Task WhenTheCallerCancels_ThenTheScanIsCanceled()
    {
        // Arrange
        var service = CreateService(TimeProvider.System, new LambdaDiscovery<DiscoveryTestSubject>((_, cancellationToken) => TestScans.Hang(cancellationToken)));
        using var cancellation = new CancellationTokenSource();
        var scan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], _ => { }, _ => { }, cancellation.Token);

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(TestTimeouts.Default));
    }

    [Fact]
    public async Task WhenATypeIsNotAsked_ThenItsDiscoveryDoesNotRun()
    {
        // Arrange
        var service = CreateService(
            TimeProvider.System,
            new LambdaDiscovery<OtherDiscoveryTestSubject>((_, _) => TestScans.Yield(new TestDiscoveryResult("c"))));
        var results = new ConcurrentQueue<DiscoveredSubject>();

        // Act
        await service.ScanAsync([typeof(DiscoveryTestSubject)], [], results.Enqueue, _ => { }, CancellationToken.None);

        // Assert
        Assert.Empty(results);
        Assert.False(service.HasDiscoveries([typeof(DiscoveryTestSubject)]));
        Assert.True(service.HasDiscoveries([typeof(OtherDiscoveryTestSubject)]));
    }

    private sealed class RecordingLogger : ILogger<SubjectDiscoveryService>
    {
        private readonly ConcurrentQueue<LogLevel> _levels = new();
        private readonly TaskCompletionSource _logged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyCollection<LogLevel> Levels => _levels;

        /// <summary>Completes once an entry has been logged.</summary>
        public Task Logged => _logged.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _levels.Enqueue(logLevel);
            _logged.TrySetResult();
        }
    }
}
