using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests;

public class ChangeQueueBackgroundServiceTests
{
    [Fact]
    public async Task WhenStartAsyncReturns_ThenTheChangeSubscriptionExists()
    {
        // Arrange
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        Assert.False(interceptor.IsIdle);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenProcessAsyncReturns_ThenTheProcessorIsDisposed()
    {
        // Arrange
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
        await service.StopAsync(CancellationToken.None);
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
    public async Task WhenCreateProcessorThrows_ThenStartAsyncThrows()
    {
        // Arrange: no PropertyChangeInterceptor in the context.
        using var service = new TestService(InterceptorSubjectContext.Create());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WhenASecondStartFailsToCreateAProcessor_ThenTheFirstProcessorIsRetainedUntilDispose()
    {
        // Arrange: the first start is cancelled, so its processor is never taken by an execution.
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        var service = new TestService(context);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await service.StartAsync(cancelled.Token);
        service.ThrowOnCreate = true;

        // Act
        var exception = await Record.ExceptionAsync(() => service.StartAsync(cancelled.Token));
        var idleAfterFailedStart = interceptor.IsIdle;
        service.Dispose();

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.False(idleAfterFailedStart);
        Assert.True(interceptor.IsIdle);
    }

    [Fact]
    public async Task WhenStoppedDuringProcessing_ThenTheExecutionCompletesSuccessfully()
    {
        // Arrange: the double throws OperationCanceledException when its session token is cancelled.
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);
        await service.StartAsync(CancellationToken.None);
        var session = await service.Sessions.Reader.ReadAsync();

        // Act
        await service.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(session.Token.IsCancellationRequested);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.True(interceptor.IsIdle);
    }

    [Fact]
    public async Task WhenRestartIsRequested_ThenProcessAsyncRunsAgainWithANewProcessor()
    {
        // Arrange
        using var service = new TestService(CreateContext());
        await service.StartAsync(CancellationToken.None);
        var first = await service.Sessions.Reader.ReadAsync();

        // Act
        service.Restart();
        var second = await service.Sessions.Reader.ReadAsync();

        // Assert
        Assert.True(first.Token.IsCancellationRequested);
        Assert.Equal(2, service.Created.Count);
        Assert.Same(service.Created[1], second.Processor);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.Processor.ProcessAsync(CancellationToken.None));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenProcessAsyncReturnsEarly_ThenARestartRequestRunsItAgain()
    {
        // Arrange: the first session returns on its own, so the loop idles.
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync();
        service.Release.SetResult();
        await AsyncTestHelpers.WaitUntilAsync(() => interceptor.IsIdle);

        // Act
        service.Restart();
        var second = await service.Sessions.Reader.ReadAsync();

        // Assert
        Assert.Equal(2, service.Created.Count);
        Assert.Same(service.Created[1], second.Processor);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenStoppedWhileIdle_ThenExecuteCompletesAndEveryProcessorIsDisposed()
    {
        // Arrange
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync();
        service.Restart();
        await service.Sessions.Reader.ReadAsync();
        service.Release.SetResult();
        await AsyncTestHelpers.WaitUntilAsync(() => interceptor.IsIdle);

        // Act
        await service.StopAsync(CancellationToken.None);

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
        using var service = new TestService(CreateContext()) { IgnoreCancellation = true };
        await service.StartAsync(CancellationToken.None);
        var first = await service.Sessions.Reader.ReadAsync();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = first.Token.Register(() => cancelled.SetResult());

        // Act
        service.Restart();
        service.Restart();
        await cancelled.Task;
        service.Release.SetResult();
        var second = await service.Sessions.Reader.ReadAsync();
        await service.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, service.Created.Count);
        Assert.Same(service.Created[1], second.Processor);
        Assert.False(service.Sessions.Reader.TryRead(out _));
    }

    [Fact]
    public async Task WhenRestartIsRequestedBeforeStart_ThenStartDoesNotRestart()
    {
        // Arrange
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);
        service.Restart();

        // Act
        await service.StartAsync(CancellationToken.None);
        var first = await service.Sessions.Reader.ReadAsync();
        service.Release.SetResult();
        await AsyncTestHelpers.WaitUntilAsync(() => interceptor.IsIdle);

        // Assert: a served request would have cancelled the first session before its processor was disposed.
        Assert.False(first.Token.IsCancellationRequested);
        await service.StopAsync(CancellationToken.None);
        Assert.Single(service.Created);
        Assert.False(service.Sessions.Reader.TryRead(out _));
    }

    [Fact]
    public async Task WhenRestartIsRequestedAfterStop_ThenNothingThrows()
    {
        // Arrange
        using var service = new TestService(CreateContext());
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync();
        await service.StopAsync(CancellationToken.None);

        // Act
        service.Restart();
        service.Restart();

        // Assert
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Single(service.Created);
    }

    [Fact]
    public async Task WhenRestartIsRequestedAfterAStopAndASecondStart_ThenTheRequestIsServed()
    {
        // Arrange: the same instance is stopped and started again, as a graph detach and reattach does.
        using var service = new TestService(CreateContext());
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync();
        await service.StopAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        var second = await service.Sessions.Reader.ReadAsync();

        // Act
        service.Restart();
        var third = await service.Sessions.Reader.ReadAsync();

        // Assert
        Assert.True(second.Token.IsCancellationRequested);
        Assert.Equal(3, service.Created.Count);
        Assert.Same(service.Created[2], third.Processor);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenProcessAsyncFaultsAndTheServiceIsStartedAgain_ThenARestartRequestIsServed()
    {
        // Arrange: the execution faults, a stale request arrives, then the service is stopped and started again.
        using var service = new TestService(CreateContext());
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync();
        service.Release.SetException(new InvalidOperationException("Processing failed."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteTask!);
        service.Restart();
        await service.StopAsync(CancellationToken.None);
        service.Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await service.StartAsync(CancellationToken.None);
        var second = await service.Sessions.Reader.ReadAsync();

        // Act
        service.Restart();
        var third = await service.Sessions.Reader.ReadAsync();

        // Assert
        Assert.True(second.Token.IsCancellationRequested);
        Assert.Equal(3, service.Created.Count);
        Assert.Same(service.Created[2], third.Processor);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.Created[0].ProcessAsync(CancellationToken.None));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenCreateProcessorThrowsDuringARestart_ThenTheExecutionFaults()
    {
        // Arrange
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        using var service = new TestService(context);
        await service.StartAsync(CancellationToken.None);
        var first = await service.Sessions.Reader.ReadAsync();
        service.ThrowOnCreate = true;

        // Act
        service.Restart();

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteTask!);
        Assert.True(first.Token.IsCancellationRequested);
        Assert.Single(service.Created);
        Assert.True(interceptor.IsIdle);
        await service.StopAsync(CancellationToken.None);
    }

    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create().WithFullPropertyTracking();

    private sealed class TestService(IInterceptorSubjectContext context) : ChangeQueueBackgroundService
    {
        public List<ChangeQueueProcessor> Created { get; } = [];
        public TaskCompletionSource Release { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<(ChangeQueueProcessor Processor, CancellationToken Token)> Sessions { get; } =
            Channel.CreateUnbounded<(ChangeQueueProcessor, CancellationToken)>();
        public ChangeQueueProcessor? ProcessedWith { get; private set; }
        public bool ThrowOnCreate { get; set; }
        public bool IgnoreCancellation { get; init; }

        public void Restart() => RequestRestart();

        protected override ChangeQueueProcessor CreateProcessor()
        {
            if (ThrowOnCreate)
            {
                throw new InvalidOperationException("Processor creation failed.");
            }

            var processor = new ChangeQueueProcessor(
                this, context, _ => true, (_, _) => ValueTask.CompletedTask,
                ChangeDeliveryRule.SourceValuesAreSettled, bufferTime: null, maxQueueDepth: null,
                NullLogger.Instance);
            Created.Add(processor);
            return processor;
        }

        // Throws OperationCanceledException on a restart or stop, which the base has to treat as the session's
        // end; IgnoreCancellation holds the session open until Release completes instead.
        protected override async Task ProcessAsync(ChangeQueueProcessor processor, CancellationToken stoppingToken)
        {
            ProcessedWith = processor;
            Sessions.Writer.TryWrite((processor, stoppingToken));
            await (IgnoreCancellation ? Release.Task : Release.Task.WaitAsync(stoppingToken));
        }
    }
}
