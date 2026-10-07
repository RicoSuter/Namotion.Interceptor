# SubjectServerBase Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `SubjectServerBase` template that subscribes to property changes before each server attempt accepts clients, and migrate the OPC UA, MQTT and WebSocket servers onto it.

**Architecture:** `SubjectServerBase` derives from `SubjectConnectorBase`, seals `RunAsync`, and owns the restart loop: once-per-run `InitializeAsync`, then per attempt `CreateChangeQueueProcessor` (subscribes) before `StartServerAsync` (accepts clients), then `ProcessAsync`, with exponential backoff through an overridable `GetRestartDelay`. Each server moves its protocol work into those hooks and drops its own loop.

**Tech Stack:** C# 14 / .NET 10, xUnit, Microsoft.Extensions.Hosting, OPC Foundation SDK, MQTTnet, ASP.NET Core Kestrel.

**Spec:** `docs/superpowers/specs/2026-10-08-subject-server-base-design.md`

**Repository rules that apply to every task** (from `AGENTS.md`):
- Tests are named `When<Condition>_Then<ExpectedBehavior>` and have `// Arrange`, `// Act`, `// Assert` comments. No `Task.Delay`/`Thread.Sleep` waits; use `AsyncTestHelpers.WaitUntilAsync`.
- Comments state only the why a reader cannot derive. Docs and comments describe current behavior only, never "previously" or "now".
- No em dashes in docs. No hard wrapping in markdown.
- Commit messages: conventional prefix, no AI attribution, no `Co-Authored-By` trailer.
- Warnings are errors; Sonar rules may fire. Do not add exceptions without asking the user.

---

## File map

| File | Change |
|---|---|
| `src/Namotion.Interceptor.Connectors/SubjectServerBase.cs` | Create: the template |
| `src/Namotion.Interceptor.Connectors.Tests/SubjectServerBaseTests.cs` | Create: template tests and test server |
| `src/Namotion.Interceptor.Connectors/SubjectConnectorBase.cs` | Modify: XML docs steer to the templates |
| `src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt` | Accept new snapshot |
| `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectServer.cs` | Migrate |
| `src/Namotion.Interceptor.WebSocket.Tests/VerifyChecksTests.PublicApi.verified.txt` | Accept new snapshot |
| `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServer.cs` | Migrate |
| `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerDiagnostics.cs` | Read the base failure count |
| `src/Namotion.Interceptor.OpcUa.Tests/Server/OpcUaServerDiagnosticsTests.cs` | Adapt to the base counter and retried application build |
| `src/Namotion.Interceptor.OpcUa.Tests/Server/OpcUaServerDeliveryRuleTests.cs` | Call the renamed factory |
| `src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServer.cs` | Migrate |
| `src/Namotion.Interceptor.Mqtt.Tests/MqttServerDeliveryRuleTests.cs` | Call the renamed factory |
| `src/Namotion.Interceptor.Mqtt.Tests/VerifyChecksTests.PublicApi.verified.txt` | Accept new snapshot |
| `docs/connectors.md` | Rewrite the server sections and the `SubjectConnectorBase` section |
| `docs/connectors-opcua-server.md` | Resilience section links to the canonical description |

---

### Task 1: SubjectServerBase with tests

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/SubjectServerBase.cs`
- Create: `src/Namotion.Interceptor.Connectors.Tests/SubjectServerBaseTests.cs`

- [ ] **Step 1: Write the test file**

Create `src/Namotion.Interceptor.Connectors.Tests/SubjectServerBaseTests.cs`:

```csharp
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
            OnStart = (_, _) =>
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
            OnStart = (_, attemptNumber) => attemptNumber < 3
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
            OnStart = (_, _) => throw failure
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
            OnStart = (_, attemptNumber) => attemptNumber < 3
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
            OnStart = async (attempt, attemptNumber) =>
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
            OnStart = (_, _) => throw new InvalidOperationException("start failed"),
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
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
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

        public Func<ConnectorRunAttempt, int, Task>? OnStart { get; init; }

        public Action? OnInitialize { get; init; }

        public Func<int, TimeSpan> RestartDelay { get; init; } = _ => TimeSpan.Zero;

        public ConcurrentQueue<string> Events { get; } = new();

        public ConcurrentQueue<SubjectPropertyChange> WrittenChanges { get; } = new();

        public ConcurrentQueue<int> RequestedDelays { get; } = new();

        public ChangeQueueProcessor? LastProcessor { get; private set; }

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

        protected override async Task<IAsyncDisposable?> StartServerAsync(ConnectorRunAttempt attempt)
        {
            var attemptNumber = Interlocked.Increment(ref _startCount);
            if (OnStart is not null)
            {
                await OnStart(attempt, attemptNumber);
            }

            return null;
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectServerBaseTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'SubjectServerBase' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Namotion.Interceptor.Connectors/SubjectServerBase.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Abstract base for a server, which exposes the local model to external clients. Owns the restart
/// loop and subscribes to property changes before each attempt accepts clients, so every change made
/// after a client's snapshot reaches that client.
/// </summary>
/// <remarks>
/// <see cref="RunAsync"/> is sealed. A derived server implements <see cref="CreateChangeQueueProcessor"/>
/// and <see cref="StartServerAsync"/>, and optionally <see cref="InitializeAsync"/>.
/// </remarks>
public abstract class SubjectServerBase : SubjectConnectorBase
{
    private const double MaximumRestartDelaySeconds = 30;
    private const double MaximumRestartJitterSeconds = 2;

    private readonly ILogger _logger;
    private int _consecutiveFailures;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubjectServerBase"/> class.
    /// </summary>
    /// <param name="metrics">The metrics this server writes to.</param>
    /// <param name="logger">The logger for restart-loop failures.</param>
    protected SubjectServerBase(ConnectorMetrics metrics, ILogger logger)
        : base(metrics)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the number of failed attempts since the last successful start.
    /// </summary>
    protected int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>
    /// Sets up what lives across restarts. Called once per run, before the first attempt. Must not
    /// accept clients. A failure ends the connector and is not retried.
    /// </summary>
    /// <param name="stoppingToken">The connector's stopping token.</param>
    /// <returns>A teardown disposed after the last attempt, or <c>null</c> if there is nothing to release.</returns>
    protected virtual Task<IAsyncDisposable?> InitializeAsync(CancellationToken stoppingToken) =>
        Task.FromResult<IAsyncDisposable?>(null);

    /// <summary>
    /// Creates the processor that publishes this server's outbound changes. Called at the start of each
    /// attempt, before <see cref="StartServerAsync"/>, and disposed when the attempt ends.
    /// </summary>
    /// <param name="dropHandler">The outbound drop reporter, to pass to the processor.</param>
    /// <returns>The processor, already subscribed to property changes.</returns>
    protected abstract ChangeQueueProcessor CreateChangeQueueProcessor(Action<long> dropHandler);

    /// <summary>
    /// Starts the protocol server for one attempt. Called once the change subscription exists, and
    /// returns once clients can connect. If it throws, it releases what it acquired before rethrowing.
    /// </summary>
    /// <param name="attempt">The attempt this start belongs to.</param>
    /// <returns>A teardown disposed when the attempt ends, or <c>null</c> if there is nothing to release.</returns>
    protected abstract Task<IAsyncDisposable?> StartServerAsync(ConnectorRunAttempt attempt);

    /// <summary>
    /// Gets the delay before the next attempt after a failed one. The default grows exponentially from
    /// one second to a 30 second cap and adds up to two seconds of random jitter.
    /// </summary>
    /// <param name="consecutiveFailures">The number of failed attempts in a row, starting at 1.</param>
    /// <returns>The delay; zero restarts immediately.</returns>
    protected virtual TimeSpan GetRestartDelay(int consecutiveFailures)
    {
        var baseDelay = Math.Min(Math.Pow(2, consecutiveFailures - 1), MaximumRestartDelaySeconds);

        // Jitter keeps servers that failed together from restarting together.
        return TimeSpan.FromSeconds(baseDelay + Random.Shared.NextDouble() * MaximumRestartJitterSeconds);
    }

    /// <inheritdoc />
    protected sealed override async Task RunAsync(CancellationToken stoppingToken)
    {
        await using var initialization = await InitializeAsync(stoppingToken).ConfigureAwait(false);

        Interlocked.Exchange(ref _consecutiveFailures, 0);
        while (!stoppingToken.IsCancellationRequested)
        {
            var restartDelay = TimeSpan.Zero;
            await RunAttemptAsync(stoppingToken, async attempt =>
            {
                restartDelay = await RunServerAttemptAsync(attempt, stoppingToken).ConfigureAwait(false);
            }).ConfigureAwait(false);

            // After the attempt's teardown, so the port is free rather than held for the whole delay.
            if (restartDelay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(restartDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task<TimeSpan> RunServerAttemptAsync(ConnectorRunAttempt attempt, CancellationToken stoppingToken)
    {
        try
        {
            using var changeQueueProcessor = CreateChangeQueueProcessor(Metrics.OutboundChanges.CreateDropReporter());

            // Declared after the processor so it is released first, which is what lets the next attempt
            // register its own: a second Register while one is still live throws.
            using var outboundRegistration = Metrics.OutboundChanges.Register(
                () => changeQueueProcessor.QueueDepth, capacity: null);

            var serverTeardown = await StartServerAsync(attempt).ConfigureAwait(false);
            try
            {
                Interlocked.Exchange(ref _consecutiveFailures, 0);

                // LastError is deliberately left in place: clearing it on recovery would erase the only
                // evidence of a transient fault.
                Metrics.MarkOperational();

                await changeQueueProcessor.ProcessAsync(attempt.Token).ConfigureAwait(false);
            }
            finally
            {
                Metrics.MarkNotOperational();
                if (serverTeardown is not null)
                {
                    await serverTeardown.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return TimeSpan.Zero;
        }
        catch (OperationCanceledException) when (attempt.WasForceKilled)
        {
            LogForceKill();
            return TimeSpan.Zero;
        }
        catch (Exception exception)
        {
            // A stop tears a server down with an arbitrary exception rather than a cancellation, so only
            // the stopping token tells a shutdown apart from a genuine fault.
            if (stoppingToken.IsCancellationRequested)
            {
                return TimeSpan.Zero;
            }

            return RecordFailure(exception);
        }

        // ProcessAsync returns rather than throws when its token is cancelled, so the tokens say why it ended.
        if (stoppingToken.IsCancellationRequested)
        {
            return TimeSpan.Zero;
        }

        if (attempt.WasForceKilled)
        {
            LogForceKill();
            return TimeSpan.Zero;
        }

        return RecordFailure(new InvalidOperationException("Server processing completed unexpectedly."));
    }

    private void LogForceKill()
    {
        // Not reported as an error: an injected fault the server recovers from by restarting.
        _logger.LogWarning("Server {Server} force-killed. Restarting...", GetType().Name);
    }

    private TimeSpan RecordFailure(Exception exception)
    {
        var consecutiveFailures = Interlocked.Increment(ref _consecutiveFailures);

        // Nothing outside this loop reports its failures.
        Metrics.ReportError(exception);

        var restartDelay = GetRestartDelay(consecutiveFailures);
        _logger.LogError(exception,
            "Server {Server} failed (attempt {Attempt}). Restarting in {Delay}.",
            GetType().Name, consecutiveFailures, restartDelay);

        return restartDelay;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectServerBaseTests"`
Expected: all 14 test cases PASS (8 facts + 6 theory rows).

If `WhenInitializeAsyncThrows_ThenTheConnectorFaultsWithTheError` fails because `StartAsync` throws synchronously, keep `_ = server.StartAsync(...)` as written: `SubjectConnectorBase.StartAsync` returns the faulted execute task rather than throwing, and the assertion awaits `ExecuteTask`.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/SubjectServerBase.cs src/Namotion.Interceptor.Connectors.Tests/SubjectServerBaseTests.cs
git commit -m "feat: add SubjectServerBase that subscribes before a server accepts clients"
```

---

### Task 2: Steer SubjectConnectorBase authors and accept the Connectors API snapshot

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SubjectConnectorBase.cs:6-12`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] **Step 1: Update the class XML docs**

Replace the summary and remarks above `public abstract class SubjectConnectorBase` with:

```csharp
/// <summary>
/// Abstract base for every connector, client or server, owning the diagnostics lifecycle so that a
/// connector cannot forget to report that it stopped serving.
/// </summary>
/// <remarks>
/// Derive a source from <see cref="SubjectSourceBase"/> and a server from <see cref="SubjectServerBase"/>:
/// both own their restart loop and the ordering between subscribing to changes and exchanging values.
/// Derive from this class directly only for a connector that is neither, and override
/// <see cref="RunAsync"/>; <see cref="ExecuteAsync"/> is sealed.
/// </remarks>
```

- [ ] **Step 2: Run the public API test and accept the snapshot**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~VerifyChecksTests"`
Expected: FAIL, with a `VerifyChecksTests.PublicApi.received.txt` next to the verified file.

Check the diff contains only the new `SubjectServerBase` type:

```bash
diff src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.received.txt
```

Expected: an added block starting `public abstract class SubjectServerBase : Namotion.Interceptor.Connectors.SubjectConnectorBase`. Then accept:

```bash
mv src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.received.txt src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~VerifyChecksTests"
```

Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/SubjectConnectorBase.cs src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt
git commit -m "docs: steer connector authors to the source and server base classes"
```

---

### Task 3: Migrate the WebSocket server

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectServer.cs`
- Modify: `src/Namotion.Interceptor.WebSocket.Tests/VerifyChecksTests.PublicApi.verified.txt`

Existing coverage: `WebSocketServerLivenessTests` (bind failure, stop during backoff) and the WebSocket integration tests exercise this path; no new test is added here because the ordering is covered by Task 1.

- [ ] **Step 1: Change the base class and constructor**

Change the class declaration to:

```csharp
public sealed class WebSocketSubjectServer : SubjectServerBase, IFaultInjectable, IAsyncDisposable
```

Delete the `RestartBackoff` field and its comment (lines 23-24).

Change the constructor's base call from `: base(new ConnectorMetrics())` to:

```csharp
        : base(new ConnectorMetrics(), logger)
```

- [ ] **Step 2: Replace `RunAsync` with the hooks**

Replace the whole `protected override async Task RunAsync(CancellationToken stoppingToken)` method (from its `/// <inheritdoc />` through its closing brace, before `BuildWebApplication`) with:

```csharp
    /// <inheritdoc />
    protected override ChangeQueueProcessor CreateChangeQueueProcessor(Action<long> dropHandler) =>
        _handler.CreateChangeQueueProcessor(_logger, dropHandler);

    /// <inheritdoc />
    protected override async Task<IAsyncDisposable?> StartServerAsync(ConnectorRunAttempt attempt)
    {
        var attemptToken = attempt.Token;
        Task? heartbeatTask = null;

        var teardown = new AttemptTeardown(async () =>
        {
            // The heartbeat runs until the attempt is cancelled.
            await attempt.CancelAsync().ConfigureAwait(false);
            if (heartbeatTask is not null)
            {
                await heartbeatTask.ConfigureAwait(false);
            }

            await _handler.CloseAllConnectionsAsync().ConfigureAwait(false);
            await StopApplicationAsync().ConfigureAwait(false);
        });

        try
        {
            // Built per attempt because IHost does not support Start/Stop cycles, so a kill tears down and
            // rebuilds the whole Kestrel instance, matching real crash behavior.
            var app = BuildWebApplication(attemptToken, out var listenUrl);
            _app = app;

            _logger.LogInformation("WebSocket server starting on {Url}{Path}", listenUrl, _configuration.Path);
            await app.StartAsync(attemptToken).ConfigureAwait(false);

            heartbeatTask = RunHeartbeatAsync(attempt, attemptToken);
            return teardown;
        }
        catch
        {
            await teardown.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunHeartbeatAsync(ConnectorRunAttempt attempt, CancellationToken attemptToken)
    {
        try
        {
            await _handler.RunHeartbeatLoopAsync(attemptToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attemptToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "WebSocket heartbeat loop failed.");
        }

        // A heartbeat that ends while the attempt is live ends processing too, which restarts the attempt.
        // The attempt is still live here: its teardown awaits this task before the attempt is disposed.
        if (!attemptToken.IsCancellationRequested)
        {
            await attempt.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task StopApplicationAsync()
    {
        // Claimed atomically, because DisposeAsync also races for this app after a stop that timed out,
        // and both winning would dispose it twice.
        var app = Interlocked.Exchange(ref _app, null);
        if (app is null)
        {
            return;
        }

        try
        {
            // Use a short timeout to avoid the default 30-second ASP.NET graceful shutdown. Connections are
            // already closed, so Kestrel should stop quickly. The timeout is just a safety net.
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await app.StopAsync(shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown timed out, so DisposeAsync will force-release the port.
            }
        }
        finally
        {
            // In a finally, because a stop that fails must not skip the disposal: the app still holds the
            // listening port and every later bind would fail.
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class AttemptTeardown(Func<Task> teardown) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await teardown().ConfigureAwait(false);
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Namotion.Interceptor.WebSocket`
Expected: Build succeeded, 0 warnings. If `using` directives are missing (the file uses explicit `using System;` etc.), none are needed beyond the existing ones; `ChangeQueueProcessor` and `ConnectorRunAttempt` live in `Namotion.Interceptor.Connectors`, already imported.

- [ ] **Step 4: Run the WebSocket tests**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests`
Expected: all PASS except `VerifyChecksTests.PublicApi`, which fails because the base type changed.

- [ ] **Step 5: Accept the WebSocket API snapshot**

```bash
diff src/Namotion.Interceptor.WebSocket.Tests/VerifyChecksTests.PublicApi.verified.txt src/Namotion.Interceptor.WebSocket.Tests/VerifyChecksTests.PublicApi.received.txt
```

Expected: `WebSocketSubjectServer : Namotion.Interceptor.Connectors.SubjectServerBase` replaces `SubjectConnectorBase`, plus the two protected overrides. Then:

```bash
mv src/Namotion.Interceptor.WebSocket.Tests/VerifyChecksTests.PublicApi.received.txt src/Namotion.Interceptor.WebSocket.Tests/VerifyChecksTests.PublicApi.verified.txt
dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "FullyQualifiedName~VerifyChecksTests"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket src/Namotion.Interceptor.WebSocket.Tests/VerifyChecksTests.PublicApi.verified.txt
git commit -m "refactor: run the WebSocket server on SubjectServerBase"
```

---

### Task 4: Migrate the OPC UA server

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServer.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerDiagnostics.cs:33`
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Server/OpcUaServerDiagnosticsTests.cs`
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Server/OpcUaServerDeliveryRuleTests.cs:31`

- [ ] **Step 1: Adapt the tests first**

In `OpcUaServerDeliveryRuleTests.cs`, change line 31 to:

```csharp
        using var processor = server.CreateOutboundProcessor(dropHandler: null);
```

In `OpcUaServerDiagnosticsTests.cs`:

1. Delete the test `WhenFailuresAreRecordedConcurrently_ThenDiagnosticsReportsEveryFailure` entirely. The counter is now written only by the base loop; `SubjectServerBaseTests` covers counting and reset.

2. Replace the test `WhenTheServerCannotBuildItsApplication_ThenTheFailureReachesItsOwnDiagnostics` (including its `<summary>` comment) with:

```csharp
    /// <summary>
    /// The application instance is built inside each attempt, so a failure there is retried. It is the
    /// cheapest reachable failure that pins the diagnostics to the connector's own metrics.
    /// </summary>
    [Fact]
    public async Task WhenTheServerCannotBuildItsApplication_ThenTheFailureReachesItsOwnDiagnostics()
    {
        // Arrange
        using var server = CreateServer(new FailingOpcUaServerConfiguration());

        try
        {
            // Act
            await server.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.ConsecutiveFailures >= 1,
                message: "A server that cannot build its application should record the failure.");

            // Assert
            Assert.IsType<InvalidOperationException>(server.Diagnostics.LastError);
            Assert.NotNull(server.Diagnostics.StartTime);
            Assert.False(server.Diagnostics.IsOperational);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }
```

Check `FailingOpcUaServerConfiguration` in the same file: it must throw `InvalidOperationException` from `CreateApplicationInstanceAsync`. If it throws a different type, assert that type instead.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaServerDiagnosticsTests|FullyQualifiedName~OpcUaServerDeliveryRuleTests"`
Expected: build FAILS with `CS1061: 'OpcUaSubjectServer' does not contain a definition for 'CreateOutboundProcessor'`.

- [ ] **Step 3: Change the base class, constructor and failure counter**

In `OpcUaSubjectServer.cs`:

Change the declaration to:

```csharp
internal class OpcUaSubjectServer : SubjectServerBase, IOpcUaSubjectServer, IFaultInjectable
```

Delete the field `private int _consecutiveFailures;`.

Replace the three members `ConsecutiveFailures`, `RecordConsecutiveFailure` and `ResetConsecutiveFailures` (with the summary on the first) with:

```csharp
    /// <summary>
    /// Gets the consecutive failure count.
    /// </summary>
    internal int ConsecutiveFailureCount => ConsecutiveFailures;
```

Change the constructor's base call to:

```csharp
        : base(new ConnectorMetrics(new ThroughputCounter(), new ThroughputCounter()), logger)
```

In `OpcUaServerDiagnostics.cs`, change line 33 to:

```csharp
    public int ConsecutiveFailures => _server.ConsecutiveFailureCount;
```

- [ ] **Step 4: Rename the processor factory**

Replace the `internal ChangeQueueProcessor CreateChangeQueueProcessor() => ...` member (keep its summary) with:

```csharp
    /// <inheritdoc />
    protected override ChangeQueueProcessor CreateChangeQueueProcessor(Action<long> dropHandler) =>
        CreateOutboundProcessor(dropHandler);

    /// <summary>
    /// Builds the outbound processor. Extracted so a test can read back the rule it selected: asserting
    /// the constant alone would not catch a different value being inlined at the construction site,
    /// which is the mistake the constant exists to prevent.
    /// </summary>
    internal ChangeQueueProcessor CreateOutboundProcessor(Action<long>? dropHandler) =>
        new(source: this, _context,
            propertyFilter: IsPropertyIncluded, writeHandler: WriteChangesAsync,
            DeliveryRule,
            _configuration.BufferTime, maxQueueDepth: null, logger: _logger,
            dropHandler: dropHandler);
```

- [ ] **Step 5: Replace `RunAsync` and `ExecuteServerLoopAsync` with the hooks**

Replace the methods `RunAsync`, `SubscribeToSubjectDetaching` and `ExecuteServerLoopAsync` (from `/// <inheritdoc />` above `RunAsync` through the closing brace of `ExecuteServerLoopAsync`) with:

```csharp
    /// <inheritdoc />
    protected override Task<IAsyncDisposable?> InitializeAsync(CancellationToken stoppingToken)
    {
        _context.WithRegistry();

        // Null when the context has no lifecycle interceptor, which the base treats as nothing to release.
        return Task.FromResult(SubscribeToSubjectDetaching());
    }

    private IAsyncDisposable? SubscribeToSubjectDetaching()
    {
        if (_context.TryGetLifecycleInterceptor() is not { } lifecycleInterceptor)
        {
            return null;
        }

        lifecycleInterceptor.SubjectDetaching += OnSubjectDetaching;
        return new SubjectDetachingSubscription(lifecycleInterceptor, OnSubjectDetaching);
    }

    /// <inheritdoc />
    protected override async Task<IAsyncDisposable?> StartServerAsync(ConnectorRunAttempt attempt)
    {
        var application = await _configuration.CreateApplicationInstanceAsync().ConfigureAwait(false);

        if (_configuration.CleanCertificateStore)
        {
            CleanCertificateStore(application);
        }

        var server = new OpcUaStandardServer(_subject, this, _configuration, _logger);
        _server = server;

        var teardown = new AttemptTeardown(() => TearDownAttemptAsync(attempt, application, server));
        try
        {
            await application.CheckApplicationInstanceCertificatesAsync(true, ct: attempt.Token).ConfigureAwait(false);
            await application.StartAsync(server).ConfigureAwait(false);
            return teardown;
        }
        catch
        {
            await teardown.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task TearDownAttemptAsync(
        ConnectorRunAttempt attempt, ApplicationInstance application, OpcUaStandardServer server)
    {
        var serverToClean = _server;
        _server = null;
        serverToClean?.ClearPropertyData();

        try
        {
            if (attempt.WasForceKilled && application.Server is OpcUaStandardServer startedServer)
            {
                // Force-kill: close transport listeners immediately so clients see an abrupt connection
                // loss (realistic crash simulation).
                startedServer.CloseTransportListeners();
            }

            // Always run ShutdownServerAsync to ensure the SDK's internal tasks (SubscriptionManager
            // publish/refresh threads) are properly signaled to exit via OnServerStoppingAsync. Without
            // StopAsync, these fire-and-forget tasks keep the entire server object graph alive as GC roots,
            // causing ~8-16 MB leak per server restart. On force-kill the transport is already dead, so
            // this only cleans up internal state and does not change what clients observe.
            await ShutdownServerAsync(application).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to shutdown OPC UA server.");
        }
        finally
        {
            try { server.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error disposing OPC UA server."); }
        }
    }
```

At the end of the class, replace `SubjectDetachingSubscription` with:

```csharp
    private sealed class SubjectDetachingSubscription(
        LifecycleInterceptor lifecycleInterceptor, Action<SubjectLifecycleChange> handler) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lifecycleInterceptor.SubjectDetaching -= handler;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AttemptTeardown(Func<Task> teardown) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await teardown().ConfigureAwait(false);
    }
```

- [ ] **Step 6: Build and run the OPC UA unit tests**

Run: `dotnet build src/Namotion.Interceptor.OpcUa && dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category!=Integration"`
Expected: Build succeeded with 0 warnings; all tests PASS.

- [ ] **Step 7: Run the OPC UA integration tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests`
Expected: all PASS, including `WhenAStartAttemptFails_ThenTheNextAttemptCanRegisterItsOwnChangeQueue` and `OpcUaServerLivenessTests`.

- [ ] **Step 8: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa src/Namotion.Interceptor.OpcUa.Tests
git commit -m "refactor: run the OPC UA server on SubjectServerBase"
```

---

### Task 5: Migrate the MQTT server

**Files:**
- Modify: `src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServer.cs`
- Modify: `src/Namotion.Interceptor.Mqtt.Tests/MqttServerDeliveryRuleTests.cs:32`
- Modify: `src/Namotion.Interceptor.Mqtt.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] **Step 1: Adapt the delivery rule test**

In `MqttServerDeliveryRuleTests.cs`, change line 32 to:

```csharp
        using var processor = server.CreateOutboundProcessor(dropHandler: null);
```

Run: `dotnet test src/Namotion.Interceptor.Mqtt.Tests --filter "FullyQualifiedName~MqttServerDeliveryRuleTests"`
Expected: build FAILS with `CS1061: 'MqttSubjectServer' does not contain a definition for 'CreateOutboundProcessor'`.

- [ ] **Step 2: Change the base class and constructor**

Change the declaration to:

```csharp
public class MqttSubjectServer : SubjectServerBase, IFaultInjectable, IAsyncDisposable
```

Change the constructor's base call to:

```csharp
        : base(new ConnectorMetrics(), logger)
```

Add a field below `_currentClientCounter`:

```csharp
    // Set once per run, so an attempt's teardown can leave the broker running for the run's own cleanup.
    private CancellationToken _stoppingToken;
```

- [ ] **Step 3: Replace `RunAsync` with the hooks**

Replace the whole `protected override async Task RunAsync(CancellationToken stoppingToken)` method with:

```csharp
    /// <inheritdoc />
    protected override Task<IAsyncDisposable?> InitializeAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        var optionsBuilder = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(_configuration.BrokerPort)
            .WithMaxPendingMessagesPerClient(_configuration.MaxPendingMessagesPerClient);

        if (!string.IsNullOrEmpty(_configuration.BrokerHost))
        {
            var boundAddress = System.Net.IPAddress.Parse(_configuration.BrokerHost);
            optionsBuilder.WithDefaultEndpointBoundIPAddress(boundAddress);
        }

        var options = optionsBuilder.Build();
        var server = new MqttServerFactory().CreateMqttServer(options);
        var lifecycleInterceptor = _context.TryGetLifecycleInterceptor();
        var shutdownCts = new CancellationTokenSource();
        var shutdownToken = shutdownCts.Token;
        var initialStateTasks = new List<Task>();
        var clientCounter = new RunClientCounter();
        var publishLease = new ConnectorCommitLease();

        Task ClientConnectedForRunAsync(ClientConnectedEventArgs args) =>
            ClientConnectedAsync(args, server, shutdownToken, initialStateTasks, clientCounter);

        Task ClientDisconnectedForRunAsync(ClientDisconnectedEventArgs args) =>
            ClientDisconnectedAsync(args, clientCounter);

        Task InterceptingPublishForRunAsync(InterceptingPublishEventArgs args) =>
            InterceptingPublishAsync(args, server, shutdownToken, publishLease);

        _mqttServer = server;
        _currentClientCounter = clientCounter;
        lock (_initialStateTasksLock)
        {
            _runningInitialStateTasks = initialStateTasks;
        }

        if (lifecycleInterceptor is not null)
        {
            lifecycleInterceptor.SubjectDetaching += OnSubjectDetaching;
        }

        server.ClientConnectedAsync += ClientConnectedForRunAsync;
        server.ClientDisconnectedAsync += ClientDisconnectedForRunAsync;
        server.InterceptingPublishAsync += InterceptingPublishForRunAsync;

        return Task.FromResult<IAsyncDisposable?>(new AsyncTeardown(() => CleanupRunAsync(
            server,
            lifecycleInterceptor,
            ClientConnectedForRunAsync,
            ClientDisconnectedForRunAsync,
            InterceptingPublishForRunAsync,
            shutdownCts,
            initialStateTasks,
            clientCounter,
            publishLease)));
    }

    /// <inheritdoc />
    protected override ChangeQueueProcessor CreateChangeQueueProcessor(Action<long> dropHandler) =>
        CreateOutboundProcessor(dropHandler);

    /// <inheritdoc />
    protected override async Task<IAsyncDisposable?> StartServerAsync(ConnectorRunAttempt attempt)
    {
        var server = _mqttServer ?? throw new InvalidOperationException("The broker is created by InitializeAsync.");

        try
        {
            await server.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            // MQTTnet latches its started flag before binding, so a start that failed on the bind leaves
            // the broker claiming to be started and every retry would then fail as "already started",
            // hiding the genuine error. Stop it to release the latch.
            if (server.IsStarted)
            {
                try
                {
                    await server.StopAsync().ConfigureAwait(false);
                }
                catch (Exception stopException)
                {
                    _logger.LogWarning(stopException, "Error stopping half-started MQTT server before retry.");
                }
            }

            throw;
        }

        _logger.LogInformation("MQTT server started on port {Port}.", _configuration.BrokerPort);

        return new AsyncTeardown(async () =>
        {
            // On a stop the run's cleanup stops the broker, after the initial-state publishes have drained.
            if (!_stoppingToken.IsCancellationRequested)
            {
                await server.StopAsync().ConfigureAwait(false);
            }
        });
    }

    private sealed class AsyncTeardown(Func<Task> teardown) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await teardown().ConfigureAwait(false);
    }
```

- [ ] **Step 4: Rename the processor factory**

Replace the `internal ChangeQueueProcessor CreateChangeQueueProcessor() => ...` member at the end of the file (keep its summary) with:

```csharp
    /// <summary>
    /// Builds the outbound processor. Extracted so the delivery rule it selects can be pinned by a test:
    /// choosing the wrong one is silent, so "it compiles" is not evidence that it chose correctly.
    /// </summary>
    internal ChangeQueueProcessor CreateOutboundProcessor(Action<long>? dropHandler) =>
        new(source: this,
            _context,
            propertyFilter: IsPropertyIncluded,
            writeHandler: WriteChangesAsync,
            // Safe only because inbound client messages are applied under _mqttClientSource rather than
            // this, so none of them is skipped here as our own echo and every superseding value is
            // relayed on. Applying them under this would break it.
            ChangeDeliveryRule.SourceValuesAreSettled,
            _configuration.BufferTime,
            maxQueueDepth: null,
            logger: _logger,
            dropHandler: dropHandler);
```

- [ ] **Step 5: Build and run the MQTT unit tests**

Run: `dotnet build src/Namotion.Interceptor.Mqtt && dotnet test src/Namotion.Interceptor.Mqtt.Tests --filter "Category!=Integration"`
Expected: Build succeeded with 0 warnings; all tests PASS except `VerifyChecksTests.PublicApi`.

If the build reports `_disposed` as assigned but never read, leave it: `DisposeAsync` still reads it through `Interlocked.Exchange`.

- [ ] **Step 6: Accept the MQTT API snapshot**

```bash
diff src/Namotion.Interceptor.Mqtt.Tests/VerifyChecksTests.PublicApi.verified.txt src/Namotion.Interceptor.Mqtt.Tests/VerifyChecksTests.PublicApi.received.txt
```

Expected: `MqttSubjectServer : Namotion.Interceptor.Connectors.SubjectServerBase`, the protected `RunAsync` override removed, and three protected overrides added. Then:

```bash
mv src/Namotion.Interceptor.Mqtt.Tests/VerifyChecksTests.PublicApi.received.txt src/Namotion.Interceptor.Mqtt.Tests/VerifyChecksTests.PublicApi.verified.txt
```

- [ ] **Step 7: Run the MQTT integration tests**

Run: `dotnet test src/Namotion.Interceptor.Mqtt.Tests`
Expected: all PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Namotion.Interceptor.Mqtt src/Namotion.Interceptor.Mqtt.Tests
git commit -m "fix: subscribe the MQTT server before its broker accepts clients"
```

---

### Task 6: Documentation

**Files:**
- Modify: `docs/connectors.md` (sections `## Servers` through `### Pattern`, and `### SubjectConnectorBase`)
- Modify: `docs/connectors-opcua-server.md` (`## Resilience`)

- [ ] **Step 1: Rewrite the server introduction, Responsibilities and Pattern**

In `docs/connectors.md`, replace the paragraph starting `There is no server-specific base class.` with:

```markdown
A server derives from `SubjectServerBase`, which owns the restart loop and the ordering between subscribing to changes and accepting clients. See [SubjectServerBase](#subjectserverbase) for the hooks it calls and what it guarantees.
```

Replace the `**Publishing property changes**` bullet under `### Responsibilities` with:

```markdown
- **Publishing property changes**: build the `ChangeQueueProcessor` that pushes changes to connected clients in the protocol's wire format. `SubjectServerBase` creates it before the server accepts clients, so a client's snapshot plus the changes delivered after it converge on the current values. Changes a later local commit superseded are collapsed rather than published in sequence, so clients see the settled value instead of every intermediate one
```

Replace the numbered list under `### Pattern` (items 1 to 5) and its lead sentence with:

```markdown
All built-in servers (OPC UA, MQTT, WebSocket) follow the same structure:

1. Extend `SubjectServerBase` and implement `CreateChangeQueueProcessor` and `StartServerAsync`, plus `InitializeAsync` for state that outlives a restart
2. Expose a sealed diagnostics type from the `Diagnostics` override, so callers reach the server's own numbers without a cast
3. Accept incoming client connections and route write requests to the local model via `SetValueFromSource()`
```

- [ ] **Step 2: Add a SubjectServerBase section and rewrite the SubjectConnectorBase section**

Replace the whole `### SubjectConnectorBase` section (from its heading up to, not including, the paragraph that starts `The outbound queue is wired up by reporting drops`) with:

````markdown
### SubjectConnectorBase

`SubjectConnectorBase` is the base every connector shares, client or server. It is a `BackgroundService` that implements `ISubjectConnector` and owns the diagnostics lifecycle, so a connector cannot forget to report that it stopped serving. Derive a source from `SubjectSourceBase` and a server from [`SubjectServerBase`](#subjectserverbase); both seal `RunAsync` and own the ordering between subscribing to changes and exchanging values. Derive from `SubjectConnectorBase` directly only for a connector that is neither.

`ExecuteAsync` is `protected sealed override`. A direct derivation overrides `RunAsync`, which the base wraps:

| Behaviour | Where |
|---|---|
| Stamps the start epoch that every `Total` counter and `StartTime` are measured from, clears `LastError`, returns liveness to `null` so the new run carries nothing from the previous one, and resets the registered metrics | `MarkStarted()`, once per `ExecuteAsync` entry |
| Records a fault that escapes `RunAsync` into `LastError` | the catch around the `RunAsync` call |
| Leaves an expected shutdown unrecorded, so a graceful stop does not overwrite the genuine error that made the connector fail | the `OperationCanceledException` filter on the stopping token |
| Publishes liveness `false` when `RunAsync` exits, on every path, and rejects late reports until the next `MarkStarted()` | `MarkStopped()` in the `finally` |
| Publishes liveness `false` on disposal, because `BackgroundService.Dispose` cancels the token without awaiting `ExecuteAsync` | the `Dispose` override |
| Runs one restart-loop iteration under its own `ConnectorRunAttempt`, publishing it while the body runs and clearing it before disposal, so an injected kill cancels exactly the iteration that is running and one arriving between iterations finds nothing to cancel | `RunAttemptAsync()`, paired with `ForceKillCurrentAttemptAsync()` |

What a direct derivation must implement:

- `RootSubject`, the subject tree this connector is bound to.
- `Diagnostics`, narrowed to the connector's own sealed type via a covariant override, so callers reach the protocol-specific numbers without a cast.
- `RunAsync`, the protocol work. It runs until cancellation, and a cancellation caused by the stopping token must not be turned into a fault: either return, or let the exception leave, which the base recognises and does not record.
- Handing the diagnostics view the same metrics object the base holds, by constructing it from the inherited `Metrics` property rather than from a second instance. A view built over its own `ConnectorMetrics` compiles and then reports nothing.
- Every failure the connector's own loop swallows: the base only sees what escapes `RunAsync`, so a retry loop that catches its own failures has to call `ReportError` itself, and move liveness with `MarkOperational` and `MarkNotOperational` around the serving window. Guard that report on the stopping token: a cancellation filter alone does not cover the arbitrary exception a transport torn down mid-stop raises, and recording that replaces the genuine fault for good, because `LastError` is sticky and a stopped connector does not start again. One class of failure stays out of reach: `ChangeQueueProcessor` logs and swallows everything the write handler raises, on the buffered path, on the immediate path and inside the flush task, so a write that fails on the way out of the connector never reaches `LastError` however the loop is written.

A connector whose transport work runs in a task the loop does not await, such as a client's reconnect monitor, is outside `RunAsync` too, and has to report its own failures for the same reason.

A connector that participates in chaos testing implements [`IFaultInjectable`](../src/Namotion.Interceptor.Connectors/IFaultInjectable.cs) and runs each restart-loop iteration through `RunAttemptAsync`, which gives the iteration its own [`ConnectorRunAttempt`](../src/Namotion.Interceptor.Connectors/ConnectorRunAttempt.cs), so injected-kill cancellation and the flag identifying it have the same lifetime. `InjectFaultAsync` kills through `ForceKillCurrentAttemptAsync`. `SubjectServerBase` runs every attempt this way, so all servers take that route. The [MQTT client](../src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs), [WebSocket client](../src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs) and [Modbus client](../src/Namotion.Interceptor.Modbus/Client/ModbusSubjectClientSource.cs) take it too, although the Connector Tester has no Modbus profile yet. The OPC UA client instead cancels the SDK session by clearing it, or cancels the currently owned manual-reconnection token, because the SDK owns its reconnect loop.

### SubjectServerBase

`SubjectServerBase` derives from `SubjectConnectorBase`, seals `RunAsync`, and runs the server's restart loop. A server implements three hooks:

| Hook | Called | Contract |
|---|---|---|
| `InitializeAsync` (optional) | once per run, before the first attempt | Sets up what lives across restarts. Must not accept clients. Its result is disposed after the last attempt. A failure ends the connector. |
| `CreateChangeQueueProcessor` | at the start of every attempt | Returns the processor that publishes outbound changes, passing it the drop handler it receives. The processor subscribes on construction. |
| `StartServerAsync` | after the processor exists | Starts the protocol server and returns once clients can connect. Disposing its result tears the attempt down. If it throws, it releases what it acquired first. |

Each attempt runs in this order: create the processor and register its depth on `OutboundChanges`, start the server, reset the failure count and mark the server operational, then process changes until the attempt ends. Because the subscription exists before any client connects, a change made between a client's snapshot and the start of processing still reaches that client.

When an attempt ends, the base marks the server not operational, disposes the server's teardown, then releases the registration and the processor. What happens next depends on why it ended:

- A stop ends the loop without recording anything, whatever exception the teardown raised.
- An injected kill restarts immediately without recording an error.
- Any other exception, or processing that ends while neither stopping nor killed, is recorded in `LastError`, increments `ConsecutiveFailures`, and restarts after `GetRestartDelay`.

The default delay grows exponentially with each consecutive failure and adds 0 to 2 seconds of jitter, so servers that failed together do not restart together:

| Failure # | Base Delay | Jitter |
|-----------|-----------|--------|
| 1 | 1s | 0-2s |
| 2 | 2s | 0-2s |
| 3 | 4s | 0-2s |
| 4 | 8s | 0-2s |
| 5 | 16s | 0-2s |
| 6+ | 30s (cap) | 0-2s |

The count resets when an attempt starts successfully. A server overrides `GetRestartDelay` for a different policy. The delay runs after the attempt's teardown, so a port is not held while waiting.

```csharp
// IsPropertyIncluded, PublishAsync and MyListener stand for the server's own protocol code.
public sealed class MySubjectServer : SubjectServerBase
{
    private readonly int _port;
    private readonly ILogger _logger;

    public MySubjectServer(IInterceptorSubject subject, int port, ILogger<MySubjectServer> logger)
        : base(new ConnectorMetrics(), logger)
    {
        RootSubject = subject;
        _port = port;
        _logger = logger;

        // The same instance the base holds, so the read side and the write side agree.
        Diagnostics = new MyServerDiagnostics(this, Metrics);
    }

    public override IInterceptorSubject RootSubject { get; }

    /// <inheritdoc cref="SubjectConnectorBase.Diagnostics" />
    public override MyServerDiagnostics Diagnostics { get; }

    protected override ChangeQueueProcessor CreateChangeQueueProcessor(Action<long> dropHandler) =>
        new(source: this, RootSubject.Context,
            propertyFilter: IsPropertyIncluded, writeHandler: PublishAsync,
            ChangeDeliveryRule.SourceValuesAreSettled,
            bufferTime: null, maxQueueDepth: null, logger: _logger, dropHandler: dropHandler);

    protected override async Task<IAsyncDisposable?> StartServerAsync(ConnectorRunAttempt attempt)
    {
        var listener = await MyListener.StartAsync(_port, attempt.Token);
        return listener; // Disposing it stops accepting clients.
    }
}
````

- [ ] **Step 3: Update the outbound queue paragraph**

In the paragraph and code block that follow (starting `The outbound queue is wired up by reporting drops`), replace the lead sentence and the code block with:

````markdown
`SubjectServerBase` wires the outbound queue into diagnostics for every server. A direct derivation of `SubjectConnectorBase` that publishes through a `ChangeQueueProcessor` wires it up by reporting drops into the lifetime-owned metrics and registering only the processor's depth provider. The registration is released when that processor goes away:

```csharp
using var processor = CreateProcessor(Metrics.OutboundChanges.CreateDropReporter());

// Declared after the processor, so reverse-order disposal releases the registration first.
using var registration = Metrics.OutboundChanges.Register(
    () => processor.QueueDepth, capacity: null);

await processor.ProcessAsync(stoppingToken);
```
````

Keep the rest of that paragraph (from `Register allows one live registration at a time`) unchanged.

- [ ] **Step 4: Point the OPC UA Resilience section at the canonical description**

In `docs/connectors-opcua-server.md`, replace the `## Resilience` section body (the sentence, the table and the reset sentence) with:

```markdown
The server restarts on failure with exponential backoff and jitter, as described in [SubjectServerBase](connectors.md#subjectserverbase). Track the failure count via `Diagnostics.ConsecutiveFailures`, which resets when the server starts successfully.
```

- [ ] **Step 5: Check links and wording**

```bash
grep -n "subjectserverbase\|There is no server-specific\|Only the OPC UA server" docs/*.md
grep -n "—" docs/connectors.md docs/connectors-opcua-server.md
```

Expected: the first command lists only the new links/heading and no stale sentences; the second prints nothing.

- [ ] **Step 6: Commit**

```bash
git add docs/connectors.md docs/connectors-opcua-server.md
git commit -m "docs: describe servers through SubjectServerBase"
```

---

### Task 7: Full verification

- [ ] **Step 1: Build the solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 2: Run all unit tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: all PASS.

- [ ] **Step 3: Run the WebSocket integration tests** (OPC UA and MQTT ran in Tasks 4 and 5)

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests`
Expected: all PASS.

- [ ] **Step 4: Run the Connector Tester chaos profiles**

Read `docs/connector-tester.md` first. Run each profile in the background from the repository root, one at a time unless their ports are known to differ, for at least 100 cycles each (about 100 minutes per profile):

```bash
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile opcua-chaos --configuration Release
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile mqtt-chaos --configuration Release
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile websocket-chaos --configuration Release
```

Expected: each reaches at least 100 passing cycles without exiting with code 1. Stop with Ctrl-C (SIGINT) once the count is reached and record the cycle counts from the run's `logs/` directory. A failing cycle is investigated with superpowers:systematic-debugging before anything else.

- [ ] **Step 5: Remove the working spec and plan**

```bash
git rm docs/superpowers/specs/2026-10-08-subject-server-base-design.md docs/superpowers/plans/2026-10-08-subject-server-base.md
git commit -m "chore: remove the working spec and plan"
```

- [ ] **Step 6: Hand off for the pull request**

Use superpowers:finishing-a-development-branch. The PR description must list these behavior changes (from the spec's PR notes):
- MQTT subscribes before the broker accepts clients.
- MQTT and WebSocket restart with exponential backoff instead of a fixed 5 s.
- A WebSocket processing loop that ends unexpectedly restarts after the backoff.
- An OPC UA application build failure is retried instead of ending the connector.
- Failure log messages come from the base class.
- Connector Tester cycle counts per profile.
