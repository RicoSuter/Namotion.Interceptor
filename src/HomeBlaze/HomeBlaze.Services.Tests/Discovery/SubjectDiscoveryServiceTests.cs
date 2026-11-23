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
        new(() => discoveries, timeProvider, logger);

    [Fact]
    public async Task WhenDiscoveriesYieldResults_ThenEveryResultIsReported()
    {
        // Arrange
        var service = CreateService(
            TimeProvider.System,
            new LambdaDiscovery<DiscoveryTestSubject>(_ => TestScans.Yield(new TestDiscoveryResult("a"), new TestDiscoveryResult("b"))),
            new LambdaDiscovery<OtherDiscoveryTestSubject>(_ => TestScans.Yield(new TestDiscoveryResult("c"))));
        var results = new ConcurrentQueue<DiscoveredSubject>();

        // Act
        await service.ScanAsync(
            [typeof(DiscoveryTestSubject), typeof(OtherDiscoveryTestSubject)], [], results.Enqueue, _ => { }, CancellationToken.None);

        // Assert
        Assert.Equal(["a", "b", "c"], results.Select(result => result.Result.Key).Order());
    }

    [Fact]
    public async Task WhenAResultMatchesExistingSubjects_ThenItIsReportedWithTheFirstOfTheSubjectType()
    {
        // Arrange
        var otherType = new OtherDiscoveryTestSubject();
        var first = new DiscoveryTestSubject();
        var second = new DiscoveryTestSubject();
        var askedSubjects = new ConcurrentQueue<object>();
        var service = CreateService(
            TimeProvider.System,
            new LambdaDiscovery<DiscoveryTestSubject>(_ => TestScans.Yield(
                new TestDiscoveryResult("a") { IsMatch = subject => { askedSubjects.Enqueue(subject); return true; } },
                new TestDiscoveryResult("b"))));
        var results = new ConcurrentQueue<DiscoveredSubject>();

        // Act
        await service.ScanAsync([typeof(DiscoveryTestSubject)], [otherType, first, second], results.Enqueue, _ => { }, CancellationToken.None);

        // Assert - the result matches a subject of any type, so only the subject type keeps the other subject out.
        var discovered = results.OrderBy(result => result.Result.Key).ToArray();
        Assert.Equal(["a", "b"], discovered.Select(result => result.Result.Key));
        Assert.Same(first, discovered[0].ExistingSubject);
        Assert.Null(discovered[1].ExistingSubject);
        Assert.Equal([first], askedSubjects);
        Assert.All(discovered, result => Assert.Equal(typeof(DiscoveryTestSubject), result.SubjectType));
    }

    [Fact]
    public async Task WhenADiscoveryYieldsADeviceTwice_ThenItIsReportedOnce()
    {
        // Arrange
        var service = CreateService(
            TimeProvider.System,
            new LambdaDiscovery<DiscoveryTestSubject>(
                _ => TestScans.Yield(new TestDiscoveryResult("a"), new TestDiscoveryResult("a", "Same device again"), new TestDiscoveryResult("A"))),
            new LambdaDiscovery<OtherDiscoveryTestSubject>(_ => TestScans.Yield(new TestDiscoveryResult("a"))));
        var results = new ConcurrentQueue<DiscoveredSubject>();

        // Act
        await service.ScanAsync(
            [typeof(DiscoveryTestSubject), typeof(OtherDiscoveryTestSubject)], [], results.Enqueue, _ => { }, CancellationToken.None);

        // Assert - keys are compared ordinally and per subject discovery.
        var ofType = results.Where(result => result.SubjectType == typeof(DiscoveryTestSubject)).ToArray();
        Assert.Equal(["A", "a"], ofType.Select(result => result.Result.Key).Order(StringComparer.Ordinal));
        Assert.Equal("Device", ofType.Single(result => result.Result.Key == "a").Result.DisplayName);
        Assert.Single(results, result => result.SubjectType == typeof(OtherDiscoveryTestSubject));
    }

    [Fact]
    public async Task WhenMatchesThrows_ThenItsDiscoveryFailsAndTheOthersCarryOn()
    {
        // Arrange
        var logger = new RecordingLogger();
        var service = CreateService(
            TimeProvider.System,
            logger,
            new LambdaDiscovery<DiscoveryTestSubject>(_ => TestScans.Yield(
                new TestDiscoveryResult("a") { IsMatch = _ => throw new InvalidOperationException("The match failed.") },
                new TestDiscoveryResult("b"))),
            new LambdaDiscovery<OtherDiscoveryTestSubject>(_ => TestScans.Yield(new TestDiscoveryResult("c"))));
        var results = new ConcurrentQueue<DiscoveredSubject>();
        var failures = new ConcurrentQueue<Type>();

        // Act
        await service.ScanAsync(
            [typeof(DiscoveryTestSubject), typeof(OtherDiscoveryTestSubject)],
            [new DiscoveryTestSubject()],
            results.Enqueue,
            failures.Enqueue,
            CancellationToken.None);

        // Assert
        Assert.Equal(["c"], results.Select(result => result.Result.Key));
        Assert.Equal([typeof(DiscoveryTestSubject)], failures);
        Assert.Equal([LogLevel.Warning], logger.Levels);
    }

    [Fact]
    public async Task WhenADiscoveryThrows_ThenItsFailureIsReportedAndTheOthersCarryOn()
    {
        // Arrange
        var service = CreateService(
            TimeProvider.System,
            new LambdaDiscovery<DiscoveryTestSubject>(_ => TestScans.YieldThenThrow(new TestDiscoveryResult("a"))),
            new LambdaDiscovery<OtherDiscoveryTestSubject>(_ => TestScans.Yield(new TestDiscoveryResult("c"))));
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
            new LambdaDiscovery<DiscoveryTestSubject>(cancellationToken =>
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
        var service = CreateService(timeProvider, new LambdaDiscovery<DiscoveryTestSubject>(cancellationToken => TestScans.Hang(cancellationToken)));
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
            new LambdaDiscovery<DiscoveryTestSubject>(_ => TestScans.IgnoreCancellation(release.Task, ended, new TestDiscoveryResult("late"))));
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
            new LambdaDiscovery<DiscoveryTestSubject>(cancellationToken => TestScans.ThrowDisposedWhenCanceled(cancellationToken)));
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
            new LambdaDiscovery<DiscoveryTestSubject>(_ => TestScans.ThrowIgnoringCancellation(release.Task)));
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
            new LambdaDiscovery<DiscoveryTestSubject>(_ => TestScans.IgnoreCancellation(release.Task, ended, new TestDiscoveryResult("late"))));
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
        var service = CreateService(TimeProvider.System, new LambdaDiscovery<DiscoveryTestSubject>(cancellationToken => TestScans.Hang(cancellationToken)));
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
            new LambdaDiscovery<OtherDiscoveryTestSubject>(_ => TestScans.Yield(new TestDiscoveryResult("c"))));
        var results = new ConcurrentQueue<DiscoveredSubject>();

        // Act
        await service.ScanAsync([typeof(DiscoveryTestSubject)], [], results.Enqueue, _ => { }, CancellationToken.None);

        // Assert
        Assert.Empty(results);
        Assert.False(service.HasSubjectDiscoveries([typeof(DiscoveryTestSubject)]));
        Assert.True(service.HasSubjectDiscoveries([typeof(OtherDiscoveryTestSubject)]));
    }

    [Fact]
    public async Task WhenAScanStartsWhileAnotherRuns_ThenItsDiscoveriesStartOnlyAfterTheFirstHasEnded()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startCount = 0;
        var service = CreateService(TimeProvider.System, new LambdaDiscovery<DiscoveryTestSubject>(_ =>
        {
            if (Interlocked.Increment(ref startCount) == 1)
            {
                firstStarted.SetResult();
                return TestScans.IgnoreCancellation(release.Task, firstEnded, new TestDiscoveryResult("first"));
            }

            secondStarted.TrySetResult(firstEnded.Task.IsCompleted);
            return TestScans.Yield(new TestDiscoveryResult("second"));
        }));
        var firstResults = new ConcurrentQueue<DiscoveredSubject>();
        var secondResults = new ConcurrentQueue<DiscoveredSubject>();
        var firstScan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], firstResults.Enqueue, _ => { }, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TestTimeouts.Default);

        // Act
        var secondScan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], secondResults.Enqueue, _ => { }, CancellationToken.None);

        // A check that nothing happens, so its bound is short.
        var hasStartedEarly = await Task.WhenAny(secondStarted.Task, Task.Delay(TimeSpan.FromMilliseconds(200))) == secondStarted.Task;
        release.SetResult();
        await firstScan.WaitAsync(TestTimeouts.Default);
        await secondScan.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.False(hasStartedEarly);
        Assert.True(await secondStarted.Task, "The second scan started a subject discovery before the first one ended.");
        Assert.Equal(["first"], firstResults.Select(result => result.Result.Key));
        Assert.Equal(["second"], secondResults.Select(result => result.Result.Key));
    }

    [Fact]
    public async Task WhenAWaitingScanIsCanceled_ThenItRunsNothingAndTheNextScanStillWaitsAndRuns()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startCount = 0;
        var service = CreateService(TimeProvider.System, new LambdaDiscovery<DiscoveryTestSubject>(_ =>
        {
            if (Interlocked.Increment(ref startCount) == 1)
            {
                firstStarted.SetResult();
                return TestScans.IgnoreCancellation(release.Task, firstEnded, new TestDiscoveryResult("first"));
            }

            laterStarted.TrySetResult(firstEnded.Task.IsCompleted);
            return TestScans.Yield(new TestDiscoveryResult("third"));
        }));
        var firstScan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], _ => { }, _ => { }, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TestTimeouts.Default);
        using var cancellation = new CancellationTokenSource();
        var waitingScan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], _ => { }, _ => { }, cancellation.Token);

        // Act
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingScan.WaitAsync(TestTimeouts.Default));
        var thirdResults = new ConcurrentQueue<DiscoveredSubject>();
        var thirdScan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], thirdResults.Enqueue, _ => { }, CancellationToken.None);
        release.SetResult();
        await firstScan.WaitAsync(TestTimeouts.Default);
        await thirdScan.WaitAsync(TestTimeouts.Default);

        // Assert - a canceled scan that released the lock it never held would let the third one run beside the first.
        Assert.True(await laterStarted.Task, "The scan after the canceled one started before the first one ended.");
        Assert.Equal(2, startCount);
        Assert.Equal(["third"], thirdResults.Select(result => result.Result.Key));
    }

    [Fact]
    public async Task WhenAScanHasWaitedForAnother_ThenItsTimeLimitCountsFromWhenItRuns()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startCount = 0;
        var service = CreateService(timeProvider, new LambdaDiscovery<DiscoveryTestSubject>(cancellationToken =>
        {
            if (Interlocked.Increment(ref startCount) == 1)
            {
                firstStarted.SetResult();
                return TestScans.IgnoreCancellation(release.Task, firstEnded, new TestDiscoveryResult("first"));
            }

            secondToken.SetResult(cancellationToken);
            return TestScans.Hang(cancellationToken);
        }));
        var firstScan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], _ => { }, _ => { }, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TestTimeouts.Default);
        var failures = new ConcurrentQueue<Type>();
        var secondScan = service.ScanAsync([typeof(DiscoveryTestSubject)], [], _ => { }, failures.Enqueue, CancellationToken.None);
        var waited = TimeSpan.FromSeconds(20);
        timeProvider.Advance(waited);
        release.SetResult();
        await firstScan.WaitAsync(TestTimeouts.Default);
        var scanToken = await secondToken.Task.WaitAsync(TestTimeouts.Default);

        // Act - past the time limit counted from the call, but not from when the scan runs.
        timeProvider.Advance(SubjectDiscoveryService.ScanTimeout - waited);
        var isCanceledEarly = scanToken.IsCancellationRequested;
        timeProvider.Advance(waited);
        await secondScan.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.False(isCanceledEarly);
        Assert.True(scanToken.IsCancellationRequested);
        Assert.Empty(failures);
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
