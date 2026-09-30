using System;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Testing;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.WebSocket.Server;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

public class WebSocketSubjectChangeProcessorTests
{
    [Fact]
    public async Task WhenStartAsyncReturns_ThenTheChangeSubscriptionExists()
    {
        // Arrange: a client welcomed once the host start returns must not miss a change made before
        // the execution runs, so the subscription has to exist by then.
        var context = CreateContext();
        var propertyChangeInterceptor = context.GetService<PropertyChangeInterceptor>();
        using var processor = CreateProcessor(context);
        Assert.True(propertyChangeInterceptor.IsIdle);

        try
        {
            // Act
            await processor.StartAsync(CancellationToken.None);

            // Assert
            Assert.False(propertyChangeInterceptor.IsIdle);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenFilteringFails_ThenTheServiceCanRecoverOrStopDuringBackoff(bool stopDuringBackoff)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var context = CreateContext();
        var root = new TestRoot(context);
        var pathProvider = new FailingPathProvider();
        var logger = new FailureLogger();
        var handler = new WebSocketSubjectHandler(root,
            new WebSocketServerConfiguration { PathProvider = pathProvider }, NullLogger.Instance);
        using var service = new WebSocketSubjectChangeProcessor(handler, logger);
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        await service.StartAsync(CancellationToken.None);
        try
        {
            // Act
            root.Name = "fails";
            await logger.Failure.Task.WaitAsync(timeout.Token);
            Assert.True(interceptor.IsIdle);
            if (!stopDuringBackoff)
            {
                await AsyncTestHelpers.WaitUntilAsync(() => !interceptor.IsIdle);
                root.Name = "recovered";
                await pathProvider.Recovered.Task.WaitAsync(timeout.Token);
            }
            await service.StopAsync(timeout.Token);

            // Assert
            Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
            Assert.True(interceptor.IsIdle);
            Assert.Equal(!stopDuringBackoff, pathProvider.Recovered.Task.IsCompletedSuccessfully);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FailingPathProvider : PathProviderBase
    {
        private int _calls;
        public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool IsPropertyIncluded(RegisteredSubjectProperty property)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new InvalidOperationException("Filter failed once.");
            }
            Recovered.TrySetResult();
            return true;
        }
    }

    private sealed class FailureLogger : ILogger<WebSocketSubjectChangeProcessor>
    {
        public TaskCompletionSource Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Failure.TrySetResult();
            }
        }
    }

    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

    private static WebSocketSubjectChangeProcessor CreateProcessor(IInterceptorSubjectContext context)
    {
        var handler = new WebSocketSubjectHandler(
            new TestRoot(context), new WebSocketServerConfiguration(), NullLogger.Instance);
        return new WebSocketSubjectChangeProcessor(handler, NullLogger<WebSocketSubjectChangeProcessor>.Instance);
    }
}
