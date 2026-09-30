using Microsoft.Extensions.Logging.Abstractions;
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
        await service.ExecuteTask!;

        // Assert
        Assert.NotNull(service.ProcessedWith);
        Assert.True(interceptor.IsIdle);
    }

    [Fact]
    public async Task WhenStartedWithACancelledToken_ThenDisposeReleasesTheProcessor()
    {
        // Arrange: a cancelled start never enters the execution, so the processor stays with the base.
        var context = CreateContext();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        var service = new TestService(context);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await service.StartAsync(cancelled.Token);
        await service.StopAsync(CancellationToken.None);
        Assert.Null(service.ProcessedWith);
        Assert.False(interceptor.IsIdle);

        // Act
        service.Dispose();

        // Assert
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
        Assert.Throws<ObjectDisposedException>(() => { first.ProcessAsync(CancellationToken.None).GetAwaiter().GetResult(); });
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

    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create().WithFullPropertyTracking();

    private sealed class TestService(IInterceptorSubjectContext context) : ChangeQueueBackgroundService
    {
        public List<ChangeQueueProcessor> Created { get; } = [];
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ChangeQueueProcessor? ProcessedWith { get; private set; }

        protected override ChangeQueueProcessor CreateProcessor()
        {
            var processor = new ChangeQueueProcessor(
                this, context, _ => true, (_, _) => ValueTask.CompletedTask,
                ChangeDeliveryRule.SourceValuesAreSettled, bufferTime: null, maxQueueDepth: null,
                NullLogger.Instance);
            Created.Add(processor);
            return processor;
        }

        protected override async Task ProcessAsync(ChangeQueueProcessor processor, CancellationToken stoppingToken)
        {
            ProcessedWith = processor;
            await Release.Task.WaitAsync(stoppingToken);
        }
    }
}
