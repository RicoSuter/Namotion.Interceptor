using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectSourceBaseOverflowTests
{
    [Fact]
    public async Task WhenSourceConfiguresBoundedQueue_ThenOverflowHandlerFires()
    {
        // Arrange
        var context = new InterceptorSubjectContext();
        context.WithRegistry();
        context.WithPropertyChangeQueue();

        var subject = new Person(context);

        var overflows = new System.Collections.Concurrent.ConcurrentQueue<ChangeQueueOverflow>();
        using var source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            CreateChangeQueueConfigurationOverride = () => new ChangeQueueProcessorConfiguration
            {
                // Large buffer time keeps the periodic flush from draining the queue, so the bound
                // is exercised purely by enqueue-side overflow.
                BufferTime = TimeSpan.FromMinutes(10),
                OverflowBehavior = OverflowBehavior.DropOldest,
                MaxQueueSize = 2,
                OverflowHandler = overflow => overflows.Enqueue(overflow),
            },
        };

        // The processor only handles changes to properties whose source is this source, and ignores
        // changes that originated from this source. Claim ownership, then mutate locally (source null)
        // so the changes are queued for outbound writing.
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        using var cancellation = new CancellationTokenSource();
        await source.StartAsync(cancellation.Token);

        // Act - continuously produce unique changes until the bounded queue overflows. Looping covers
        // the window between StartAsync returning and the background processor's subscription going
        // live, so no change is missed because of startup timing. Unique values keep the equality
        // interceptor from coalescing the changes away.
        var producer = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested && overflows.Count < 3)
            {
                subject.FirstName = Guid.NewGuid().ToString("N");
                await Task.Yield();
            }
        });

        await AsyncTestHelpers.WaitUntilAsync(
            () => overflows.Count >= 3,
            message: "Three overflow events should be reported through the seam");

        // Assert
        Assert.All(overflows, overflow => Assert.Equal(OverflowBehavior.DropOldest, overflow.OverflowBehavior));

        // Cleanup
        await cancellation.CancelAsync();
        await producer;
        await source.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenOverrideReturnsCachedConfiguration_ThenBaseDoesNotMutateIt()
    {
        // Arrange
        var context = new InterceptorSubjectContext();
        context.WithRegistry();
        context.WithPropertyChangeQueue();

        var subject = new Person(context);

        var overflows = new System.Collections.Concurrent.ConcurrentQueue<ChangeQueueOverflow>();
        Action<ChangeQueueOverflow> sourceOverflowHandler = overflow => overflows.Enqueue(overflow);

        // A single configuration instance reused on every CreateChangeQueueConfiguration call. The base
        // must derive its own copy: if it mutated this instance, the handler would be replaced by the
        // logging wrapper, and wrappers would stack across reconnects.
        var cachedConfiguration = new ChangeQueueProcessorConfiguration
        {
            BufferTime = TimeSpan.FromMinutes(10),
            OverflowBehavior = OverflowBehavior.DropOldest,
            MaxQueueSize = 2,
            OverflowHandler = sourceOverflowHandler,
        };

        using var source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            CreateChangeQueueConfigurationOverride = () => cachedConfiguration,
        };

        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        using var cancellation = new CancellationTokenSource();
        await source.StartAsync(cancellation.Token);

        // Act - produce until an overflow fires, proving the processor consumed the configuration
        var producer = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested && overflows.IsEmpty)
            {
                subject.FirstName = Guid.NewGuid().ToString("N");
                await Task.Yield();
            }
        });

        await AsyncTestHelpers.WaitUntilAsync(
            () => !overflows.IsEmpty,
            message: "At least one overflow event should be reported");

        // Assert - the cached instance still carries the source's handler, untouched by the base
        Assert.Same(sourceOverflowHandler, cachedConfiguration.OverflowHandler);

        // Cleanup
        await cancellation.CancelAsync();
        await producer;
        await source.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenOverflowFiresRepeatedly_ThenWarningLoggingIsThrottled()
    {
        // Arrange
        var context = new InterceptorSubjectContext();
        context.WithRegistry();
        context.WithPropertyChangeQueue();

        var subject = new Person(context);

        var overflows = new System.Collections.Concurrent.ConcurrentQueue<ChangeQueueOverflow>();
        var logger = new WarningCountingLogger();
        using var source = new TestSubjectSource(subject, context, logger)
        {
            CreateChangeQueueConfigurationOverride = () => new ChangeQueueProcessorConfiguration
            {
                BufferTime = TimeSpan.FromMinutes(10),
                OverflowBehavior = OverflowBehavior.DropOldest,
                MaxQueueSize = 2,
                OverflowHandler = overflow => overflows.Enqueue(overflow),
            },
        };

        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        using var cancellation = new CancellationTokenSource();
        await source.StartAsync(cancellation.Token);

        // Act - drive many overflow events, all well inside the throttle window
        var producer = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested && overflows.Count < 10)
            {
                subject.FirstName = Guid.NewGuid().ToString("N");
                await Task.Yield();
            }
        });

        await AsyncTestHelpers.WaitUntilAsync(
            () => overflows.Count >= 10,
            message: "Ten overflow events should be reported through the seam");

        // Assert - the source handler saw every event, but logging collapsed to a single warning
        Assert.True(overflows.Count >= 10);
        Assert.Equal(1, logger.WarningCount);

        // Cleanup
        await cancellation.CancelAsync();
        await producer;
        await source.StopAsync(CancellationToken.None);
    }

    private sealed class WarningCountingLogger : ILogger
    {
        private int _warningCount;

        public int WarningCount => Volatile.Read(ref _warningCount);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Interlocked.Increment(ref _warningCount);
            }
        }
    }
}
