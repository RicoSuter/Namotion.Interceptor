using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectServerBaseTests
{
    [Fact]
    public async Task WhenStartServerAsyncWritesAProperty_ThenTheChangeIsDelivered()
    {
        // Arrange
        var person = CreatePerson();
        using var server = new TestServer(person)
        {
            OnStart = (_, _, _) =>
            {
                // A write made while the server starts, before it would accept clients.
                person.LastName = "During start";
                return Task.CompletedTask;
            }
        };

        try
        {
            // Act
            await server.StartAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.WrittenChanges.Any(change => Equals(change.GetNewValue<string?>(), "During start")),
                message: "A change made inside StartServerAsync should reach the write handler.");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheServerRestarts_ThenInitializeAsyncRunsOnceAndItsTeardownRunsOnceAfterStop()
    {
        // Arrange
        using var server = new TestServer(CreatePerson())
        {
            OnStart = (_, _, attemptNumber) => attemptNumber < 3
                ? throw new InvalidOperationException("start failed")
                : Task.CompletedTask
        };

        await server.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => server.Diagnostics.IsOperational == true);

        // Act
        await server.StopAsync(CancellationToken.None);

        // Assert
        var events = server.Events.ToArray();
        Assert.Equal(1, events.Count(e => e == "initialize"));
        Assert.Equal(1, events.Count(e => e == "initialize-teardown"));
        Assert.Equal("initialize-teardown", events[^1]);
        Assert.Equal(3, server.StartCount);
    }

    [Fact]
    public async Task WhenStartServerAsyncFails_ThenTheErrorIsReportedAndTheRestartDelayIsRequestedPerFailure()
    {
        // Arrange
        var failure = new InvalidOperationException("start failed");
        using var server = new TestServer(CreatePerson())
        {
            OnStart = (_, _, _) => throw failure
        };

        try
        {
            // Act
            await server.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => server.RequestedDelays.Count >= 3);

            // Assert
            Assert.Equal(new[] { 1, 2, 3 }, server.RequestedDelays.Take(3));
            Assert.Same(failure, server.Diagnostics.LastError);
            Assert.False(server.Diagnostics.IsOperational);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAStartSucceedsAfterFailures_ThenConsecutiveFailuresIsReset()
    {
        // Arrange
        using var server = new TestServer(CreatePerson())
        {
            OnStart = (_, _, attemptNumber) => attemptNumber < 3
                ? throw new InvalidOperationException("start failed")
                : Task.CompletedTask
        };

        try
        {
            // Act
            await server.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => server.Diagnostics.IsOperational == true);

            // Assert
            Assert.Equal(0, server.CurrentConsecutiveFailures);
            Assert.Equal(new[] { 1, 2 }, server.RequestedDelays);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenForceKilled_ThenRestartsWithoutDelayOrError()
    {
        // Arrange: a requested delay would stall the restart for an hour, so a restart proves none was requested.
        using var server = new TestServer(CreatePerson())
        {
            RestartDelay = _ => TimeSpan.FromHours(1)
        };

        try
        {
            await server.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => server.Diagnostics.IsOperational == true);

            // Act
            await server.ForceKillAsync();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.StartCount == 2 && server.Diagnostics.IsOperational == true,
                message: "A force-killed attempt should restart immediately.");
            Assert.Null(server.Diagnostics.LastError);
            Assert.Empty(server.RequestedDelays);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenProcessingCompletesUnexpectedly_ThenItIsReportedAndRestarted()
    {
        // Arrange: cancelling the attempt from inside the start makes processing end without a stop or a kill.
        using var server = new TestServer(CreatePerson())
        {
            OnStart = async (attempt, _, attemptNumber) =>
            {
                if (attemptNumber == 1)
                {
                    await attempt.CancelAsync();
                }
            }
        };

        try
        {
            // Act
            await server.StartAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.StartCount == 2 && server.Diagnostics.IsOperational == true);
            Assert.IsType<InvalidOperationException>(server.Diagnostics.LastError);
            Assert.Equal(new[] { 1 }, server.RequestedDelays);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenInitializeAsyncThrows_ThenTheConnectorFaultsWithTheError()
    {
        // Arrange
        var failure = new InvalidOperationException("initialize failed");
        using var server = new TestServer(CreatePerson())
        {
            OnInitialize = () => throw failure
        };

        // Act
        _ = server.StartAsync(CancellationToken.None);

        // Assert
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => server.ExecuteTask!);
        Assert.Same(failure, thrown);
        Assert.Same(failure, server.Diagnostics.LastError);
        Assert.Equal(0, server.StartCount);
    }

    [Fact]
    public async Task WhenStartServerAsyncThrows_ThenTheProcessorIsDisposed()
    {
        // Arrange: the hour-long delay keeps the loop on the first attempt, so the captured processor is that attempt's.
        using var server = new TestServer(CreatePerson())
        {
            OnStart = (_, _, _) => throw new InvalidOperationException("start failed"),
            RestartDelay = _ => TimeSpan.FromHours(1)
        };

        try
        {
            // Act
            await server.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => server.RequestedDelays.Count == 1);

            // Assert
            var processor = server.LastProcessor!;
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => processor.ProcessAsync(new CancellationToken(canceled: true)));
            Assert.Equal(1, server.StartCount);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAnAttemptEnds_ThenTheServerIsNotOperationalBeforeItsTeardownRuns()
    {
        // Arrange
        using var server = new TestServer(CreatePerson());

        await server.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => server.Diagnostics.IsOperational == true);

        // Act
        await server.StopAsync(CancellationToken.None);

        // Assert
        Assert.False(server.OperationalAtTeardown);
    }

    [Fact]
    public async Task WhenStoppedDuringStartServerAsyncWithANonCancellationException_ThenNoErrorIsRecorded()
    {
        // Arrange: StartServerAsync blocks until the host stops, then fails with an exception that is
        // not itself a cancellation, mimicking a transport abort during shutdown rather than a clean one.
        using var server = new TestServer(CreatePerson())
        {
            OnStart = async (_, stoppingToken, _) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                }

                throw new InvalidOperationException("start aborted by stop");
            }
        };

        // Act
        await server.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => server.StartCount == 1);
        await server.StopAsync(CancellationToken.None);

        // Assert
        Assert.Null(server.Diagnostics.LastError);
        Assert.Empty(server.RequestedDelays);
        Assert.True(server.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenTheServerTeardownThrows_ThenTheFailureIsRecordedAndTheRestartDelayIsRequested()
    {
        // Arrange: attempt 1 ends itself by self-cancelling, not by a stop or a kill, so its teardown's
        // exception can only reach the generic catch through the finally block in the code under test.
        var teardownFailure = new InvalidOperationException("teardown failed");
        using var server = new TestServer(CreatePerson())
        {
            OnStart = async (attempt, _, attemptNumber) =>
            {
                if (attemptNumber == 1)
                {
                    await attempt.CancelAsync();
                }
            },
            OnTeardown = () => throw teardownFailure
        };

        try
        {
            // Act
            await server.StartAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => server.StartCount == 2);
            Assert.Same(teardownFailure, server.Diagnostics.LastError);
            Assert.Equal(new[] { 1 }, server.RequestedDelays);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenForceKilledWhileStartServerAsyncWaits_ThenRestartsWithoutError()
    {
        // Arrange: StartServerAsync blocks on the attempt's own token, then turns the kill's cancellation
        // into a non-cancellation exception, proving the kill classification is not narrowed to
        // OperationCanceledException.
        using var server = new TestServer(CreatePerson())
        {
            OnStart = async (attempt, _, attemptNumber) =>
            {
                if (attemptNumber == 1)
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, attempt.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new InvalidOperationException("start aborted by kill");
                    }
                }
            },
            RestartDelay = _ => TimeSpan.FromHours(1)
        };

        try
        {
            // Act
            await server.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => server.StartCount == 1);
            await server.ForceKillAsync();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.StartCount == 2 && server.Diagnostics.IsOperational == true,
                message: "A force-killed attempt should restart immediately.");
            Assert.Null(server.Diagnostics.LastError);
            Assert.Empty(server.RequestedDelays);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenStoppedDuringTheRestartDelay_ThenTheHostedTaskCompletesSuccessfully()
    {
        // Arrange
        using var server = new TestServer(CreatePerson())
        {
            OnStart = (_, _, _) => throw new InvalidOperationException("start failed"),
            RestartDelay = _ => TimeSpan.FromHours(1)
        };

        // Act
        await server.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => server.RequestedDelays.Count == 1);
        await server.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(server.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(1, server.StartCount);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(50, 30)]
    public void WhenGetRestartDelayUsesTheDefault_ThenItGrowsAndIsCapped(int consecutiveFailures, int baseSeconds)
    {
        // Arrange
        using var server = new TestServer(CreatePerson());

        // Act
        var delay = server.DefaultRestartDelay(consecutiveFailures);

        // Assert
        Assert.InRange(delay, TimeSpan.FromSeconds(baseSeconds), TimeSpan.FromSeconds(baseSeconds + 2));
    }

    private static Person CreatePerson()
    {
        var context = InterceptorSubjectContext.Create();
        context.WithRegistry();
        context.WithPropertyChangeSubscriptions();

        return new Person(context);
    }

    private sealed class TestServer : SubjectServerBase
    {
        private int _startCount;

        public TestServer(IInterceptorSubject subject)
            : this(subject, new ConnectorMetrics())
        {
        }

        private TestServer(IInterceptorSubject subject, ConnectorMetrics metrics)
            : base(metrics, NullLogger.Instance)
        {
            RootSubject = subject;
            Diagnostics = new ConnectorDiagnostics(metrics);
        }

        public override IInterceptorSubject RootSubject { get; }

        public override ConnectorDiagnostics Diagnostics { get; }

        public Func<ConnectorRunAttempt, CancellationToken, int, Task>? OnStart { get; init; }

        public Action? OnInitialize { get; init; }

        public Action? OnTeardown { get; init; }

        public Func<int, TimeSpan> RestartDelay { get; init; } = _ => TimeSpan.Zero;

        public ConcurrentQueue<string> Events { get; } = new();

        public ConcurrentQueue<SubjectPropertyChange> WrittenChanges { get; } = new();

        public ConcurrentQueue<int> RequestedDelays { get; } = new();

        public ChangeQueueProcessor? LastProcessor { get; private set; }

        public bool? OperationalAtTeardown { get; private set; }

        public int StartCount => Volatile.Read(ref _startCount);

        public int CurrentConsecutiveFailures => ConsecutiveFailures;

        public TimeSpan DefaultRestartDelay(int consecutiveFailures) => base.GetRestartDelay(consecutiveFailures);

        public Task ForceKillAsync() => ForceKillCurrentAttemptAsync();

        protected override Task<IAsyncDisposable?> InitializeAsync(CancellationToken stoppingToken)
        {
            OnInitialize?.Invoke();
            Events.Enqueue("initialize");
            return Task.FromResult<IAsyncDisposable?>(new Teardown(() => Events.Enqueue("initialize-teardown")));
        }

        protected override ChangeQueueProcessor CreateChangeQueueProcessor(Action<long> dropHandler)
        {
            var processor = new ChangeQueueProcessor(
                source: this,
                RootSubject.Context,
                propertyFilter: _ => true,
                writeHandler: (changes, _) =>
                {
                    foreach (var change in changes.Span)
                    {
                        WrittenChanges.Enqueue(change);
                    }

                    return ValueTask.CompletedTask;
                },
                ChangeDeliveryRule.SourceValuesAreSettled,
                bufferTime: TimeSpan.Zero,
                maxQueueDepth: null,
                logger: NullLogger.Instance,
                dropHandler: dropHandler);

            LastProcessor = processor;
            return processor;
        }

        protected override async Task<IAsyncDisposable?> StartServerAsync(ConnectorRunAttempt attempt, CancellationToken stoppingToken)
        {
            var attemptNumber = Interlocked.Increment(ref _startCount);
            if (OnStart is not null)
            {
                await OnStart(attempt, stoppingToken, attemptNumber);
            }

            return new Teardown(() =>
            {
                OperationalAtTeardown = Diagnostics.IsOperational;
                OnTeardown?.Invoke();
            });
        }

        protected override TimeSpan GetRestartDelay(int consecutiveFailures)
        {
            RequestedDelays.Enqueue(consecutiveFailures);
            return RestartDelay(consecutiveFailures);
        }

        private sealed class Teardown(Action dispose) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
