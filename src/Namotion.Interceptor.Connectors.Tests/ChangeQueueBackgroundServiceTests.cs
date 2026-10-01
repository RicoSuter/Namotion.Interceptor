using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests;

public class ChangeQueueBackgroundServiceTests
{
    // Bounds waits that only a lost wake can prolong, so a regression fails the test instead of hanging it.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WhenStartAsyncReturns_ThenTheChangeSubscriptionExists()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert: the stop may outrun the dispatch of the execution, so it is cancelled or completed, never hung.
        Assert.False(interceptor.IsIdle);
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompleted);
        Assert.True(interceptor.IsIdle);
    }

    [Fact]
    public async Task WhenProcessAsyncReturns_ThenTheProcessorIsDisposed()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);
        await service.StartAsync(CancellationToken.None);

        // Act
        service.Release.SetResult();
        await AsyncTestHelpers.WaitUntilAsync(() => interceptor.IsIdle);

        // Assert
        Assert.NotNull(service.ProcessedWith);
        Assert.True(interceptor.IsIdle);
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenStartedWithACancelledTokenAndStopped_ThenTheProcessorIsReleased()
    {
        // Arrange: a cancelled start never enters the execution, so the processor stays with the base.
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await service.StartAsync(cancelled.Token);

        // Act
        await service.StopAsync(CancellationToken.None);

        // Assert
        Assert.Null(service.ProcessedWith);
        Assert.True(interceptor.IsIdle);
    }

    [Fact]
    public async Task WhenStartedWithACancelledTokenAndDisposed_ThenTheProcessorIsReleased()
    {
        // Arrange
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        var service = new TestService(context);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await service.StartAsync(cancelled.Token);

        // Act
        service.Dispose();

        // Assert
        Assert.Null(service.ProcessedWith);
        Assert.True(interceptor.IsIdle);
    }

    [Fact]
    public async Task WhenStartedTwiceWithoutExecution_ThenTheFirstProcessorIsDisposed()
    {
        // Arrange
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await service.StartAsync(cancelled.Token);
        var first = service.Created[0];

        // Act
        await service.StartAsync(cancelled.Token);

        // Assert: the first processor was disposed, the second still holds the subscription.
        Assert.Equal(2, service.Created.Count);
        var processing = first.ProcessAsync(cancelled.Token);
        Assert.True(processing.IsFaulted);
        Assert.IsType<ObjectDisposedException>(processing.Exception!.InnerException);
        Assert.False(interceptor.IsIdle);
    }

    [Fact]
    public async Task WhenTheContextHasNoChangeInterceptor_ThenStartAsyncThrows()
    {
        // Arrange
        using var service = new TestService(InterceptorSubjectContext.Create());

        // Act & Assert: an initial subscription failure is not retried.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.False(service.Failures.Reader.TryRead(out _));
    }

    [Fact]
    public async Task WhenCreateProcessorBuildsItsOwnSubscription_ThenStartAsyncThrowsAndNoSubscriptionRemains()
    {
        // Arrange
        var context = CreateContext();
        using var service = new OwningService(context);

        // Act
        var exception = await Record.ExceptionAsync(() => service.StartAsync(CancellationToken.None));

        // Assert: both the service's subscription and the rejected processor's own are gone.
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("CreateProcessor", exception.Message);
        Assert.True(context.GetService<PropertyChangeInterceptor>().IsIdle);
    }

    [Fact]
    public async Task WhenASecondStartFailsToCreateAProcessor_ThenNoSubscriptionRemains()
    {
        // Arrange: the first start is cancelled, so its processor is never taken by an execution.
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await service.StartAsync(cancelled.Token);
        service.CreationFailuresRemaining = 1;

        // Act
        var exception = await Record.ExceptionAsync(() => service.StartAsync(cancelled.Token));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.True(interceptor.IsIdle);
    }

    [Fact]
    public async Task WhenTheExecutionStartsAfterTheStopWasRequested_ThenProcessAsyncIsNotCalled()
    {
        // Arrange: a cancelled start leaves the processor for an execution that the stop then outruns.
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await service.StartAsync(cancelled.Token);

        // Act
        await service.Execute(cancelled.Token);

        // Assert
        Assert.Null(service.ProcessedWith);
        Assert.True(interceptor.IsIdle);
    }

    [Fact]
    public async Task WhenStoppedDuringProcessing_ThenTheExecutionCompletesSuccessfully()
    {
        // Arrange: the double throws OperationCanceledException when its session token is cancelled.
        using var timeout = new CancellationTokenSource(TestTimeout);
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);
        await service.StartAsync(CancellationToken.None);
        var session = await service.Sessions.Reader.ReadAsync(timeout.Token);

        // Act
        await service.StopAsync(timeout.Token);

        // Assert: the cancellation is the session's end, not a fault.
        Assert.True(session.Token.IsCancellationRequested);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.True(interceptor.IsIdle);
        Assert.False(service.Failures.Reader.TryRead(out _));
    }

    [Fact]
    public async Task WhenProcessAsyncFaultsAfterTheStopWasRequested_ThenTheExecutionCompletesSuccessfully()
    {
        // Arrange: the session ignores its cancellation, so the fault is thrown once the stop is under way.
        using var timeout = new CancellationTokenSource(TestTimeout);
        var context = CreateContext();
        var logger = new CapturingLogger();
        var failure = new InvalidOperationException("Processing failed.");
        using var service = new TestService(context, logger) { IgnoreCancellation = true };
        await service.StartAsync(CancellationToken.None);
        var session = await service.Sessions.Reader.ReadAsync(timeout.Token);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = session.Token.Register(() => cancelled.SetResult());
        var stopping = service.StopAsync(timeout.Token);
        await cancelled.Task.WaitAsync(timeout.Token);

        // Act
        service.Release.SetException(failure);
        await stopping;

        // Assert: the fault is logged without a retry delay, since no retry follows a stop.
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.True(context.GetService<PropertyChangeInterceptor>().IsIdle);
        Assert.Same(failure, Assert.Single(logger.Entries).Exception);
        Assert.False(service.Failures.Reader.TryRead(out _));
    }

    [Fact]
    public async Task WhenRestartIsRequested_ThenProcessAsyncRunsAgainWithANewProcessor()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var service = new TestService(CreateContext());
        await service.StartAsync(CancellationToken.None);
        var first = await service.Sessions.Reader.ReadAsync(timeout.Token);

        // Act
        service.Restart();
        var second = await service.Sessions.Reader.ReadAsync(timeout.Token);

        // Assert: the restart cancellation is the session's end, not a fault.
        Assert.True(first.Token.IsCancellationRequested);
        Assert.Equal(2, service.Created.Count);
        Assert.Same(service.Created[1], second.Processor);
        Assert.False(service.Failures.Reader.TryRead(out _));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.Processor.ProcessAsync(CancellationToken.None));
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenProcessAsyncReturnsEarly_ThenARestartRequestRunsItAgain()
    {
        // Arrange: the first session returns on its own, so the loop idles.
        using var timeout = new CancellationTokenSource(TestTimeout);
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        service.Release.SetResult();
        await AsyncTestHelpers.WaitUntilAsync(() => interceptor.IsIdle);
        service.Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        service.Restart();
        var second = await service.Sessions.Reader.ReadAsync(timeout.Token);

        // Assert: a normal return is not a fault, so nothing was retried before the request, and the subscription
        // released while idle exists again.
        Assert.Equal(2, service.Created.Count);
        Assert.Same(service.Created[1], second.Processor);
        Assert.False(interceptor.IsIdle);
        Assert.False(service.Failures.Reader.TryRead(out _));
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenStoppedWhileIdle_ThenExecuteCompletesAndEveryProcessorIsDisposed()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        service.Restart();
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        service.Release.SetResult();
        await AsyncTestHelpers.WaitUntilAsync(() => interceptor.IsIdle);

        // Act
        await service.StopAsync(timeout.Token);

        // Assert
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(2, service.Created.Count);
        foreach (var processor in service.Created)
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => processor.ProcessAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task WhenRestartIsRequestedTwiceDuringASession_ThenTheRequestsCoalesce()
    {
        // Arrange: the session outlives its cancellation, so both requests land before the restart consumes them.
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var service = new TestService(CreateContext()) { IgnoreCancellation = true };
        await service.StartAsync(CancellationToken.None);
        var first = await service.Sessions.Reader.ReadAsync(timeout.Token);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = first.Token.Register(() => cancelled.SetResult());

        // Act
        service.Restart();
        service.Restart();
        await cancelled.Task.WaitAsync(timeout.Token);
        service.Release.SetResult();
        var second = await service.Sessions.Reader.ReadAsync(timeout.Token);
        await service.StopAsync(timeout.Token);

        // Assert
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(2, service.Created.Count);
        Assert.Same(service.Created[1], second.Processor);
        Assert.False(service.Sessions.Reader.TryRead(out _));
    }

    [Fact]
    public async Task WhenRestartIsRequestedWhileTheNextProcessorIsBeingCreated_ThenItRestartsAgain()
    {
        // Arrange: the second CreateProcessor blocks, so the request lands after the first one was consumed and
        // before the next session publishes its wake.
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var gate = new ManualResetEventSlim();
        using var service = new TestService(CreateContext()) { HoldSecondCreate = gate };
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        service.Restart();
        await service.SecondCreateEntered.Task.WaitAsync(timeout.Token);

        // Act
        service.Restart();
        gate.Set();
        var second = await service.Sessions.Reader.ReadAsync(timeout.Token);
        var third = await service.Sessions.Reader.ReadAsync(timeout.Token);

        // Assert
        Assert.True(second.Token.IsCancellationRequested);
        Assert.Equal(3, service.Created.Count);
        Assert.Same(service.Created[2], third.Processor);
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenRestartIsRequestedBeforeStart_ThenStartDoesNotRestart()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);
        service.Restart();

        // Act
        await service.StartAsync(CancellationToken.None);
        var first = await service.Sessions.Reader.ReadAsync(timeout.Token);
        service.Release.SetResult();
        await AsyncTestHelpers.WaitUntilAsync(() => interceptor.IsIdle);

        // Assert: a served request would have cancelled the first session before its processor was disposed.
        Assert.False(first.Token.IsCancellationRequested);
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Single(service.Created);
        Assert.False(service.Sessions.Reader.TryRead(out _));
    }

    [Fact]
    public async Task WhenRestartIsRequestedAfterStop_ThenNothingThrows()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var service = new TestService(CreateContext());
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        await service.StopAsync(timeout.Token);

        // Act
        service.Restart();
        service.Restart();

        // Assert
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Single(service.Created);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task WhenProcessingFails_ThenRetryUsesANewProcessor(bool synchronous, bool unrelatedCancellation)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        Exception failure = unrelatedCancellation ? new OperationCanceledException() : new InvalidOperationException();
        using var service = new TestService(CreateContext())
        {
            Failure = failure,
            ThrowSynchronously = synchronous
        };

        // Act
        await service.StartAsync(CancellationToken.None);
        var first = await service.Sessions.Reader.ReadAsync(timeout.Token);
        var second = await service.Sessions.Reader.ReadAsync(timeout.Token);

        // Assert: the subscription outlives the failed processor.
        Assert.NotSame(first.Processor, second.Processor);
        var (reported, idle) = await service.Failures.Reader.ReadAsync(timeout.Token);
        Assert.Same(failure, reported);
        Assert.False(idle);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.Processor.ProcessAsync(CancellationToken.None));
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenProcessAsyncFaults_ThenTheFaultIsLoggedOnceWithTheDelay()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        var logger = new CapturingLogger();
        var failure = new InvalidOperationException("Processing failed.");
        var delay = TimeSpan.FromMilliseconds(1);
        using var service = new TestService(CreateContext(), logger) { Failure = failure, RetryDelay = delay };

        // Act
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        await service.StopAsync(timeout.Token);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(failure, entry.Exception);
        Assert.Equal(delay, Assert.Single(entry.State, pair => pair.Key == "RetryDelay").Value);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenProcessingFails_ThenTheRetryWaitsForTheDelay()
    {
        // Arrange: measured from before the start, so the retried session cannot appear earlier than the
        // delay after the fault unless the wait was skipped.
        using var timeout = new CancellationTokenSource(TestTimeout);
        var delay = TimeSpan.FromMilliseconds(200);
        using var service = new TestService(CreateContext()) { Failure = new InvalidOperationException(), RetryDelay = delay };
        var startedAt = Stopwatch.GetTimestamp();

        // Act
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        // Assert: a lower bound only, so a slow runner cannot fail it.
        Assert.True(elapsed >= delay - TimeSpan.FromMilliseconds(20), $"The retry ran after {elapsed} instead of waiting {delay}.");
        Assert.Equal(2, service.Created.Count);
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenGetRetryDelayReturnsANegativeDelay_ThenTheExecutionFaultsNamingIt()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        var context = CreateContext();
        var failure = new InvalidOperationException("Processing failed.");
        using var service = new TestService(context) { Failure = failure, RetryDelay = TimeSpan.FromSeconds(-1) };

        // Act
        await service.StartAsync(CancellationToken.None);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteTask!.WaitAsync(timeout.Token));

        // Assert: the contract violation names the method and carries the fault it was asked about.
        Assert.Contains("GetRetryDelay", exception.Message);
        Assert.Same(failure, exception.InnerException);
        Assert.Single(service.Created);
        Assert.True(context.GetService<PropertyChangeInterceptor>().IsIdle);
        await service.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task WhenCreateProcessorThrowsDuringARestart_ThenTheCreationIsRetried()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);
        await service.StartAsync(CancellationToken.None);
        var first = await service.Sessions.Reader.ReadAsync(timeout.Token);
        service.CreationFailuresRemaining = 1;

        // Act
        service.Restart();
        var (failure, idle) = await service.Failures.Reader.ReadAsync(timeout.Token);
        var second = await service.Sessions.Reader.ReadAsync(timeout.Token);

        // Assert: the subscription was kept through the failed creation, and the retried creation was taken.
        Assert.True(first.Token.IsCancellationRequested);
        Assert.IsType<InvalidOperationException>(failure);
        Assert.False(idle);
        Assert.Equal(3, service.CreationAttempts);
        Assert.Equal(2, service.Created.Count);
        Assert.Same(service.Created[1], second.Processor);
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.True(interceptor.IsIdle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenBackoffIsInterrupted_ThenStopOrRestartCompletesPromptly(bool restart)
    {
        // Arrange: the backoff is far longer than the test, so only the interruption can end it.
        using var timeout = new CancellationTokenSource(TestTimeout);
        var context = CreateContext();
        using var service = new TestService(context)
        {
            Failure = new InvalidOperationException(),
            RetryDelay = TimeSpan.FromHours(1)
        };
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        await service.Failures.Reader.ReadAsync(timeout.Token);

        // Act
        if (restart)
        {
            service.Restart();
            await service.Sessions.Reader.ReadAsync(timeout.Token);
        }
        await service.StopAsync(timeout.Token);

        // Assert
        Assert.Equal(restart ? 2 : 1, service.Created.Count);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.True(context.GetService<PropertyChangeInterceptor>().IsIdle);
    }

    [Fact]
    public async Task WhenStoppedDuringTheBackoffAndStartedAgain_ThenARestartRequestIsServed()
    {
        // Arrange: the first session faults and the service is stopped during the backoff, then started again.
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var service = new TestService(CreateContext())
        {
            Failure = new InvalidOperationException("Processing failed."),
            RetryDelay = TimeSpan.FromHours(1)
        };
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        await service.Failures.Reader.ReadAsync(timeout.Token);
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        await service.StartAsync(CancellationToken.None);
        var second = await service.Sessions.Reader.ReadAsync(timeout.Token);

        // Act
        service.Restart();
        var third = await service.Sessions.Reader.ReadAsync(timeout.Token);

        // Assert
        Assert.True(second.Token.IsCancellationRequested);
        Assert.Equal(3, service.Created.Count);
        Assert.Same(service.Created[2], third.Processor);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.Created[0].ProcessAsync(CancellationToken.None));
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenAChangeIsMadeWhileARestartCreatesTheNextProcessor_ThenThatProcessorDeliversIt()
    {
        // Arrange: the second CreateProcessor blocks after the first processor was disposed, so the change is made
        // while no processor exists.
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var gate = new ManualResetEventSlim();
        var context = CreateContext();
        var person = new Person(context);
        using var service = new TestService(context) { HoldSecondCreate = gate, Drain = true };
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        service.Restart();
        await service.SecondCreateEntered.Task.WaitAsync(timeout.Token);

        // Act
        person.FirstName = "Restart";
        gate.Set();

        // Assert: the second processor was built on the first one's subscription.
        Assert.Equal("Restart", await service.Written.Reader.ReadAsync(timeout.Token));
        Assert.Same(service.Subscriptions[0], service.Subscriptions[1]);
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenAChangeIsMadeDuringTheRetryDelay_ThenTheRetriedProcessorDeliversIt()
    {
        // Arrange: the backoff is far longer than the test, and a restart ends it once the change is made.
        using var timeout = new CancellationTokenSource(TestTimeout);
        var context = CreateContext();
        var person = new Person(context);
        using var service = new TestService(context)
        {
            Failure = new InvalidOperationException(),
            RetryDelay = TimeSpan.FromHours(1),
            Drain = true
        };
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        await service.Failures.Reader.ReadAsync(timeout.Token);

        // Act
        person.FirstName = "Retry";
        service.Restart();

        // Assert: the retried processor was built on the failed one's subscription.
        Assert.Equal("Retry", await service.Written.Reader.ReadAsync(timeout.Token));
        Assert.Same(service.Subscriptions[0], service.Subscriptions[1]);
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenThreeRunsFaultInARow_ThenTheSubscriptionIsReleasedUntilARunEndsWithoutAFault()
    {
        // Arrange: each backoff is ended by a restart, so the runs fault back to back.
        using var timeout = new CancellationTokenSource(TestTimeout);
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        var person = new Person(context);
        using var service = new TestService(context)
        {
            Failure = new InvalidOperationException(),
            FailingSessions = [1, 2, 3],
            RetryDelay = TimeSpan.FromHours(1),
            Drain = true
        };
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        var first = await service.Failures.Reader.ReadAsync(timeout.Token);
        service.Restart();
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        var second = await service.Failures.Reader.ReadAsync(timeout.Token);
        service.Restart();
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        await service.Failures.Reader.ReadAsync(timeout.Token);

        // Act
        await AsyncTestHelpers.WaitUntilAsync(() => interceptor.IsIdle);
        service.Restart();
        await service.Sessions.Reader.ReadAsync(timeout.Token);
        person.FirstName = "Resubscribed";

        // Assert: the first two faults kept the subscription, the third released it, and the fourth run subscribed again.
        Assert.False(first.Idle);
        Assert.False(second.Idle);
        Assert.False(interceptor.IsIdle);
        Assert.Equal("Resubscribed", await service.Written.Reader.ReadAsync(timeout.Token));
        Assert.Same(service.Subscriptions[0], service.Subscriptions[2]);
        Assert.NotSame(service.Subscriptions[2], service.Subscriptions[3]);
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.True(interceptor.IsIdle);
    }

    [Fact]
    public async Task WhenARunEndsWithoutAFaultBetweenFaults_ThenTheFaultsAreNotConsecutive()
    {
        // Arrange: two faults, a run ended by a restart, then two more faults; a change made after the fourth
        // fault is delivered only if the subscription was kept.
        using var timeout = new CancellationTokenSource(TestTimeout);
        var context = CreateContext();
        var person = new Person(context);
        using var service = new TestService(context)
        {
            Failure = new InvalidOperationException(),
            FailingSessions = [1, 2, 4, 5],
            RetryDelay = TimeSpan.FromHours(1),
            Drain = true
        };
        await service.StartAsync(CancellationToken.None);
        for (var session = 1; session <= 5; session++)
        {
            await service.Sessions.Reader.ReadAsync(timeout.Token);
            if (session != 3)
            {
                await service.Failures.Reader.ReadAsync(timeout.Token);
            }

            if (session < 5)
            {
                service.Restart();
            }
        }

        // Act
        person.FirstName = "Kept";
        service.Restart();

        // Assert
        Assert.Equal("Kept", await service.Written.Reader.ReadAsync(timeout.Token));
        Assert.Equal(6, service.Subscriptions.Count);
        Assert.All(service.Subscriptions, subscription => Assert.Same(service.Subscriptions[0], subscription));
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenProcessAsyncIsNotOverridden_ThenTheProcessorIsDrained()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        var context = CreateContext();
        var person = new Person(context);
        using var service = new DrainingService(context);
        await service.StartAsync(CancellationToken.None);

        // Act
        person.FirstName = "Changed";

        // Assert
        await service.Written.Task.WaitAsync(timeout.Token);
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenRestartIsRequestedBetweenStartAndExecution_ThenTheFirstSessionIsRestarted()
    {
        // Arrange: a cancelled start keeps the execution from running, so the request lands after the start's
        // processor was created and before the execution publishes its first wake.
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var stopping = new CancellationTokenSource();
        using var service = new TestService(CreateContext());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await service.StartAsync(cancelled.Token);
        service.Restart();

        // Act
        var execution = service.Execute(stopping.Token);
        var first = await service.Sessions.Reader.ReadAsync(timeout.Token);
        var second = await service.Sessions.Reader.ReadAsync(timeout.Token);

        // Assert: the start's processor may predate the request, so it is replaced.
        Assert.True(first.Token.IsCancellationRequested);
        Assert.Equal(2, service.Created.Count);
        Assert.Same(service.Created[1], second.Processor);
        await stopping.CancelAsync();
        await execution.WaitAsync(timeout.Token);
    }

    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create().WithFullPropertyTracking();

    private sealed class TestService(IInterceptorSubjectContext context, ILogger? logger = null)
        : ChangeQueueBackgroundService(logger ?? NullLogger.Instance)
    {
        private int _creationAttempts;
        private int _processingCount;

        public List<ChangeQueueProcessor> Created { get; } = [];
        public List<PropertyChangeQueueSubscription> Subscriptions { get; } = [];
        public int CreationAttempts => Volatile.Read(ref _creationAttempts);
        public int CreationFailuresRemaining { get; set; }
        public Exception? Failure { get; init; }
        public HashSet<int> FailingSessions { get; init; } = [1];
        public bool ThrowSynchronously { get; init; }
        public TimeSpan RetryDelay { get; init; } = TimeSpan.Zero;
        public bool IgnoreCancellation { get; init; }
        public bool Drain { get; init; }
        public ManualResetEventSlim? HoldSecondCreate { get; init; }
        public TaskCompletionSource Release { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondCreateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<(ChangeQueueProcessor Processor, CancellationToken Token)> Sessions { get; } =
            Channel.CreateUnbounded<(ChangeQueueProcessor, CancellationToken)>();
        public Channel<(Exception Exception, bool Idle)> Failures { get; } = Channel.CreateUnbounded<(Exception, bool)>();
        public Channel<string?> Written { get; } = Channel.CreateUnbounded<string?>();
        public ChangeQueueProcessor? ProcessedWith { get; private set; }

        public void Restart() => RequestRestart();

        public Task Execute(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);

        protected override IInterceptorSubjectContext Context => context;

        protected override ChangeQueueProcessor CreateProcessor(PropertyChangeQueueSubscription subscription)
        {
            Interlocked.Increment(ref _creationAttempts);
            if (CreationFailuresRemaining > 0)
            {
                CreationFailuresRemaining--;
                throw new InvalidOperationException("Processor creation failed.");
            }

            if (Created.Count == 1 && HoldSecondCreate is not null)
            {
                SecondCreateEntered.SetResult();
                HoldSecondCreate.Wait();
            }

            var processor = new ChangeQueueProcessor(
                this, subscription, _ => true,
                (changes, _) =>
                {
                    foreach (var change in changes.Span)
                    {
                        Written.Writer.TryWrite(change.GetNewValue<string?>());
                    }

                    return ValueTask.CompletedTask;
                },
                ChangeDeliveryRule.SourceValuesAreSettled, bufferTime: null, maxQueueDepth: null,
                NullLogger.Instance);
            Created.Add(processor);
            Subscriptions.Add(subscription);
            return processor;
        }

        // Records whether the change subscription was gone when the delay was asked for.
        protected override TimeSpan GetRetryDelay(Exception exception)
        {
            var idle = context.TryGetService<PropertyChangeInterceptor>()?.IsIdle ?? true;
            Failures.Writer.TryWrite((exception, idle));
            return RetryDelay;
        }

        // Throws OperationCanceledException on a restart or stop, which the base has to treat as the session's
        // end; IgnoreCancellation holds the session open until Release completes instead, and Drain runs the
        // processor. Failure fails each session listed in FailingSessions, either before the method returns or
        // through its task.
        protected override Task ProcessAsync(ChangeQueueProcessor processor, CancellationToken stoppingToken)
        {
            ProcessedWith = processor;
            Sessions.Writer.TryWrite((processor, stoppingToken));
            if (FailingSessions.Contains(Interlocked.Increment(ref _processingCount)) && Failure is { } failure)
            {
                if (ThrowSynchronously)
                {
                    throw failure;
                }

                return Task.FromException(failure);
            }

            if (Drain)
            {
                return processor.ProcessAsync(stoppingToken);
            }

            return IgnoreCancellation ? Release.Task : Release.Task.WaitAsync(stoppingToken);
        }
    }

    private sealed class DrainingService(IInterceptorSubjectContext context) : ChangeQueueBackgroundService(NullLogger.Instance)
    {
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override IInterceptorSubjectContext Context => context;

        protected override ChangeQueueProcessor CreateProcessor(PropertyChangeQueueSubscription subscription) => new(
            this, subscription, _ => true,
            (_, _) =>
            {
                Written.TrySetResult();
                return ValueTask.CompletedTask;
            },
            ChangeDeliveryRule.SourceValuesMayBeStale, bufferTime: null, maxQueueDepth: null, NullLogger.Instance);
    }

    private sealed class OwningService(IInterceptorSubjectContext context) : ChangeQueueBackgroundService(NullLogger.Instance)
    {
        protected override IInterceptorSubjectContext Context => context;

        protected override ChangeQueueProcessor CreateProcessor(PropertyChangeQueueSubscription subscription) => new(
            this, context, _ => true, (_, _) => ValueTask.CompletedTask,
            ChangeDeliveryRule.SourceValuesMayBeStale, bufferTime: null, maxQueueDepth: null, NullLogger.Instance);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, exception, state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
            }
        }
    }
}
