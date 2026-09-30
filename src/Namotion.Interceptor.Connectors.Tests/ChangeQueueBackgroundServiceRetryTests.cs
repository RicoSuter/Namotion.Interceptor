using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests;

public class ChangeQueueBackgroundServiceRetryTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task WhenProcessingFails_ThenRetryUsesANewProcessor(bool synchronous, bool unrelatedCancellation)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        Exception failure = unrelatedCancellation ? new OperationCanceledException() : new InvalidOperationException();
        using var service = new RetryService(CreateContext())
        {
            FirstFailure = failure,
            ThrowSynchronously = synchronous,
            RetryDelay = TimeSpan.Zero
        };

        // Act
        await service.StartAsync(CancellationToken.None);
        var first = await service.Sessions.Reader.ReadAsync(timeout.Token);
        var second = await service.Sessions.Reader.ReadAsync(timeout.Token);

        // Assert
        Assert.NotSame(first, second);
        Assert.Same(failure, await service.Failures.Reader.ReadAsync(timeout.Token));
        Assert.True(service.DisposedBeforePolicy);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.ProcessAsync(CancellationToken.None));
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenRetryCreationFails_ThenTheCreationIsRetried()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var service = new RetryService(CreateContext()) { RetryDelay = TimeSpan.Zero };
        await service.StartAsync(CancellationToken.None);
        var first = await service.Sessions.Reader.ReadAsync(timeout.Token);
        service.CreationFailuresRemaining = 1;

        // Act
        service.Restart();
        var second = await service.Sessions.Reader.ReadAsync(timeout.Token);

        // Assert
        Assert.NotSame(first, second);
        Assert.IsType<InvalidOperationException>(await service.Failures.Reader.ReadAsync(timeout.Token));
        Assert.Equal(3, service.CreationCount);
        await service.StopAsync(timeout.Token);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenInitialCreationFails_ThenRetryPolicyIsNotCalled()
    {
        // Arrange
        using var service = new RetryService(CreateContext())
        {
            CreationFailuresRemaining = 1,
            RetryDelay = TimeSpan.Zero
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.False(service.Failures.Reader.TryRead(out _));
        Assert.Equal(1, service.CreationCount);
    }

    [Fact]
    public async Task WhenRetryIsDeclined_ThenTheOriginalFailurePropagates()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        var failure = new InvalidOperationException();
        using var service = new RetryService(CreateContext()) { FirstFailure = failure };

        // Act
        await service.StartAsync(CancellationToken.None);
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteTask!.WaitAsync(timeout.Token));

        // Assert
        Assert.Same(failure, actual);
        Assert.Equal(1, service.CreationCount);
        await service.StopAsync(timeout.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenBackoffIsInterrupted_ThenStopOrRestartCompletesPromptly(bool restart)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        var context = CreateContext();
        using var service = new RetryService(context)
        {
            FirstFailure = new InvalidOperationException(),
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
        Assert.Equal(restart ? 2 : 1, service.CreationCount);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.True(context.GetService<PropertyChangeInterceptor>().IsIdle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenSessionIsCancelled_ThenRetryPolicyIsNotCalled(bool restart)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var service = new RetryService(CreateContext()) { RetryDelay = TimeSpan.Zero };
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync(timeout.Token);

        // Act
        if (restart)
        {
            service.Restart();
            await service.Sessions.Reader.ReadAsync(timeout.Token);
        }
        await service.StopAsync(timeout.Token);

        // Assert
        Assert.False(service.Failures.Reader.TryRead(out _));
        Assert.Equal(restart ? 2 : 1, service.CreationCount);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenProcessingReturnsNormally_ThenRetryPolicyIsNotCalled()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TestTimeout);
        var context = CreateContext();
        using var service = new RetryService(context) { RetryDelay = TimeSpan.Zero };
        await service.StartAsync(CancellationToken.None);
        await service.Sessions.Reader.ReadAsync(timeout.Token);

        // Act
        service.Completion.SetResult();
        await AsyncTestHelpers.WaitUntilAsync(() => context.GetService<PropertyChangeInterceptor>().IsIdle);
        await service.StopAsync(timeout.Token);

        // Assert
        Assert.Equal(1, service.CreationCount);
        Assert.False(service.Failures.Reader.TryRead(out _));
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create().WithFullPropertyTracking();

    private sealed class RetryService(IInterceptorSubjectContext context) : ChangeQueueBackgroundService
    {
        private int _creationCount;
        private int _processingCount;

        public int CreationCount => Volatile.Read(ref _creationCount);
        public int CreationFailuresRemaining { get; set; }
        public Exception? FirstFailure { get; init; }
        public bool ThrowSynchronously { get; init; }
        public TimeSpan? RetryDelay { get; init; }
        public bool DisposedBeforePolicy { get; private set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<ChangeQueueProcessor> Sessions { get; } = Channel.CreateUnbounded<ChangeQueueProcessor>();
        public Channel<Exception> Failures { get; } = Channel.CreateUnbounded<Exception>();

        public void Restart() => RequestRestart();

        protected override ChangeQueueProcessor CreateProcessor()
        {
            Interlocked.Increment(ref _creationCount);
            if (CreationFailuresRemaining > 0)
            {
                CreationFailuresRemaining--;
                throw new InvalidOperationException("Processor creation failed.");
            }

            return new ChangeQueueProcessor(this, context, _ => true, (_, _) => ValueTask.CompletedTask,
                ChangeDeliveryRule.SourceValuesAreSettled, bufferTime: null, maxQueueDepth: null, NullLogger.Instance);
        }

        protected override TimeSpan? GetRetryDelay(Exception exception)
        {
            DisposedBeforePolicy = context.GetService<PropertyChangeInterceptor>().IsIdle;
            Failures.Writer.TryWrite(exception);
            return RetryDelay ?? base.GetRetryDelay(exception);
        }

        protected override Task ProcessAsync(ChangeQueueProcessor processor, CancellationToken stoppingToken)
        {
            Sessions.Writer.TryWrite(processor);
            if (Interlocked.Increment(ref _processingCount) == 1 && FirstFailure is { } failure)
            {
                if (ThrowSynchronously)
                {
                    throw failure;
                }
                return Task.FromException(failure);
            }

            return Completion.Task.WaitAsync(stoppingToken);
        }
    }
}
