# Connector Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the six connectors' inconsistent, OPC UA-only diagnostics with one shared object model rooted at `ISubjectConnector.Diagnostics`, and start counting the outbound writes that are silently dropped today.

**Architecture:** A mutable `ConnectorMetrics`/`SourceMetrics` object is created and owned by the connector and never reachable through any interface. A read-only `ConnectorDiagnostics`/`SourceDiagnostics` view is built from it and is what consumers see. A new `SubjectConnectorBase : BackgroundService` sits above `SubjectSourceBase` and the three servers, seals `ExecuteAsync`, and drives the metrics lifecycle through a `RunAsync` template method. Per-connector extras (OPC UA sessions, MQTT clients, WebSocket connections) live on sealed leaf diagnostics classes that derive from the shared bases.

**Tech Stack:** C# 13, .NET 9.0 (`Namotion.Interceptor.Connectors` and all connector projects), xUnit, Moq, `PublicApiGenerator` + `Verify` for API snapshots.

**Source spec:** `docs/superpowers/specs/2026-08-11-connector-diagnostics-design.md` (revision 10). Closes #277.

## Global Constraints

- **Nullable enabled, warnings as errors** (`src/Directory.Build.props`). A `volatile` field passed to `Volatile.Read(ref ...)` is CS0420 and therefore a build failure: pick one mechanism per field, never both.
- **`GenerateDocumentationFile` is on and only CS1591 is suppressed**, so an unresolved `<see cref="..."/>` is CS1574 and fails the build. Never write a cref to a type or member a later task introduces. Use plain text for a forward reference and convert it to a cref in the task that creates the target, if it is worth converting at all.
- **No `Total` counter may reset except at `MarkStarted`.** A `Total` prefix means monotonic since `ConnectorDiagnostics.StartTime`. Anything that resets otherwise carries no `Total` (this is why `ConsecutiveFailures` keeps its name).
- **No diagnostics getter may throw.** A throwing getter turns a metrics scrape of one connector into a failed response for all of them.
- **No getter may take a lock owned by this library.** The binding case is the members `SourceMonitor` reads while holding its own lock, which is `State` and `StateChangeTime`: `StateChanged` fires inside the source's transition lock, so a lock-taking getter closes an ABBA cycle. This is documented on `ISubjectSource`. A lock private to a BCL collection, such as the one `ConcurrentQueue<T>.Count` takes on a multi-segment queue, cannot participate in such a cycle and is acceptable; document it where it costs the caller something.
- **Test naming:** `When<Condition>_Then<ExpectedBehavior>`, with explicit `// Arrange`, `// Act`, `// Assert` comments (`// Act & Assert` for exception tests).
- **No hardcoded waits** in tests. Use `AsyncTestHelpers.WaitUntilAsync(() => condition)` or `ManualResetEventSlim`/`CountdownEvent`.
- **Several tests need the wall clock to advance** before asserting that a timestamp moved, because two `DateTimeOffset.UtcNow` reads inside one method can return identical ticks. Spin until it does rather than sleeping a fixed amount, which would be a hardcoded wait: too short on a coarse timer, wasteful otherwise. Add this private helper to each test class that needs it (`Task 2`, `Task 7`, `Task 14`), rather than sharing it across assemblies:

```csharp
    private static void WaitForClockTick()
    {
        var tick = DateTimeOffset.UtcNow.UtcTicks;
        while (DateTimeOffset.UtcNow.UtcTicks == tick)
        {
            Thread.SpinWait(1);
        }
    }
```
- **No em dashes** in any documentation, XML doc comment, or commit message. Restructure into plain sentences.
- **No AI attribution** in commit messages or the PR description. No agent names, no `Co-Authored-By` trailers, no "Generated with" footers.
- **Markdown paragraphs go on one line.** Never hard-wrap at a column.
- **Comments explain only the non-obvious.** Do not narrate what the code already says.
- **`QueueMetrics.Register` rejects an overlapping registration** with `InvalidOperationException`: a live registration must be released with `Deregister` before another is made. Decided during Task 1's review, because real callers register with lambdas that allocate a fresh delegate every call, so the type cannot tell "same buffer, new delegate" from "new buffer" and would double count the former. Every call site in Tasks 8 through 11 already pairs the two in a `try`/`finally` or registers exactly once in a constructor, so nothing needs to change; do not introduce a call site that re-registers without deregistering.
- **Build:** `dotnet build src/Namotion.Interceptor.slnx`
- **Unit tests:** `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
- **Snapshot loop:** prefix snapshot test runs with `DiffEngine_Disabled=true` so no diff tool launches.

## Deviations from the spec, decided here

The spec is revision 10 and its line citations were verified when it was written. Four things changed or were left open. Each is resolved below and the reasoning is repeated in the task that implements it.

**1. `SubjectSourceBase.Diagnostics` is a concrete override, not abstract.** The spec chose abstract on both bases to avoid allocating a dead diagnostics object per inheritance level. That was costed against three derived classes; there are actually **six**: `OpcUaSubjectClientSource`, `MqttSubjectClientSource`, `WebSocketSubjectClientSource`, plus `Connectors.Tests/TestSubjectSource.cs:9`, `Connectors.Tests/SourceStateTests.cs:287` and `Benchmark/SubjectSourceBenchmark.cs:136`. Only the OPC UA client needs a richer type. Abstract would force a declaration plus a constructor assignment into five classes that want the plain `SourceDiagnostics`. Concrete-with-override costs one dead `SourceDiagnostics` per OPC UA client source instance, which is a long-lived singleton holding four references. Correctness and reviewability win over one allocation per connector instance.

**2. Hoisting `PollingMetrics` does not save `CircuitBreakerTrips`.** The spec says the nine renamed counters "live in `PollingMetrics` and the read-after-write metrics object". Eight do. `PollingManager.CircuitBreakerTrips` reads `_circuitBreaker.TripCount` (`PollingManager.cs:85`), and `CircuitBreaker` is constructed inside `PollingManager`'s constructor (`:53`), so it dies with every `SessionManager`. Hoisting the `CircuitBreaker` itself is wrong: it also holds `IsOpen`, which must start closed on a fresh connection. Resolution: `PollingMetrics` gains its own trip counter, incremented where `CircuitBreaker.RecordFailure()` returns true. `IsCircuitBreakerOpen` still reads the live breaker.

**3. `ConnectorMetrics` gains `MarkStopped()`.** The spec requires a terminal rule for `IsOperational` ("`MarkOperational` for the OPC UA client is raised from `SessionManager`, off the pump thread, so without the same rule it can land after the base's `finally`") but its API list has no member that expresses it. `MarkNotOperational()` is an ordinary reversible transition; `MarkStopped()` latches, so a late `MarkOperational()` no-ops. The base calls `MarkStopped()` from the `ExecuteAsync` finally and from `Dispose()`. This mirrors `SourceState.Stopped` being terminal.

**4. The `Mock<ISubjectSource>` fallout is not real.** The spec calls roughly eighty auto-stubbed `Diagnostics` members "the largest single piece of fallout". No production code dereferences `ISubjectSource.Diagnostics`: the member exists for consumers, and every in-repo consumer holds a concrete connector. A mock returning null is therefore harmless, and no shared setup helper is needed. Task 12 verifies this by building and running the suite rather than by pre-emptively editing mocks.

**5. `maxQueueDepth: 0` is rejected at construction.** The spec left this open ("the plan should either forbid the latter at construction or document the collision"). Forbidding it is chosen: a bound of zero drops every change immediately, which no caller can want, and rejecting it makes `QueueDiagnostics.Capacity == 0` mean exactly one thing, "the queue was never constructed". Task 1 covers this.

## Operational predicate per connector, decided here

The spec left this to the plan and flagged it as user-visible. All six preserve the meaning of the member they replace, so `HomeBlaze.OpcUa/OpcUaClient.cs:225` keeps reporting what it reports today.

| Connector | `IsOperational` means | `MarkOperational()` at | `MarkStopped()`/`MarkNotOperational()` at |
|---|---|---|---|
| `OpcUaSubjectClientSource` | session usable and not reconnecting (today's `IsConnected`) | `HandleHealthySessionAsync` success path | `ReportConnectionLost` (`:458`) and `NotifyConnectionLost` |
| `OpcUaSubjectServer` | server started and serving (today's `IsRunning`) | after `_startTime = DateTimeOffset.UtcNow;` | the inner `finally` that nulls `_startTime` |
| `MqttSubjectServer` | broker listening (today's `IsListening`) | `:182` where `_isListening` becomes 1 | `:195`, `:208`, `:652` where it becomes 0 |
| `WebSocketSubjectServer` | Kestrel accepting | after `await _app.StartAsync(...)` (`:97`) | the iteration's `finally` |
| `MqttSubjectClientSource` | connected to broker | `:114` after the connect succeeds | `OnDisconnectedAsync` (`:531`) |
| `WebSocketSubjectClientSource` | socket open and welcomed | `:265` after the welcome frame | where the receive loop exits (`:413`) |

## File Structure

**New files, `src/Namotion.Interceptor.Connectors/Diagnostics/`:**

| File | Responsibility |
|---|---|
| `IResettableMetrics.cs` | One-method contract letting hoisted metrics join the `MarkStarted` reset |
| `QueueMetrics.cs` | Write side of one buffer: registration, drop accumulation, reset |
| `QueueDiagnostics.cs` | Read-only view over one `QueueMetrics` |
| `ThroughputDiagnostics.cs` | Read-only view over up to two `ThroughputCounter`s |
| `ConnectorMetrics.cs` | Write side shared by every connector: liveness, start epoch, last error, outbound change queue |
| `SourceMetrics.cs` | Adds the retry queue, the inbound buffer and the claimed-property gauge |
| `ConnectorDiagnostics.cs` | Read-only view over `ConnectorMetrics` |
| `SourceDiagnostics.cs` | Read-only view over `SourceMetrics` |

**New file, `src/Namotion.Interceptor.Connectors/`:**

| File | Responsibility |
|---|---|
| `SubjectConnectorBase.cs` | `BackgroundService` base owning the metrics lifecycle, sealing `ExecuteAsync`, exposing `RunAsync` |

**Modified, core:** `ISubjectConnector.cs`, `ISubjectSource.cs`, `SubjectSourceBase.cs`, `SubjectPropertyWriter.cs`, `SourceOwnershipManager.cs`, `ChangeQueueProcessor.cs`, `WriteRetryQueue.cs`.

**Modified, connectors:** `OpcUa/Client/OpcUaSubjectClientSource.cs`, `OpcUa/Client/OpcUaClientDiagnostics.cs`, `OpcUa/Client/ReconnectionMetrics.cs`, `OpcUa/Client/SessionManager.cs`, `OpcUa/Client/Polling/PollingManager.cs`, `OpcUa/Client/Polling/PollingMetrics.cs`, `OpcUa/Client/ReadAfterWrite/ReadAfterWriteMetrics.cs`, `OpcUa/Server/OpcUaSubjectServer.cs`, `OpcUa/Server/OpcUaServerDiagnostics.cs`, `Mqtt/Server/MqttSubjectServer.cs`, `Mqtt/Client/MqttSubjectClientSource.cs`, `WebSocket/Server/WebSocketSubjectServer.cs`, `WebSocket/Server/WebSocketSubjectHandler.cs`, `WebSocket/Client/WebSocketSubjectClientSource.cs`.

**New leaf diagnostics:** `Mqtt/Server/MqttServerDiagnostics.cs`, `WebSocket/Server/WebSocketServerDiagnostics.cs`.

---

### Task 1: `QueueMetrics` and `QueueDiagnostics`

The accumulator that has to survive a `ChangeQueueProcessor` being recreated on every connect cycle. Everything else in the plan depends on this type, so it lands first and alone.

The whole state is one immutable record swapped atomically. The spec's earlier design used separate fields with a lock only on deregistration, which cannot be lock-free, monotonic and non-double-counting at once: reading the accumulator then the provider can decrease across a deregistration, and the opposite order double counts.

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/Diagnostics/IResettableMetrics.cs`
- Create: `src/Namotion.Interceptor.Connectors/Diagnostics/QueueMetrics.cs`
- Create: `src/Namotion.Interceptor.Connectors/Diagnostics/QueueDiagnostics.cs`
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs` (reject `maxQueueDepth <= 0`, expose depth)
- Test: `src/Namotion.Interceptor.Connectors.Tests/Diagnostics/QueueMetricsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `IResettableMetrics.Reset()`; `QueueMetrics` with `Register(Func<int> depth, Func<long>? dropped, int? capacity)`, `Deregister()`, `AddDropped(long count)`, internal `Depth`/`Capacity`/`TotalDropped`/`Reset()`; `QueueDiagnostics(QueueMetrics metrics)` with `int Depth`, `int? Capacity`, `long TotalDropped`; `ChangeQueueProcessor.QueueDepth` (public `int`).

- [ ] **Step 1: Write the failing tests**

Create `src/Namotion.Interceptor.Connectors.Tests/Diagnostics/QueueMetricsTests.cs`:

```csharp
using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Connectors.Tests.Diagnostics;

public class QueueMetricsTests
{
    [Fact]
    public void WhenNothingIsRegistered_ThenDepthIsZeroAndCapacityIsNull()
    {
        // Arrange
        var metrics = new QueueMetrics();

        // Act
        var diagnostics = new QueueDiagnostics(metrics);

        // Assert
        Assert.Equal(0, diagnostics.Depth);
        Assert.Null(diagnostics.Capacity);
        Assert.Equal(0, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenProviderIsRegistered_ThenDepthAndCapacityComeFromIt()
    {
        // Arrange
        var metrics = new QueueMetrics();
        var depth = 7;
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        metrics.Register(() => depth, dropped: null, capacity: 100);

        // Assert
        Assert.Equal(7, diagnostics.Depth);
        Assert.Equal(100, diagnostics.Capacity);
    }

    [Fact]
    public void WhenProviderIsDeregistered_ThenDepthReturnsToZeroButCapacityStays()
    {
        // Arrange
        var metrics = new QueueMetrics();
        metrics.Register(() => 7, dropped: null, capacity: 100);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        metrics.Deregister();

        // Assert
        Assert.Equal(0, diagnostics.Depth);
        Assert.Equal(100, diagnostics.Capacity);
    }

    [Fact]
    public void WhenLiveProviderReportsDrops_ThenTotalAdvancesDuringTheBurst()
    {
        // Arrange
        var metrics = new QueueMetrics();
        var live = 0L;
        metrics.Register(() => 0, () => live, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        live = 5;

        // Assert
        Assert.Equal(5, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenProviderIsHandedOver_ThenTotalNeitherDecreasesNorDoubleCounts()
    {
        // Arrange
        var metrics = new QueueMetrics();
        var first = 5L;
        metrics.Register(() => 0, () => first, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);
        Assert.Equal(5, diagnostics.TotalDropped);

        // Act
        metrics.Deregister();
        var afterDeregister = diagnostics.TotalDropped;

        var second = 0L;
        metrics.Register(() => 0, () => second, capacity: 10);
        var afterReregister = diagnostics.TotalDropped;
        second = 3;

        // Assert
        Assert.Equal(5, afterDeregister);
        Assert.Equal(5, afterReregister);
        Assert.Equal(8, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenAddDroppedRacesWithDeregister_ThenNoIncrementIsLost()
    {
        // Arrange
        var metrics = new QueueMetrics();
        var diagnostics = new QueueDiagnostics(metrics);
        const int iterations = 10_000;

        // Act
        var adder = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                metrics.AddDropped(1);
            }
        });

        var churner = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                metrics.Register(() => 0, dropped: null, capacity: 10);
                metrics.Deregister();
            }
        });

        Task.WaitAll(adder, churner);

        // Assert
        Assert.Equal(iterations, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenTotalIsReadRepeatedlyDuringChurn_ThenItNeverDecreases()
    {
        // Arrange
        var metrics = new QueueMetrics();
        var diagnostics = new QueueDiagnostics(metrics);
        var stop = false;
        var observed = 0L;
        var decreased = false;

        // Act
        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var current = diagnostics.TotalDropped;
                if (current < Interlocked.Read(ref observed))
                {
                    decreased = true;
                }

                Interlocked.Exchange(ref observed, current);
            }
        });

        for (var i = 0; i < 500; i++)
        {
            var live = 0L;
            metrics.Register(() => 0, () => live, capacity: 10);
            live = 4;
            metrics.Deregister();
        }

        Volatile.Write(ref stop, true);
        reader.Wait();

        // Assert
        Assert.False(decreased);
        Assert.Equal(2000, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenReset_ThenTotalDroppedReturnsToZeroAndRegistrationSurvives()
    {
        // Arrange
        var metrics = new QueueMetrics();
        metrics.AddDropped(9);
        metrics.Register(() => 4, dropped: null, capacity: 10);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        metrics.Reset();

        // Assert
        Assert.Equal(0, diagnostics.TotalDropped);
        Assert.Equal(4, diagnostics.Depth);
        Assert.Equal(10, diagnostics.Capacity);
    }
}
```

Create `src/Namotion.Interceptor.Connectors.Tests/Diagnostics/ChangeQueueProcessorCapacityTests.cs`:

```csharp
namespace Namotion.Interceptor.Connectors.Tests.Diagnostics;

public class ChangeQueueProcessorCapacityTests
{
    [Fact]
    public void WhenMaxQueueDepthIsZero_ThenConstructionThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new Person(context);
        var source = new TestSubjectSource(subject);
        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new ChangeQueueProcessor(
            source,
            subscription,
            _ => true,
            (_, _) => ValueTask.CompletedTask,
            ChangeDeliveryRule.SourceValuesMayBeStale,
            TimeSpan.FromMilliseconds(8),
            maxQueueDepth: 0,
            logger: NullLogger.Instance));

        Assert.Equal("maxQueueDepth", exception.ParamName);
    }
}
```

Match the `using` directives and the subject type to the existing `ChangeQueueProcessorTests.cs` in the same project rather than inventing new ones.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~QueueMetricsTests|FullyQualifiedName~ChangeQueueProcessorCapacityTests"`

Expected: compile failure, `QueueMetrics` and `QueueDiagnostics` do not exist.

- [ ] **Step 3: Write `IResettableMetrics`**

Create `src/Namotion.Interceptor.Connectors/Diagnostics/IResettableMetrics.cs`:

```csharp
namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// Implemented by metrics objects that a connector owns outside its <c>ConnectorMetrics</c> and that
/// must still take part in the counter reset performed by <c>ConnectorMetrics.MarkStarted</c>.
/// </summary>
/// <remarks>
/// Metrics are hoisted out of short-lived components so their totals survive a reconnect. That is
/// what makes the counters honest, and it is also why they cannot be reached by resetting
/// <c>ConnectorMetrics</c> alone. Register them with <c>ConnectorMetrics.RegisterResettable</c> so
/// the epoch stays consistent across every <c>Total</c> counter a connector reports.
/// </remarks>
public interface IResettableMetrics
{
    /// <summary>
    /// Resets every cumulative counter to zero. Gauges and last-event timestamps are left alone.
    /// </summary>
    void Reset();
}
```

- [ ] **Step 4: Write `QueueMetrics`**

Create `src/Namotion.Interceptor.Connectors/Diagnostics/QueueMetrics.cs`:

```csharp
namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// Write side of one buffer's diagnostics. Owned by the connector for its whole lifetime, while the
/// buffer it describes may be created and destroyed many times.
/// </summary>
/// <remarks>
/// All state lives in a single immutable snapshot swapped with <see cref="Interlocked"/>, so a
/// reader sees the accumulated count and the live provider that belongs with it. Holding them in
/// separate fields cannot be lock-free, monotonic and free of double counting at the same time:
/// reading the accumulator before the provider lets the total decrease across a handover, and
/// reading them the other way round counts the same drops twice.
/// </remarks>
public sealed class QueueMetrics
{
    private sealed record Snapshot(long Accumulated, Func<int>? Depth, Func<long>? Dropped, int? Capacity);

    private Snapshot _snapshot = new(0, null, null, null);

    /// <summary>
    /// Points this instance at a newly created buffer.
    /// </summary>
    /// <param name="depth">Reads the buffer's current item count.</param>
    /// <param name="dropped">
    /// Reads the buffer's own drop counter, or <c>null</c> for a buffer that has none and reports
    /// through <see cref="AddDropped"/> instead. Passing <c>() =&gt; 0</c> instead of <c>null</c>
    /// would work but invites a later implementer to add a counter that then double counts.
    /// </param>
    /// <param name="capacity">The buffer's bound, or <c>null</c> if it is unbounded.</param>
    public void Register(Func<int> depth, Func<long>? dropped, int? capacity)
    {
        ArgumentNullException.ThrowIfNull(depth);

        Swap(current => current with { Depth = depth, Dropped = dropped, Capacity = capacity });
    }

    /// <summary>
    /// Folds the live drop count into the accumulator and clears the providers. Must run before the
    /// buffer is disposed.
    /// </summary>
    /// <remarks>
    /// Clearing the providers first narrows the race with a concurrent reader rather than closing
    /// it: a reader can hold a non-null provider and be preempted. That is safe only because
    /// <see cref="ChangeQueueProcessor"/> keeps its queue and drop count alive through
    /// <see cref="ChangeQueueProcessor.Dispose"/>.
    /// </remarks>
    public void Deregister()
    {
        Swap(current => new Snapshot(
            current.Accumulated + (current.Dropped?.Invoke() ?? 0),
            Depth: null,
            Dropped: null,
            current.Capacity));
    }

    /// <summary>
    /// Records drops for a buffer that has no counter of its own.
    /// </summary>
    public void AddDropped(long count)
    {
        if (count <= 0)
        {
            return;
        }

        Swap(current => current with { Accumulated = current.Accumulated + count });
    }

    internal void Reset() => Swap(current => current with { Accumulated = -(current.Dropped?.Invoke() ?? 0) });

    internal int Depth => _snapshot.Depth?.Invoke() ?? 0;

    internal int? Capacity => _snapshot.Capacity;

    internal long TotalDropped
    {
        get
        {
            var snapshot = _snapshot;
            return snapshot.Accumulated + (snapshot.Dropped?.Invoke() ?? 0);
        }
    }

    private void Swap(Func<Snapshot, Snapshot> update)
    {
        // Compare-exchange rather than a blind exchange: every caller here is a read-modify-write and
        // drops arrive off the pump thread, so an exchange would lose increments.
        SpinWait spin = default;
        while (true)
        {
            var current = Volatile.Read(ref _snapshot);
            if (Interlocked.CompareExchange(ref _snapshot, update(current), current) == current)
            {
                return;
            }

            spin.SpinOnce();
        }
    }
}
```

`Reset` sets the accumulator to the negation of the live count so that `TotalDropped` reads zero immediately afterwards and still advances by one for each subsequent drop from the same live provider.

- [ ] **Step 5: Write `QueueDiagnostics`**

Create `src/Namotion.Interceptor.Connectors/Diagnostics/QueueDiagnostics.cs`:

```csharp
namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// Read-only view over one buffer. All reads are lock-free and none throws.
/// </summary>
public sealed class QueueDiagnostics
{
    private readonly QueueMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueDiagnostics"/> class.
    /// </summary>
    public QueueDiagnostics(QueueMetrics metrics)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    /// <summary>
    /// Gets the buffer's current item count, or 0 when no buffer currently exists.
    /// Approximate: it is read without a lock while producers and consumers are running.
    /// </summary>
    /// <remarks>
    /// Lock-free is not the same as cheap. The change queue's count is a segment walk over a
    /// <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/>, so this should be sampled,
    /// not polled tightly.
    /// </remarks>
    public int Depth => _metrics.Depth;

    /// <summary>
    /// Gets the buffer's bound: <c>null</c> when it is unbounded, 0 when the buffer is disabled and
    /// was never constructed.
    /// </summary>
    public int? Capacity => _metrics.Capacity;

    /// <summary>
    /// Gets the number of items this buffer has thrown away since the connector's
    /// <c>ConnectorDiagnostics.StartTime</c>. Monotonic within an epoch and never rebased by the
    /// buffer being recreated.
    /// </summary>
    public long TotalDropped => _metrics.TotalDropped;
}
```

- [ ] **Step 6: Reject a zero bound and expose the depth on `ChangeQueueProcessor`**

In `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs`, add to both constructors, next to the existing argument validation:

```csharp
        if (maxQueueDepth is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueueDepth),
                "A bound of zero or less would drop every change immediately. Pass null for an unbounded queue.");
        }
```

Add the public depth accessor beside the existing `DropCount` property (`:40`):

```csharp
    /// <summary>
    /// Gets the number of changes currently buffered. Approximate: read without a lock while the
    /// pump is running. Always 0 when the processor is on its immediate path (no buffer time).
    /// </summary>
    public int QueueDepth => _changes.Count;
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~QueueMetricsTests|FullyQualifiedName~ChangeQueueProcessorCapacityTests"`

Expected: PASS, 9 tests.

- [ ] **Step 8: Run the full unit suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`

Expected: PASS. If `ChangeQueueProcessorTests` has a case constructing with `maxQueueDepth: 0`, change it to a positive bound; the two existing `Assert.Throws<ArgumentOutOfRangeException>` cases at `:709` and `:736` cover other parameters and should be unaffected.

- [ ] **Step 9: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Diagnostics src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs src/Namotion.Interceptor.Connectors.Tests/Diagnostics
git commit -m "Add QueueMetrics accumulator surviving processor recreation (#277)"
```

---

### Task 2: `ThroughputDiagnostics`, `ConnectorMetrics` and `SourceMetrics`

The write side. Every mutable piece of connector diagnostics lives here, on an object the connector holds and never exposes through any interface. Revision 6 of the spec put these mutators on the diagnostics view itself; that is not shippable, because the view is reachable from `ISubjectConnector` and any consumer could then flip another connector's liveness or inject a fake error.

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/Diagnostics/ThroughputDiagnostics.cs`
- Create: `src/Namotion.Interceptor.Connectors/Diagnostics/ConnectorMetrics.cs`
- Create: `src/Namotion.Interceptor.Connectors/Diagnostics/SourceMetrics.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/Diagnostics/ConnectorMetricsTests.cs`

**Interfaces:**
- Consumes: `QueueMetrics`, `IResettableMetrics` from Task 1.
- Produces: `ThroughputDiagnostics(ThroughputCounter?, ThroughputCounter?)` with `NotInstrumented`, `IncomingPerSecond`, `OutgoingPerSecond`; `ConnectorMetrics(ThroughputCounter? incoming = null, ThroughputCounter? outgoing = null)` with public `Incoming`, `Outgoing`, `OutboundChanges`, `MarkStarted()`, `RegisterResettable(IResettableMetrics)`, `MarkOperational()`, `MarkNotOperational()`, `MarkStopped()`, `ReportError(Exception)` and internal `IsOperational`, `OperationalChangeTime`, `LastError`, `StartTime`; `SourceMetrics` adding `OutboundRetries`, `InboundBuffer`, `RegisterClaimedProperties(Func<int>)` and internal `ClaimedPropertyCount`.

- [ ] **Step 1: Write the failing tests**

Create `src/Namotion.Interceptor.Connectors.Tests/Diagnostics/ConnectorMetricsTests.cs`:

```csharp
using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Connectors.Tests.Diagnostics;

public class ConnectorMetricsTests
{
    [Fact]
    public void WhenNeverStarted_ThenNotOperationalAndNoTimestamps()
    {
        // Arrange
        var metrics = new ConnectorMetrics();

        // Act
        var diagnostics = new ConnectorDiagnostics(metrics);

        // Assert
        Assert.False(diagnostics.IsOperational);
        Assert.Null(diagnostics.OperationalChangeTime);
        Assert.Null(diagnostics.StartTime);
        Assert.Null(diagnostics.LastError);
    }

    [Fact]
    public void WhenMarkedOperational_ThenFlagAndTimestampMoveTogether()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkStarted();

        // Act
        metrics.MarkOperational();

        // Assert
        Assert.True(diagnostics.IsOperational);
        Assert.NotNull(diagnostics.OperationalChangeTime);
    }

    [Fact]
    public void WhenMarkedOperationalTwice_ThenTheTimestampDoesNotMove()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkOperational();
        var first = diagnostics.OperationalChangeTime;

        // Act
        metrics.MarkOperational();

        // Assert
        Assert.Equal(first, diagnostics.OperationalChangeTime);
    }

    [Fact]
    public void WhenStopped_ThenLaterMarkOperationalIsIgnored()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        metrics.MarkOperational();

        // Act
        metrics.MarkStopped();
        metrics.MarkOperational();

        // Assert
        Assert.False(diagnostics.IsOperational);
    }

    [Fact]
    public void WhenStoppedWithoutEverBeingOperational_ThenNoTransitionTimestampIsInvented()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);

        // Act
        metrics.MarkStopped();

        // Assert
        Assert.False(diagnostics.IsOperational);
        Assert.Null(diagnostics.OperationalChangeTime);
    }

    [Fact]
    public void WhenErrorIsReported_ThenItIsStickyAcrossRecovery()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        var error = new InvalidOperationException("boom");

        // Act
        metrics.ReportError(error);
        metrics.MarkOperational();

        // Assert
        Assert.Same(error, diagnostics.LastError);
    }

    [Fact]
    public void WhenRestarted_ThenStartTimeMovesAndEveryTotalResets()
    {
        // Arrange
        var metrics = new SourceMetrics();
        var diagnostics = new SourceDiagnostics(metrics);
        var hoisted = new CountingResettable();
        metrics.RegisterResettable(hoisted);

        metrics.MarkStarted();
        var firstStart = diagnostics.StartTime;
        metrics.OutboundChanges.AddDropped(3);
        metrics.OutboundRetries.AddDropped(4);
        metrics.InboundBuffer.AddDropped(5);

        // Act
        WaitForClockTick();
        metrics.MarkStarted();

        // Assert
        Assert.NotEqual(firstStart, diagnostics.StartTime);
        Assert.Equal(0, diagnostics.OutboundChanges.TotalDropped);
        Assert.Equal(0, diagnostics.OutboundRetries.TotalDropped);
        Assert.Equal(0, diagnostics.InboundBuffer.TotalDropped);
        Assert.Equal(1, hoisted.ResetCount);
    }

    [Fact]
    public void WhenNoThroughputCountersArePassed_ThenBothRatesAreNull()
    {
        // Arrange
        var metrics = new ConnectorMetrics();

        // Act
        var diagnostics = new ConnectorDiagnostics(metrics);

        // Assert
        Assert.Null(diagnostics.Throughput.IncomingPerSecond);
        Assert.Null(diagnostics.Throughput.OutgoingPerSecond);
    }

    [Fact]
    public void WhenOnlyIncomingIsInstrumented_ThenOutgoingStaysNullAndIncomingReportsZeroWhenIdle()
    {
        // Arrange
        var metrics = new ConnectorMetrics(incoming: new ThroughputCounter());

        // Act
        var diagnostics = new ConnectorDiagnostics(metrics);

        // Assert
        Assert.Equal(0.0, diagnostics.Throughput.IncomingPerSecond);
        Assert.Null(diagnostics.Throughput.OutgoingPerSecond);
    }

    [Fact]
    public void WhenNoClaimedPropertyProviderIsRegistered_ThenCountIsZero()
    {
        // Arrange
        var metrics = new SourceMetrics();

        // Act
        var diagnostics = new SourceDiagnostics(metrics);

        // Assert
        Assert.Equal(0, diagnostics.ClaimedPropertyCount);
    }

    [Fact]
    public void WhenClaimedPropertyProviderIsRegistered_ThenCountFollowsIt()
    {
        // Arrange
        var metrics = new SourceMetrics();
        var count = 0;
        metrics.RegisterClaimedProperties(() => count);
        var diagnostics = new SourceDiagnostics(metrics);

        // Act
        count = 42;

        // Assert
        Assert.Equal(42, diagnostics.ClaimedPropertyCount);
    }

    [Fact]
    public void WhenLivenessIsFlippedConcurrently_ThenTheFlagAndTimestampAreNeverObservedTorn()
    {
        // Arrange
        var metrics = new ConnectorMetrics();
        var diagnostics = new ConnectorDiagnostics(metrics);
        var stop = false;
        var torn = false;
        metrics.MarkOperational();
        var beforeAll = DateTimeOffset.UtcNow.AddSeconds(-1);

        // Act
        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var operational = diagnostics.IsOperational;
                var changeTime = diagnostics.OperationalChangeTime;

                // Both members come from one snapshot, so a true flag can never carry a null or
                // pre-test timestamp.
                if (operational && (changeTime is null || changeTime < beforeAll))
                {
                    torn = true;
                }
            }
        });

        for (var i = 0; i < 20_000; i++)
        {
            metrics.MarkNotOperational();
            metrics.MarkOperational();
        }

        Volatile.Write(ref stop, true);
        reader.Wait();

        // Assert
        Assert.False(torn);
    }

    private sealed class CountingResettable : IResettableMetrics
    {
        public int ResetCount { get; private set; }

        public void Reset() => ResetCount++;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~ConnectorMetricsTests"`

Expected: compile failure, `ConnectorMetrics` does not exist.

- [ ] **Step 3: Write `ThroughputDiagnostics`**

Create `src/Namotion.Interceptor.Connectors/Diagnostics/ThroughputDiagnostics.cs`:

```csharp
namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// Read-only view over a connector's change throughput, averaged over the last 60 seconds.
/// </summary>
/// <remarks>
/// Direction is stated once, from the subject tree's point of view, and means the same thing for
/// clients and servers: incoming is changes flowing into the subject tree, outgoing is changes
/// flowing out of it. For a client source, incoming is what the external system pushed; for a
/// server, incoming is what a connected client wrote.
/// <para>
/// A <c>null</c> rate means the connector does not measure that direction, which is decided at
/// construction and never changes. It is distinct from a rate of <c>0.0</c>, which means the
/// connector measures the direction and nothing is flowing.
/// </para>
/// </remarks>
public sealed class ThroughputDiagnostics
{
    private readonly ThroughputCounter? _incoming;
    private readonly ThroughputCounter? _outgoing;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThroughputDiagnostics"/> class.
    /// </summary>
    public ThroughputDiagnostics(ThroughputCounter? incoming, ThroughputCounter? outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    /// <summary>
    /// Gets a view that measures neither direction.
    /// </summary>
    public static ThroughputDiagnostics NotInstrumented { get; } = new(null, null);

    /// <summary>
    /// Gets the average changes per second flowing into the subject tree, or <c>null</c> if this
    /// connector does not measure it.
    /// </summary>
    public double? IncomingPerSecond => _incoming?.CurrentRate;

    /// <summary>
    /// Gets the average changes per second flowing out of the subject tree, or <c>null</c> if this
    /// connector does not measure it.
    /// </summary>
    public double? OutgoingPerSecond => _outgoing?.CurrentRate;
}
```

- [ ] **Step 4: Write `ConnectorMetrics`**

Create `src/Namotion.Interceptor.Connectors/Diagnostics/ConnectorMetrics.cs`:

```csharp
using System.Collections.Immutable;

namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// Write side of the diagnostics every connector reports. Created and owned by the connector and
/// never reachable through <see cref="ISubjectConnector"/>, so only the connector itself can move
/// its liveness or record its errors.
/// </summary>
public class ConnectorMetrics
{
    private sealed record Liveness(bool IsOperational, long ChangeTicks, bool IsStopped);

    private Liveness _liveness = new(false, 0, false);
    private long _startTicks;
    private Exception? _lastError;
    private ImmutableArray<IResettableMetrics> _resettables = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectorMetrics"/> class.
    /// </summary>
    /// <param name="incoming">Counts changes flowing into the subject tree, or <c>null</c> if this connector does not measure that.</param>
    /// <param name="outgoing">Counts changes flowing out of the subject tree, or <c>null</c> if this connector does not measure that.</param>
    public ConnectorMetrics(ThroughputCounter? incoming = null, ThroughputCounter? outgoing = null)
    {
        Incoming = incoming;
        Outgoing = outgoing;
    }

    /// <summary>
    /// Gets the incoming throughput counter, for the connector to feed. <c>null</c> when this
    /// connector does not measure the direction.
    /// </summary>
    public ThroughputCounter? Incoming { get; }

    /// <summary>
    /// Gets the outgoing throughput counter, for the connector to feed. <c>null</c> when this
    /// connector does not measure the direction.
    /// </summary>
    public ThroughputCounter? Outgoing { get; }

    /// <summary>
    /// Gets the metrics of the outbound change queue that carries subject changes to the external system.
    /// </summary>
    public QueueMetrics OutboundChanges { get; } = new();

    /// <summary>
    /// Opens a new counter epoch: stamps a fresh start time and resets every <c>Total</c> counter,
    /// including those of registered hoisted metrics.
    /// </summary>
    /// <remarks>
    /// Deliberately not idempotent. Called once per <c>ExecuteAsync</c> entry, so a host stop and
    /// start moves the epoch while a transport reconnect inside the connector's own loop does not.
    /// </remarks>
    public void MarkStarted()
    {
        Interlocked.Exchange(ref _startTicks, DateTimeOffset.UtcNow.UtcTicks);
        ResetTotals();

        foreach (var resettable in _resettables)
        {
            resettable.Reset();
        }
    }

    /// <summary>
    /// Enrolls metrics the connector owns outside this object into the <see cref="MarkStarted"/> reset.
    /// </summary>
    public void RegisterResettable(IResettableMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        ImmutableInterlocked.Update(ref _resettables, static (current, item) => current.Add(item), metrics);
    }

    /// <summary>
    /// Reports that the connector is now serving. Ignored once <see cref="MarkStopped"/> has run.
    /// </summary>
    public void MarkOperational() => SetOperational(true, terminal: false);

    /// <summary>
    /// Reports that the connector is no longer serving but may recover.
    /// </summary>
    public void MarkNotOperational() => SetOperational(false, terminal: false);

    /// <summary>
    /// Reports that the connector has stopped for good and latches that.
    /// </summary>
    /// <remarks>
    /// Liveness transitions are raised from wherever a connector detects them, which for the OPC UA
    /// client is off the pump thread. Without a terminal rule such a transition can land after the
    /// pump's own exit and resurrect a stopped connector. Mirrors
    /// <see cref="Monitoring.SourceState.Stopped"/>, which is terminal for the same reason.
    /// </remarks>
    public void MarkStopped() => SetOperational(false, terminal: true);

    /// <summary>
    /// Records the most recent failure. Sticky: it survives recovery, because a cleared error erases
    /// the only evidence a transient fault ever happened.
    /// </summary>
    public void ReportError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        Volatile.Write(ref _lastError, error);
    }

    internal bool IsOperational => Volatile.Read(ref _liveness).IsOperational;

    internal DateTimeOffset? OperationalChangeTime
    {
        get
        {
            var ticks = Volatile.Read(ref _liveness).ChangeTicks;
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    internal Exception? LastError => Volatile.Read(ref _lastError);

    internal DateTimeOffset? StartTime
    {
        get
        {
            var ticks = Interlocked.Read(ref _startTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    private protected virtual void ResetTotals() => OutboundChanges.Reset();

    private void SetOperational(bool isOperational, bool terminal)
    {
        var ticks = DateTimeOffset.UtcNow.UtcTicks;

        SpinWait spin = default;
        while (true)
        {
            var current = Volatile.Read(ref _liveness);
            if (current.IsStopped && !terminal)
            {
                return;
            }

            var stopped = current.IsStopped || terminal;
            if (current.IsOperational == isOperational && current.IsStopped == stopped)
            {
                return;
            }

            // The flag and its timestamp are swapped as one value. Held in separate fields, a reader
            // can see the new flag beside the previous timestamp and report "operational since the
            // moment it went down". The timestamp moves only when the flag does, so latching the
            // terminal bit on a connector that was never operational does not invent a transition.
            var updated = current.IsOperational == isOperational
                ? current with { IsStopped = stopped }
                : new Liveness(isOperational, ticks, stopped);

            if (Interlocked.CompareExchange(ref _liveness, updated, current) == current)
            {
                return;
            }

            spin.SpinOnce();
        }
    }
}
```

- [ ] **Step 5: Write `SourceMetrics`**

Create `src/Namotion.Interceptor.Connectors/Diagnostics/SourceMetrics.cs`:

```csharp
namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// Write side of the diagnostics a source reports on top of <see cref="ConnectorMetrics"/>.
/// </summary>
public class SourceMetrics : ConnectorMetrics
{
    private Func<int>? _claimedPropertyCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceMetrics"/> class.
    /// </summary>
    public SourceMetrics(ThroughputCounter? incoming = null, ThroughputCounter? outgoing = null)
        : base(incoming, outgoing)
    {
    }

    /// <summary>
    /// Gets the metrics of the queue holding outbound writes awaiting retry.
    /// </summary>
    public QueueMetrics OutboundRetries { get; } = new();

    /// <summary>
    /// Gets the metrics of the buffer holding inbound updates while the initial state loads.
    /// </summary>
    public QueueMetrics InboundBuffer { get; } = new();

    /// <summary>
    /// Points the claimed-property gauge at the source's ownership manager. A source that registers
    /// nothing reports 0.
    /// </summary>
    public void RegisterClaimedProperties(Func<int> count)
    {
        ArgumentNullException.ThrowIfNull(count);

        Volatile.Write(ref _claimedPropertyCount, count);
    }

    internal int ClaimedPropertyCount => Volatile.Read(ref _claimedPropertyCount)?.Invoke() ?? 0;

    private protected override void ResetTotals()
    {
        base.ResetTotals();
        OutboundRetries.Reset();
        InboundBuffer.Reset();
    }
}
```

- [ ] **Step 6: Run the tests to verify they fail on the view types only**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~ConnectorMetricsTests"`

Expected: compile failure, `ConnectorDiagnostics` and `SourceDiagnostics` do not exist yet. They land in Task 3, which is the pair this task's tests are written against. Do not commit here; continue straight into Task 3 and commit the two together.

---

### Task 3: `ConnectorDiagnostics` and `SourceDiagnostics`

The read side, and the only diagnostics surface a consumer ever holds.

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/Diagnostics/ConnectorDiagnostics.cs`
- Create: `src/Namotion.Interceptor.Connectors/Diagnostics/SourceDiagnostics.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/Diagnostics/ConnectorMetricsTests.cs` (from Task 2)

**Interfaces:**
- Consumes: `ConnectorMetrics`, `SourceMetrics`, `QueueDiagnostics`, `ThroughputDiagnostics`.
- Produces: `ConnectorDiagnostics(ConnectorMetrics)` with `IsOperational`, `OperationalChangeTime`, `LastError`, `StartTime`, `Throughput`, `OutboundChanges`; `SourceDiagnostics(SourceMetrics)` adding `ClaimedPropertyCount`, `OutboundRetries`, `InboundBuffer`. Both are unsealed so connectors can add protocol-specific members.

- [ ] **Step 1: Write `ConnectorDiagnostics`**

Create `src/Namotion.Interceptor.Connectors/Diagnostics/ConnectorDiagnostics.cs`:

```csharp
namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// What a connector reports about the transport it drives. Read-only, lock-free, and no getter throws.
/// </summary>
/// <remarks>
/// This answers "what is the transport doing". Whether the model can be trusted is a separate
/// question answered by <see cref="ISubjectSource.State"/>, which describes the inbound direction
/// only. Read them together to tell a network outage from a connected source still loading: the
/// first is <see cref="IsOperational"/> false, the second is <see cref="IsOperational"/> true with a
/// state of <see cref="Monitoring.SourceState.Synchronizing"/>. See docs/connectors-monitoring.md.
/// </remarks>
public class ConnectorDiagnostics
{
    private readonly ConnectorMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectorDiagnostics"/> class.
    /// </summary>
    public ConnectorDiagnostics(ConnectorMetrics metrics)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

        Throughput = new ThroughputDiagnostics(metrics.Incoming, metrics.Outgoing);
        OutboundChanges = new QueueDiagnostics(metrics.OutboundChanges);
    }

    /// <summary>
    /// Gets a value indicating whether the transport is up and serving. What that means is defined
    /// by each connector and documented on its own diagnostics type. It does not mean the model is
    /// in sync: see the remarks on this type.
    /// </summary>
    public bool IsOperational => _metrics.IsOperational;

    /// <summary>
    /// Gets when <see cref="IsOperational"/> last changed, or <c>null</c> if the connector has never
    /// started. Moves whenever the flag moves, so the pair reads as "up since T" or "down since T".
    /// </summary>
    public DateTimeOffset? OperationalChangeTime => _metrics.OperationalChangeTime;

    /// <summary>
    /// Gets the most recent error in either direction, or <c>null</c> if there has been none.
    /// Sticky: it survives recovery and is only cleared by a restart.
    /// </summary>
    public Exception? LastError => _metrics.LastError;

    /// <summary>
    /// Gets when the connector's current run began, or <c>null</c> if it has never started. This is
    /// the epoch every <c>Total</c> counter below is measured from. It does not move when the
    /// transport reconnects, only when the connector itself is stopped and started.
    /// </summary>
    public DateTimeOffset? StartTime => _metrics.StartTime;

    /// <summary>
    /// Gets the change rates in each direction.
    /// </summary>
    public ThroughputDiagnostics Throughput { get; }

    /// <summary>
    /// Gets the outbound change queue: subject changes waiting to be written to the external system.
    /// A growing depth means changes are produced faster than they can be flushed.
    /// </summary>
    public QueueDiagnostics OutboundChanges { get; }
}
```

- [ ] **Step 2: Write `SourceDiagnostics`**

Create `src/Namotion.Interceptor.Connectors/Diagnostics/SourceDiagnostics.cs`:

```csharp
namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// What a source reports on top of <see cref="ConnectorDiagnostics"/>.
/// </summary>
public class SourceDiagnostics : ConnectorDiagnostics
{
    private readonly SourceMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceDiagnostics"/> class.
    /// </summary>
    public SourceDiagnostics(SourceMetrics metrics)
        : base(metrics)
    {
        _metrics = metrics;

        OutboundRetries = new QueueDiagnostics(metrics.OutboundRetries);
        InboundBuffer = new QueueDiagnostics(metrics.InboundBuffer);
    }

    /// <summary>
    /// Gets how many properties this source currently owns. A gauge, not a counter: it rises as the
    /// source claims properties and falls as subjects detach. The individual claims and releases are
    /// on the source monitoring event stream.
    /// </summary>
    public int ClaimedPropertyCount => _metrics.ClaimedPropertyCount;

    /// <summary>
    /// Gets the queue of outbound writes awaiting retry. A growing depth means the external system
    /// is rejecting writes.
    /// </summary>
    /// <remarks>
    /// When the source is configured without a retry queue this block reports a capacity of 0 and a
    /// depth of 0, while <see cref="QueueDiagnostics.TotalDropped"/> still rises: without a queue,
    /// failed writes are discarded directly and are attributed here.
    /// </remarks>
    public QueueDiagnostics OutboundRetries { get; }

    /// <summary>
    /// Gets the buffer of inbound updates held while the initial state loads. A growing depth means
    /// an initial load is still in progress.
    /// </summary>
    /// <remarks>
    /// <see cref="QueueDiagnostics.TotalDropped"/> here counts buffered updates thrown away when a
    /// connect attempt was abandoned before its load completed. Those discards are deliberate rather
    /// than data loss, because applying a superseded snapshot would be wrong. The number is useful
    /// as the only signal of how often initial loads are being superseded, which is reconnect thrash.
    /// </remarks>
    public QueueDiagnostics InboundBuffer { get; }
}
```

- [ ] **Step 3: Run the Task 2 tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~ConnectorMetricsTests"`

Expected: PASS, 12 tests.

- [ ] **Step 4: Run the full unit suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Diagnostics src/Namotion.Interceptor.Connectors.Tests/Diagnostics
git commit -m "Add connector metrics and read-only diagnostics views (#277)"
```

---

### Task 4: `SubjectConnectorBase`

The base that makes the metrics lifecycle impossible to get wrong. It must exist before any connector can be migrated onto it.

The three servers each own their own `ExecuteAsync` loop with its own `finally` today, so a faulting server would keep reporting operational. Sealing `ExecuteAsync` and handing down `RunAsync` is what closes that.

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/SubjectConnectorBase.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/SubjectConnectorBaseTests.cs`

**Interfaces:**
- Consumes: `ConnectorMetrics`, `ConnectorDiagnostics` from Tasks 2 and 3.
- Produces: `abstract class SubjectConnectorBase : BackgroundService, ISubjectConnector` with `protected SubjectConnectorBase(ConnectorMetrics metrics)`, `protected ConnectorMetrics Metrics`, `public abstract IInterceptorSubject RootSubject`, `public abstract ConnectorDiagnostics Diagnostics`, `protected abstract Task RunAsync(CancellationToken)`, sealed `ExecuteAsync`, `public override void Dispose()`.

Note: this task does **not** add `Diagnostics` to `ISubjectConnector`. That lands in Task 12, once every implementer has one. `SubjectConnectorBase` declaring the member early is what makes Task 12 a small change.

- [ ] **Step 1: Write the failing tests**

Create `src/Namotion.Interceptor.Connectors.Tests/SubjectConnectorBaseTests.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectConnectorBaseTests
{
    [Fact]
    public async Task WhenStarted_ThenStartTimeIsStamped()
    {
        // Arrange
        using var connector = new TestConnector();

        // Act
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.StartTime is not null);

        // Assert
        Assert.NotNull(connector.Diagnostics.StartTime);
    }

    [Fact]
    public async Task WhenRunAsyncFaults_ThenTheErrorIsRecordedAndTheConnectorIsNotOperational()
    {
        // Arrange
        var error = new InvalidOperationException("run failed");
        using var connector = new TestConnector { Fault = error };

        // Act
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.LastError is not null);

        // Assert
        Assert.Same(error, connector.Diagnostics.LastError);
        Assert.False(connector.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenStoppedAfterBeingOperational_ThenItReportsNotOperational()
    {
        // Arrange
        using var connector = new TestConnector();
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        connector.MarkOperational();
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.IsOperational);

        // Act
        connector.Release();
        await ((IHostedService)connector).StopAsync(CancellationToken.None);

        // Assert
        Assert.False(connector.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenDisposedWithoutStopping_ThenItReportsNotOperational()
    {
        // Arrange
        var connector = new TestConnector();
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        connector.MarkOperational();
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.IsOperational);

        // Act
        connector.Dispose();

        // Assert
        Assert.False(connector.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenReEntered_ThenTheEpochMovesAndTotalsReset()
    {
        // Arrange
        using var connector = new TestConnector();
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.StartTime is not null);
        var firstStart = connector.Diagnostics.StartTime;
        connector.AddOutboundDrop(5);
        connector.Release();
        await ((IHostedService)connector).StopAsync(CancellationToken.None);

        // Act
        connector.Reopen();
        await ((IHostedService)connector).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => connector.Diagnostics.StartTime != firstStart);

        // Assert
        Assert.NotEqual(firstStart, connector.Diagnostics.StartTime);
        Assert.Equal(0, connector.Diagnostics.OutboundChanges.TotalDropped);
    }

    private sealed class TestConnector : SubjectConnectorBase
    {
        private readonly ConnectorMetrics _metrics;
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TestConnector()
            : this(new ConnectorMetrics())
        {
        }

        private TestConnector(ConnectorMetrics metrics)
            : base(metrics)
        {
            _metrics = metrics;
            Diagnostics = new ConnectorDiagnostics(metrics);
        }

        public Exception? Fault { get; init; }

        public override IInterceptorSubject RootSubject => throw new NotSupportedException();

        public override ConnectorDiagnostics Diagnostics { get; }

        public void MarkOperational() => _metrics.MarkOperational();

        public void AddOutboundDrop(long count) => _metrics.OutboundChanges.AddDropped(count);

        public void Release() => _gate.TrySetResult();

        public void Reopen() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task RunAsync(CancellationToken stoppingToken)
        {
            if (Fault is not null)
            {
                throw Fault;
            }

            await using (stoppingToken.Register(() => _gate.TrySetResult()))
            {
                await _gate.Task;
            }
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectConnectorBaseTests"`

Expected: compile failure, `SubjectConnectorBase` does not exist.

- [ ] **Step 3: Write `SubjectConnectorBase`**

Create `src/Namotion.Interceptor.Connectors/SubjectConnectorBase.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Abstract base for every connector, client or server, owning the diagnostics lifecycle so that a
/// connector cannot forget to report that it stopped serving.
/// </summary>
/// <remarks>
/// <see cref="ExecuteAsync"/> is sealed and derived classes override <see cref="RunAsync"/> instead.
/// Without that, each connector's own loop would have to force liveness false on fault, on exit and
/// on disposal, and a connector whose loop faulted would keep reporting that it was serving.
/// </remarks>
public abstract class SubjectConnectorBase : BackgroundService, ISubjectConnector
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubjectConnectorBase"/> class.
    /// </summary>
    /// <param name="metrics">
    /// The metrics this connector writes to. Created by the caller and passed in rather than created
    /// here, so a derived class can supply a richer type and still hand the same instance to its own
    /// diagnostics view.
    /// </param>
    protected SubjectConnectorBase(ConnectorMetrics metrics)
    {
        Metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    /// <summary>
    /// Gets the write side of this connector's diagnostics. Never exposed through
    /// <see cref="ISubjectConnector"/>.
    /// </summary>
    protected ConnectorMetrics Metrics { get; }

    /// <inheritdoc cref="ISubjectConnector.RootSubject" />
    public abstract IInterceptorSubject RootSubject { get; }

    /// <summary>
    /// Gets what this connector reports about its transport.
    /// </summary>
    public abstract ConnectorDiagnostics Diagnostics { get; }

    /// <summary>
    /// Runs the connector until cancellation. Replaces <see cref="ExecuteAsync"/>, which this class
    /// seals so that the diagnostics lifecycle is applied uniformly.
    /// </summary>
    protected abstract Task RunAsync(CancellationToken stoppingToken);

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Metrics.MarkStarted();
        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Metrics.ReportError(exception);
            throw;
        }
        finally
        {
            Metrics.MarkStopped();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        // BackgroundService.Dispose cancels the token but does not await ExecuteAsync, so the finally
        // above runs at an unspecified later time. Without this, a disposed connector keeps reporting
        // that it is serving.
        Metrics.MarkStopped();
        base.Dispose();
    }
}
```

`MarkStopped` latches, so the epoch reset in `MarkStarted` must also clear it. Add to `ConnectorMetrics.MarkStarted`, before `ResetTotals()`:

```csharp
        Interlocked.Exchange(ref _liveness, new Liveness(false, 0, false));
```

and extend the Task 2 test `WhenRestarted_ThenStartTimeMovesAndEveryTotalResets` with:

```csharp
        Assert.False(diagnostics.IsOperational);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectConnectorBaseTests|FullyQualifiedName~ConnectorMetricsTests"`

Expected: PASS, 17 tests.

- [ ] **Step 5: Run the full unit suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/SubjectConnectorBase.cs src/Namotion.Interceptor.Connectors/Diagnostics/ConnectorMetrics.cs src/Namotion.Interceptor.Connectors.Tests
git commit -m "Add SubjectConnectorBase owning the connector diagnostics lifecycle (#277)"
```

---

### Task 5: Lock-free counters on the ownership manager and the property writer

Two gauges that later tasks register. Both are maintained under locks that already exist, so neither getter takes one.

`ClaimedPropertyCount` must not be read through `SourceOwnershipManager.Properties`: that getter does `_properties.ToArray()` under the lock (`:59-66`), so a `.Count` on it allocates an array the size of the claim set on every scrape, which for an OPC UA client is thousands of entries.

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SourceOwnershipManager.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SubjectPropertyWriter.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/SourceOwnershipManagerCountTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `SourceOwnershipManager.Count` (public `int`); `SubjectPropertyWriter.BufferedUpdateCount` (public `int`).

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.Connectors.Tests/SourceOwnershipManagerCountTests.cs`. Model the arrange block on the existing ownership tests in this project so the context configuration matches (`WithFullPropertyTracking().WithRegistry().WithLifecycle()`).

```csharp
namespace Namotion.Interceptor.Connectors.Tests;

public class SourceOwnershipManagerCountTests
{
    [Fact]
    public void WhenPropertiesAreClaimed_ThenCountRises()
    {
        // Arrange
        var (subject, source) = CreateSubjectAndSource();
        using var ownership = new SourceOwnershipManager(source);

        // Act
        ownership.ClaimSource(subject.GetProperty("FirstName"));
        ownership.ClaimSource(subject.GetProperty("LastName"));

        // Assert
        Assert.Equal(2, ownership.Count);
    }

    [Fact]
    public void WhenAPropertyIsReleased_ThenCountFalls()
    {
        // Arrange
        var (subject, source) = CreateSubjectAndSource();
        using var ownership = new SourceOwnershipManager(source);
        var property = subject.GetProperty("FirstName");
        ownership.ClaimSource(property);

        // Act
        ownership.ReleaseSource(property);

        // Assert
        Assert.Equal(0, ownership.Count);
    }

    [Fact]
    public void WhenASubjectDetaches_ThenCountFallsByItsProperties()
    {
        // Arrange
        var (parent, source) = CreateSubjectAndSource();
        using var ownership = new SourceOwnershipManager(source);
        var child = AttachChild(parent);
        ownership.ClaimSource(parent.GetProperty("FirstName"));
        ownership.ClaimSource(child.GetProperty("FirstName"));
        Assert.Equal(2, ownership.Count);

        // Act
        DetachChild(parent);

        // Assert
        Assert.Equal(1, ownership.Count);
    }

    [Fact]
    public void WhenDisposed_ThenCountIsZero()
    {
        // Arrange
        var (subject, source) = CreateSubjectAndSource();
        var ownership = new SourceOwnershipManager(source);
        ownership.ClaimSource(subject.GetProperty("FirstName"));

        // Act
        ownership.Dispose();

        // Assert
        Assert.Equal(0, ownership.Count);
    }
}
```

Each of the four mutation sites gets its own assertion, because a missed site drifts permanently rather than transiently.

Add to the existing `SubjectPropertyWriterTests` (or create it if absent):

```csharp
    [Fact]
    public void WhenBuffering_ThenBufferedUpdateCountTracksTheBuffer()
    {
        // Arrange
        var writer = CreateWriter(out _);
        writer.StartBuffering();

        // Act
        writer.Write(0, static _ => { });
        writer.Write(1, static _ => { });

        // Assert
        Assert.Equal(2, writer.BufferedUpdateCount);
    }

    [Fact]
    public async Task WhenTheBufferIsReplayed_ThenBufferedUpdateCountReturnsToZero()
    {
        // Arrange
        var writer = CreateWriter(out _);
        writer.StartBuffering();
        writer.Write(0, static _ => { });

        // Act
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, writer.BufferedUpdateCount);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceOwnershipManagerCountTests|FullyQualifiedName~SubjectPropertyWriterTests"`

Expected: compile failure, `Count` and `BufferedUpdateCount` do not exist.

- [ ] **Step 3: Add `Count` to `SourceOwnershipManager`**

In `src/Namotion.Interceptor.Connectors/SourceOwnershipManager.cs`, add the field next to `_disposed` (`:25`):

```csharp
    private int _count;
```

Add the getter below the `Properties` property (`:67`):

```csharp
    /// <summary>
    /// Gets how many properties this source currently owns.
    /// </summary>
    /// <remarks>
    /// Maintained rather than derived: <see cref="Properties"/> copies the whole set under the lock,
    /// so counting through it would allocate an array the size of the claim set on every read, and
    /// this is read by a metrics scrape. Recomputed at each mutation site, all of which already hold
    /// the lock, so the value is exact at each write and needs no increment arithmetic.
    /// </remarks>
    public int Count => Volatile.Read(ref _count);
```

Add `Volatile.Write(ref _count, _properties.Count);` as the last statement inside the lock at all four mutation sites: after `_properties.Add(property);` in `ClaimSource` (`:86`), after the `if (_properties.Remove(property))` block in `ReleaseSource` (`:104`), after the `foreach` in `OnSubjectDetaching` (`:122`), and after `_properties.Clear();` in `Dispose` (`:143`).

Do not declare `_count` as `volatile`: passing a `volatile` field to `Volatile.Read`/`Volatile.Write` by `ref` is CS0420, which this repository turns into a build error.

- [ ] **Step 4: Add `BufferedUpdateCount` to `SubjectPropertyWriter`**

In `src/Namotion.Interceptor.Connectors/SubjectPropertyWriter.cs`, add the field next to `_generation` (`:28`):

```csharp
    private int _bufferedUpdateCount;
```

Add the getter after the constructor (`:44`):

```csharp
    /// <summary>
    /// Gets how many inbound updates are currently buffered while the initial state loads.
    /// </summary>
    /// <remarks>
    /// Maintained under the writer's own lock, which every mutation of the buffer already holds, and
    /// read without taking it. A lock-taking getter would close an ABBA cycle:
    /// <see cref="StartBuffering"/> holds this lock while transitioning the source's state, which
    /// reaches registered monitors synchronously.
    /// </remarks>
    public int BufferedUpdateCount => Volatile.Read(ref _bufferedUpdateCount);
```

Set it to 0 in `StartBuffering`, inside the existing lock, right after `_updates = [];`:

```csharp
            Volatile.Write(ref _bufferedUpdateCount, 0);
```

Set it to 0 in `LoadInitialStateAndResumeAsync`, inside the existing lock, right after `_updates = null;`:

```csharp
                Volatile.Write(ref _bufferedUpdateCount, 0);
```

Increment it in `Write`, inside the existing lock, right after the `AddBeforeInitializationUpdate(...)` call and before its `return`:

```csharp
                    Volatile.Write(ref _bufferedUpdateCount, updates.Count);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceOwnershipManagerCountTests|FullyQualifiedName~SubjectPropertyWriterTests"`

Expected: PASS.

- [ ] **Step 6: Run the full unit suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/SourceOwnershipManager.cs src/Namotion.Interceptor.Connectors/SubjectPropertyWriter.cs src/Namotion.Interceptor.Connectors.Tests
git commit -m "Track claimed-property and buffered-update counts without allocating (#277)"
```

---

### Task 6: Move `SubjectSourceBase` onto `SubjectConnectorBase`

Structural only. No behaviour changes beyond the connector now recording errors from its retry loop, which is the one place a source's failures are otherwise swallowed entirely.

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceDiagnosticsTests.cs`

**Interfaces:**
- Consumes: `SubjectConnectorBase`, `SourceMetrics`, `SourceDiagnostics`.
- Produces: `SubjectSourceBase : SubjectConnectorBase, ISubjectSource` with `protected new SourceMetrics Metrics`, `public override SourceDiagnostics Diagnostics`, and its pump body moved from `ExecuteAsync` to `RunAsync`. The constructor gains two optional trailing parameters, `ThroughputCounter? incomingThroughput = null, ThroughputCounter? outgoingThroughput = null`.

`Diagnostics` here is a concrete override, not abstract. Five of the six classes deriving from `SubjectSourceBase` want exactly the plain `SourceDiagnostics`: `MqttSubjectClientSource`, `WebSocketSubjectClientSource`, `Connectors.Tests/TestSubjectSource.cs:9`, `Connectors.Tests/SourceStateTests.cs:287` and `Benchmark/SubjectSourceBenchmark.cs:136`. Only `OpcUaSubjectClientSource` refines it. Making the member abstract would force a declaration and a constructor assignment into all five for no gain; the cost of the concrete default is one dead `SourceDiagnostics` per OPC UA client source instance, which is a long-lived singleton holding four references.

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceDiagnosticsTests.cs`:

```csharp
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectSourceDiagnosticsTests
{
    [Fact]
    public void WhenNeverStarted_ThenTheSourceExposesAnEmptyDiagnostics()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        var subject = new Person(context);

        // Act
        using var source = new TestSubjectSource(subject);

        // Assert
        Assert.NotNull(source.Diagnostics);
        Assert.Null(source.Diagnostics.StartTime);
        Assert.False(source.Diagnostics.IsOperational);
        Assert.Equal(0, source.Diagnostics.ClaimedPropertyCount);
    }

    [Fact]
    public async Task WhenAConnectAttemptFails_ThenTheErrorIsRecordedWithoutTheSourceStopping()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        var subject = new Person(context);
        using var source = new TestSubjectSource(subject)
        {
            StartListeningFailure = new InvalidOperationException("cannot connect")
        };

        // Act
        await ((IHostedService)source).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.LastError is not null);

        // Assert
        Assert.IsType<InvalidOperationException>(source.Diagnostics.LastError);
        Assert.Equal(SourceState.Synchronizing, source.State);
    }
}
```

Add a settable `StartListeningFailure` to `src/Namotion.Interceptor.Connectors.Tests/TestSubjectSource.cs` and throw it from that class's `StartListeningAsync` override when it is not null. Keep the retry time long enough that the test observes one failure, by passing `retryTime: TimeSpan.FromMinutes(1)` to the base constructor from the test's arrange block if the existing fake does not already allow it.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectSourceDiagnosticsTests"`

Expected: compile failure, `SubjectSourceBase` has no `Diagnostics`.

- [ ] **Step 3: Change the class declaration and constructor**

In `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`, add `using Namotion.Interceptor.Connectors.Diagnostics;` and change the declaration at `:18`:

```csharp
public abstract class SubjectSourceBase : SubjectConnectorBase, ISubjectSource
```

Replace the constructor (`:47-67`) with a public entry point and a private constructor that threads the metrics, which is the only way to hand the same instance to both `base(...)` and the derived fields:

```csharp
    protected SubjectSourceBase(
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime = null,
        TimeSpan? retryTime = null,
        int writeRetryQueueSize = 1000,
        ThroughputCounter? incomingThroughput = null,
        ThroughputCounter? outgoingThroughput = null)
        : this(context, logger, bufferTime, retryTime, writeRetryQueueSize,
            new SourceMetrics(incomingThroughput, outgoingThroughput))
    {
    }

    private SubjectSourceBase(
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime,
        TimeSpan? retryTime,
        int writeRetryQueueSize,
        SourceMetrics metrics)
        : base(metrics)
    {
        Metrics = metrics;
        Diagnostics = new SourceDiagnostics(metrics);

        _context = context;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _retryTime = retryTime ?? TimeSpan.FromSeconds(10);

        // The retry queue also carries writes captured while (re)connecting. With size 0 it is
        // disabled, and those connect/reconnect-window writes are dropped rather than reconciled.
        if (writeRetryQueueSize > 0)
        {
            WriteRetryQueue = new WriteRetryQueue(writeRetryQueueSize, logger);
        }

        _propertyWriter = new SubjectPropertyWriter(this, logger);
    }

    /// <summary>
    /// Gets the write side of this source's diagnostics, narrowed to <see cref="SourceMetrics"/>.
    /// </summary>
    protected new SourceMetrics Metrics { get; }

    /// <inheritdoc cref="ISubjectSource.Diagnostics" />
    public override SourceDiagnostics Diagnostics { get; }
```

Remove the now-duplicated `RootSubject` declaration if the compiler reports it as hiding the base member: `SubjectConnectorBase` already declares `public abstract IInterceptorSubject RootSubject { get; }`, so delete the copy at `:70` and its doc comment.

- [ ] **Step 4: Rename the pump and record retry-loop errors**

Change the signature at `:163` from

```csharp
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
```

to

```csharp
    protected override async Task RunAsync(CancellationToken stoppingToken)
```

and delete its `/// <inheritdoc />`, replacing it with:

```csharp
    /// <inheritdoc />
    /// <remarks>
    /// The base class stamps the start epoch, records a fault and forces liveness false around this.
    /// The per-attempt failures the retry loop below swallows never reach it, so they are reported
    /// explicitly.
    /// </remarks>
```

In the retry loop's catch-all (`:249-255`), add the report as the first statement:

```csharp
                catch (Exception ex)
                {
                    // The base class only sees exceptions that leave RunAsync, and this loop swallows
                    // every per-attempt failure. Without this, a source that can never connect would
                    // report no error at all.
                    Metrics.ReportError(ex);

                    // Whatever it reported before the failure, the source is no longer serving the model.
                    TransitionStateTo(SourceState.Synchronizing);
                    _logger.LogError(ex, "Failed to listen for changes in source.");
                    // The next iteration delays before reconnecting, with the subscription still capturing.
                }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests`

Expected: PASS. `SubjectSourceBase.Dispose` already calls `base.Dispose()` at the end, which now reaches `SubjectConnectorBase.Dispose` and forces liveness false, so no change is needed there.

- [ ] **Step 6: Run the full unit suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`

Expected: PASS. The three client sources and three test doubles that derive from `SubjectSourceBase` need no edits: they inherit `Diagnostics` and their `ExecuteAsync` is not overridden.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs src/Namotion.Interceptor.Connectors.Tests
git commit -m "Move SubjectSourceBase onto SubjectConnectorBase (#277)"
```

---

### Task 7: `StateChangeTime` replaces `LastSynchronizedAt`, and `PendingWriteCount` moves

`_lastSynchronizedTicks` is stamped in exactly one place, the transition **into** `Synchronized` (`:589-592`). It therefore records when the last good period began and cannot say when synchronization was lost: a source that synchronized a week ago and dropped an hour ago reports a week, which is the opposite of what an operator needs during an incident.

This is a trade, not a pure win. After it, nothing reports when a currently stale source was last in sync.

`PendingWriteCount` is the one misfiled monitoring member: it gates no behaviour, and `connectors-monitoring.md:160` already documents it as orthogonal to `State`. It becomes `Diagnostics.OutboundRetries.Depth`.

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ISubjectSource.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/SourceStateTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceRetryQueueTests.cs:74`

**Interfaces:**
- Consumes: Task 6's `Diagnostics` on `SubjectSourceBase`.
- Produces: `ISubjectSource.StateChangeTime` (non-nullable `DateTimeOffset`); `ISubjectSource.LastSynchronizedAt` and `ISubjectSource.PendingWriteCount` removed; `SubjectSourceBase.PendingWriteCount` removed.

`StateChangeTime` is non-nullable because a source has a state from construction (`SourceState.Synchronizing`), so there is never a moment when the pair is meaningless. `ConnectorDiagnostics.OperationalChangeTime` stays nullable for the opposite reason: liveness does not exist until the connector runs.

- [ ] **Step 1: Write the failing tests**

In `src/Namotion.Interceptor.Connectors.Tests/SourceStateTests.cs`, replace the `LastSynchronizedAt` tests at `:71-84`, `:96`, `:105` and the 60-line concurrency test at `:109-168` with:

```csharp
    [Fact]
    public void WhenNeverTransitioned_ThenStateChangeTimeIsStampedAtConstruction()
    {
        // Arrange
        var before = DateTimeOffset.UtcNow;

        // Act
        var source = new TestStateSource();

        // Assert
        Assert.Equal(SourceState.Synchronizing, source.State);
        Assert.InRange(source.StateChangeTime, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void WhenSynchronizationIsLost_ThenStateChangeTimeMovesToTheLoss()
    {
        // Arrange
        var source = new TestStateSource();
        source.TransitionStateTo(SourceState.Synchronized);
        var synchronizedAt = source.StateChangeTime;

        // Act
        WaitForClockTick();
        source.TransitionStateTo(SourceState.Synchronizing);

        // Assert
        Assert.Equal(SourceState.Synchronizing, source.State);
        Assert.True(source.StateChangeTime > synchronizedAt);
    }

    [Fact]
    public void WhenTheTransitionIsANoOp_ThenStateChangeTimeDoesNotMove()
    {
        // Arrange
        var source = new TestStateSource();
        source.TransitionStateTo(SourceState.Synchronized);
        var first = source.StateChangeTime;

        // Act
        WaitForClockTick();
        source.TransitionStateTo(SourceState.Synchronized);

        // Assert
        Assert.Equal(first, source.StateChangeTime);
    }

    [Fact]
    public void WhenTransitioningConcurrently_ThenStateAndItsTimestampAreNeverObservedTorn()
    {
        // Arrange
        var source = new TestStateSource();
        var stop = false;
        var torn = false;
        var synchronizedAt = DateTimeOffset.MinValue;

        // Act
        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var state = source.State;
                var changeTime = source.StateChangeTime;

                // Both come from one snapshot, so a Synchronized reading can never carry a timestamp
                // from before the most recent transition into Synchronized.
                if (state == SourceState.Synchronized && changeTime < Volatile.Read(ref synchronizedAt))
                {
                    torn = true;
                }

                if (state == SourceState.Synchronized)
                {
                    Volatile.Write(ref synchronizedAt, changeTime);
                }
            }
        });

        for (var i = 0; i < 20_000; i++)
        {
            source.TransitionStateTo(SourceState.Synchronized);
            source.TransitionStateTo(SourceState.Synchronizing);
        }

        Volatile.Write(ref stop, true);
        reader.Wait();

        // Assert
        Assert.False(torn);
    }
```

In `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceRetryQueueTests.cs:74`, replace `source.PendingWriteCount` with `source.Diagnostics.OutboundRetries.Depth`. That reads 0 until Task 8 registers the provider, so mark this assertion with the temporary value now and correct it in Task 8; if the assertion is a non-zero count, move the whole test to Task 8 instead of asserting something untrue here.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceStateTests"`

Expected: compile failure, `StateChangeTime` does not exist.

- [ ] **Step 3: Change the interface**

In `src/Namotion.Interceptor.Connectors/ISubjectSource.cs`, replace the `LastSynchronizedAt` member and the `PendingWriteCount` member with:

```csharp
    /// <summary>
    /// Gets when <see cref="State"/> last changed. Stamped at construction, so it is always
    /// meaningful. Read with <see cref="State"/> it answers both questions an operator asks:
    /// <c>Synchronized</c> plus T reads as in sync since T, <c>Synchronizing</c> plus T reads as
    /// stale since T.
    /// </summary>
    DateTimeOffset StateChangeTime { get; }
```

Update the lock-free remark at the top of the interface so it names `StateChangeTime` instead of `LastSynchronizedAt`.

Do **not** add `Diagnostics` to the interface here. It needs `new` to hide the member `ISubjectConnector` does not have yet, and every implementer must already own one. Both interface members land together in Task 12.

- [ ] **Step 4: Replace the state fields with one atomic snapshot**

In `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`, replace the two fields at `:34-35`:

```csharp
    private SourceStateSnapshot _stateSnapshot = new(SourceState.Synchronizing, DateTimeOffset.UtcNow);
```

and add the record beside them:

```csharp
    // The state and its timestamp are swapped as one value. Held in separate fields, a reader can see
    // the new state beside the previous timestamp and report a stale duration that never happened.
    private sealed record SourceStateSnapshot(SourceState State, DateTimeOffset ChangeTime);
```

Replace the two getters at `:524-534`:

```csharp
    /// <inheritdoc />
    public SourceState State => Volatile.Read(ref _stateSnapshot).State;

    /// <inheritdoc />
    public DateTimeOffset StateChangeTime => Volatile.Read(ref _stateSnapshot).ChangeTime;
```

In `TransitionStateTo` (`:576-615`), replace the read of `_state`, the write, and the conditional timestamp stamp:

```csharp
        lock (_stateLock)
        {
            var oldState = _stateSnapshot.State;
            if (oldState == newState || oldState == SourceState.Stopped)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            Volatile.Write(ref _stateSnapshot, new SourceStateSnapshot(newState, now));

            var handlers = StateChanged;
            // ... unchanged from here
```

Delete the `_lastSynchronizedTicks` field and its `if (newState == SourceState.Synchronized)` stamp.

Do not declare `_stateSnapshot` as `volatile`: `Volatile.Read(ref ...)` on a `volatile` field is CS0420 and this repository treats warnings as errors.

- [ ] **Step 5: Remove `PendingWriteCount`**

Delete the property at `:42-45`. `WriteRetryQueue.PendingWriteCount` stays: it is the queue's own member and is used internally at `WriteRetryQueue.cs:159`.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceStateTests|FullyQualifiedName~SubjectSourceRetryQueueTests"`

Expected: PASS.

- [ ] **Step 7: Fix the implementers and the remaining readers**

Run: `grep -rn --include='*.cs' "LastSynchronizedAt\|PendingWriteCount" src`

The five hand-written `ISubjectSource` implementers break here, not in Task 12, because the members they declare no longer exist on the interface. In `SourceSubscriptionTests.cs:245`, `SubjectSourceExtensionsTests.cs:500`, `SourceMonitorTests.cs:601` and `Benchmark/SubjectTransactionBenchmark.cs:109`, delete their `LastSynchronizedAt` and `PendingWriteCount` declarations and add:

```csharp
        public DateTimeOffset StateChangeTime { get; } = DateTimeOffset.UtcNow;
```

Those same four gain `Diagnostics` in Task 12. `FaultTargetResolverTests.cs:21` implements only `ISubjectConnector` and is untouched here.

The remaining hits are readers:

| Where | Replace with |
|---|---|
| `WebSocket.Tests/Integration/OutageStateTests.cs:73,89` | `StateChangeTime`, asserting the timestamp moved rather than that it stayed |
| `OpcUa.Tests/Client/OutageStateTests.cs:105,132` | same |
| `OpcUa/Client/OpcUaClientDiagnostics.cs:96` | leave for Task 14, which rewrites the file |
| `WriteRetryQueue.cs:38,159` | leave alone: this is the queue's own member, not the source's |

For the two outage tests, the assertion changes meaning: today they check that `LastSynchronizedAt` is preserved across an outage. Rewrite them to assert that `StateChangeTime` advanced when the state moved to `Synchronizing`, which is what the member now records.

- [ ] **Step 8: Run the full unit suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`

Expected: PASS. The OPC UA and WebSocket outage tests are integration-tagged; run them in Task 16.

- [ ] **Step 9: Commit**

```bash
git add src/Namotion.Interceptor.Connectors src/Namotion.Interceptor.Connectors.Tests src/Namotion.Interceptor.WebSocket.Tests src/Namotion.Interceptor.OpcUa.Tests
git commit -m "Replace LastSynchronizedAt with StateChangeTime and move PendingWriteCount to diagnostics (#277)"
```

---

### Task 8: Count the outbound drops and register the three buffers

The immediate value of the whole change. Three outbound paths discard data in normal operation today and only log it.

One path is deliberately **not** counted: the disabled-queue drain at `SubjectSourceBase.cs:345-351`. That branch drains the *entire* subscription with no ownership filter, and `PropertyChangeInterceptor` fans every committed change to every queue subscription unfiltered, so the subscription carries other sources' properties and this source's own inbound applies. Counting it would report other sources' traffic as this source's lost writes. The consequence is that with `writeRetryQueueSize: 0` this uncounted drain is the dominant loss path, so `OutboundRetries.TotalDropped` under-reports in exactly that configuration.

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/WriteRetryQueue.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SubjectPropertyWriter.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/Diagnostics/OutboundDropCountingTests.cs`

**Interfaces:**
- Consumes: `QueueMetrics` (Task 1), `SourceMetrics` (Task 2), `ChangeQueueProcessor.QueueDepth` (Task 1), `SubjectPropertyWriter.BufferedUpdateCount` and `SourceOwnershipManager.Count` (Task 5).
- Produces: `WriteRetryQueue(int maxQueueSize, ILogger logger, QueueMetrics metrics)`; `SubjectPropertyWriter(SubjectSourceBase source, ILogger logger, QueueMetrics? inboundBuffer = null)`.

- [ ] **Step 1: Write the failing tests**

Create `src/Namotion.Interceptor.Connectors.Tests/Diagnostics/OutboundDropCountingTests.cs`. Each test must fail if its single `AddDropped` call is removed.

```csharp
namespace Namotion.Interceptor.Connectors.Tests.Diagnostics;

public class OutboundDropCountingTests
{
    [Fact]
    public void WhenTheRetryQueueOverflows_ThenTheDroppedWritesAreCounted()
    {
        // Arrange
        var metrics = new SourceMetrics();
        using var queue = new WriteRetryQueue(maxQueueSize: 2, NullLogger.Instance, metrics.OutboundRetries);
        var diagnostics = new SourceDiagnostics(metrics);

        // Act
        queue.Enqueue(CreateChanges(count: 5));

        // Assert
        Assert.Equal(3, diagnostics.OutboundRetries.TotalDropped);
    }

    [Fact]
    public async Task WhenAQueuedWriteHasNoSetter_ThenItIsCountedAsDropped()
    {
        // Arrange: park a write on a derived (getter-only) property, then reconcile.
        var source = CreateStartedSourceWithParkedDerivedWrite();

        // Act
        await source.ReconcileForTestAsync();

        // Assert
        Assert.Equal(1, source.Diagnostics.OutboundRetries.TotalDropped);
    }

    [Fact]
    public async Task WhenReconcileThrowsForAChange_ThenItIsCountedAsDropped()
    {
        // Arrange: a property whose setter throws.
        var source = CreateStartedSourceWithParkedThrowingWrite();

        // Act
        await source.ReconcileForTestAsync();

        // Assert
        Assert.Equal(1, source.Diagnostics.OutboundRetries.TotalDropped);
    }

    [Fact]
    public async Task WhenThereIsNoRetryQueueAndADirectWriteFails_ThenTheChangesAreCountedAsDropped()
    {
        // Arrange
        var source = CreateStartedSource(writeRetryQueueSize: 0, failWrites: true);

        // Act
        await source.WriteOneChangeAsync();
        await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.OutboundRetries.TotalDropped > 0);

        // Assert
        Assert.Equal(1, source.Diagnostics.OutboundRetries.TotalDropped);
        Assert.Equal(0, source.Diagnostics.OutboundRetries.Capacity);
        Assert.Equal(0, source.Diagnostics.OutboundRetries.Depth);
    }

    [Fact]
    public void WhenTheDisabledQueueDrainRuns_ThenNothingIsCounted()
    {
        // Arrange: a source configured without a retry queue, and a change on a property this source
        // does not own, which the unfiltered drain discards.
        var source = CreateStartedSource(writeRetryQueueSize: 0, failWrites: false);

        // Act
        source.CommitChangeOnUnownedProperty();
        source.DrainForTest();

        // Assert
        Assert.Equal(0, source.Diagnostics.OutboundRetries.TotalDropped);
    }

    [Fact]
    public void WhenABufferedLoadIsSuperseded_ThenTheDiscardedUpdatesAreCounted()
    {
        // Arrange
        var metrics = new SourceMetrics();
        var writer = CreateWriter(metrics.InboundBuffer);
        var diagnostics = new SourceDiagnostics(metrics);
        writer.StartBuffering();
        writer.Write(0, static _ => { });
        writer.Write(1, static _ => { });

        // Act
        writer.StartBuffering();

        // Assert
        Assert.Equal(2, diagnostics.InboundBuffer.TotalDropped);
        Assert.Equal(0, diagnostics.InboundBuffer.Depth);
    }

    [Fact]
    public async Task WhenTheProcessorIsRecreated_ThenTheAccumulatedDropCountSurvives()
    {
        // Arrange: a bounded processor registered against the metrics, dropping into it, then handed
        // over. The in-repo connectors all pass maxQueueDepth: null, so this drives QueueMetrics and
        // ChangeQueueProcessor directly rather than through a source.
        var metrics = new SourceMetrics();
        var diagnostics = new SourceDiagnostics(metrics);
        var first = CreateBoundedProcessor(maxQueueDepth: 1);
        metrics.OutboundChanges.Register(() => first.QueueDepth, () => first.DropCount, capacity: 1);
        await OverflowAsync(first, changeCount: 4);
        var afterFirst = diagnostics.OutboundChanges.TotalDropped;

        // Act
        metrics.OutboundChanges.Deregister();
        first.Dispose();
        var second = CreateBoundedProcessor(maxQueueDepth: 1);
        metrics.OutboundChanges.Register(() => second.QueueDepth, () => second.DropCount, capacity: 1);
        await OverflowAsync(second, changeCount: 4);

        // Assert
        Assert.True(afterFirst > 0);
        Assert.Equal(afterFirst * 2, diagnostics.OutboundChanges.TotalDropped);
        second.Dispose();
    }

    [Fact]
    public async Task WhenASourceIsRunning_ThenItsOutboundChangeQueueIsRegisteredAsUnbounded()
    {
        // Arrange
        var source = CreateStartedSource(writeRetryQueueSize: 1000, failWrites: false);

        // Act
        await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.OutboundChanges.Capacity is null
            && source.Diagnostics.StartTime is not null);

        // Assert
        Assert.Null(source.Diagnostics.OutboundChanges.Capacity);
        Assert.Equal(0, source.Diagnostics.OutboundChanges.TotalDropped);
    }
}
```

Build the arrange helpers on `TestSubjectSource` in the same project, adding whatever test seams the assertions need (`ReconcileForTestAsync`, `DrainForTest`, `WriteOneChangeAsync`, `CommitChangeOnUnownedProperty`) as `internal` members on `SubjectSourceBase` exposed through the test double, not as public API.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~OutboundDropCountingTests"`

Expected: compile failure, `WriteRetryQueue` takes two constructor arguments.

- [ ] **Step 3: Count the ring-buffer overflow**

In `src/Namotion.Interceptor.Connectors/WriteRetryQueue.cs`, add a field and a constructor parameter:

```csharp
    private readonly QueueMetrics _metrics;

    public WriteRetryQueue(int maxQueueSize, ILogger logger, QueueMetrics metrics)
    {
        // ...existing assignments...
        _metrics = metrics;
    }
```

In `Enqueue`, at the existing `if (droppedCount > 0)` block (`:81-87`), add the count before the log call:

```csharp
        if (droppedCount > 0)
        {
            _metrics.AddDropped(droppedCount);
            _logger.LogWarning(
```

- [ ] **Step 4: Count the inbound buffer discards**

In `src/Namotion.Interceptor.Connectors/SubjectPropertyWriter.cs`, add the optional parameter and register the depth provider:

```csharp
    private readonly QueueMetrics? _inboundBuffer;

    public SubjectPropertyWriter(SubjectSourceBase source, ILogger logger, QueueMetrics? inboundBuffer = null)
    {
        _source = source;
        _logger = logger;
        _inboundBuffer = inboundBuffer;

        // Unbounded: the buffer holds whatever arrives while the initial state loads.
        _inboundBuffer?.Register(() => BufferedUpdateCount, dropped: null, capacity: null);
    }
```

In `StartBuffering`, count what the replacement throws away, inside the existing lock and before `_updates` is replaced:

```csharp
        lock (_lock)
        {
            // Replacing the list discards whatever the previous attempt buffered. Deliberate rather
            // than data loss: a superseded snapshot must not be applied. Counted because it is the
            // only signal of how often initial loads are being superseded, which is reconnect thrash.
            _inboundBuffer?.AddDropped(_updates?.Count ?? 0);

            _updates = [];
            Volatile.Write(ref _bufferedUpdateCount, 0);
            _generation++;
```

- [ ] **Step 5: Register the buffers and count the reconcile and direct-write drops**

In `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`'s private constructor, pass the metrics through and register the retry queue:

```csharp
        if (writeRetryQueueSize > 0)
        {
            WriteRetryQueue = new WriteRetryQueue(writeRetryQueueSize, logger, metrics.OutboundRetries);
            metrics.OutboundRetries.Register(
                () => WriteRetryQueue.PendingWriteCount, dropped: null, capacity: writeRetryQueueSize);
        }
        else
        {
            // Registered as disabled rather than left unregistered: an unregistered QueueMetrics
            // reports a null capacity, which reads as unbounded, the opposite of the truth.
            metrics.OutboundRetries.Register(static () => 0, dropped: null, capacity: 0);
        }

        _propertyWriter = new SubjectPropertyWriter(this, logger, metrics.InboundBuffer);
```

Register the claimed-property gauge is **not** done here: `SubjectSourceBase` owns no ownership manager. Each client source registers its own, in Tasks 13 and 14.

In `RunAsync`, replace the `using var processor = new ChangeQueueProcessor(...)` block (`:233-243`) with a try/finally so deregistration precedes disposal:

```csharp
                    // Connected phase reuses the source-lifetime subscription and does not own it.
                    var processor = new ChangeQueueProcessor(
                        this,
                        subscription,
                        propertyReference => propertyReference.TryGetSource(out var source) && source == this,
                        WriteChangesViaRetryQueueAsync,
                        DeliveryRule,
                        _bufferTime,
                        maxQueueDepth: null,
                        logger: _logger);

                    Metrics.OutboundChanges.Register(
                        () => processor.QueueDepth, () => processor.DropCount, capacity: null);
                    try
                    {
                        await processor.ProcessAsync(stoppingToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        // Before Dispose, so no reader can call into a disposed processor. This narrows
                        // the race rather than closing it, which is safe only because the processor's
                        // queue and drop count survive its disposal.
                        Metrics.OutboundChanges.Deregister();
                        processor.Dispose();
                    }
```

In `ReconcileRetryQueueAsync`, count the two loss branches. In the no-setter `else` (`:483-491`):

```csharp
                else
                {
                    dropped++;
                    Metrics.OutboundRetries.AddDropped(1);
                    _logger.LogWarning(
```

and in the catch (`:493-499`):

```csharp
            catch (Exception exception)
            {
                failed++;
                Metrics.OutboundRetries.AddDropped(1);
                _logger.LogWarning(exception,
```

Do **not** count the `!ChangeDeliveryFilter.IsCurrent` branch: a later local commit supersedes that change and that commit's change is delivered in its place, so nothing is lost.

In `WriteChangesViaRetryQueueAsync`'s no-retry-queue path (`:267-288`), count both failure branches:

```csharp
        if (WriteRetryQueue is null)
        {
            // Without a retry queue there is nowhere to park a failed write, so it is discarded.
            // Attributed to OutboundRetries, which reports capacity 0 in this configuration.
            try
            {
                var result = await this.WriteChangesInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
                if (!result.IsFullySuccessful)
                {
                    Metrics.OutboundRetries.AddDropped(result.FailedChanges.Length);
                    _logger.LogError(result.Error, "Failed to write {Count} changes to source.",
                        result.FailedChanges.Length);
                }
            }
            catch (OperationCanceledException)
            {
                throw; // Don't swallow cancellation
            }
            catch (Exception e)
            {
                Metrics.OutboundRetries.AddDropped(changes.Length);
                _logger.LogError(e, "Failed to write changes to source.");
            }
            return;
        }
```

Leave `DrainOwnedWritesToRetryQueue`'s `WriteRetryQueue is null` branch (`:345-351`) uncounted, and add the reason as a comment there:

```csharp
        // No retry queue: still drain the subscription to empty it, but there is nothing to reconcile.
        // Deliberately uncounted: this drain has no ownership filter and the subscription carries every
        // committed change in the process, including other sources' properties and this source's own
        // inbound applies. Counting it would report other sources' traffic as this source's lost writes.
        if (WriteRetryQueue is null)
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~OutboundDropCountingTests"`

Expected: PASS, 8 tests.

- [ ] **Step 7: Verify each counter is load-bearing**

For each of the four `AddDropped` call sites, comment it out, run its test, confirm the test fails, and restore it. A drop counter that no test pins is a counter that silently stops working.

- [ ] **Step 8: Run the full unit suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`

Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Namotion.Interceptor.Connectors src/Namotion.Interceptor.Connectors.Tests
git commit -m "Count outbound write drops and register the three connector buffers (#277)"
```

---

### Task 9: OPC UA server onto `SubjectConnectorBase`

The first of three servers. Each owns its own restart loop with its own `finally` today, so a faulting server keeps reporting that it is running. Sealing `ExecuteAsync` in the base is what fixes that, and each server's loop moves into `RunAsync` unchanged.

The base constructor cannot receive a field of the derived class, because a constructor initializer cannot reference `this`. The counters are therefore created as arguments to a private constructor, which passes them to `base(...)` and keeps them. The same shape appears in Tasks 10, 11 and 13.

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServer.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerDiagnostics.cs`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Server/OpcUaServerDiagnosticsTests.cs`

**Interfaces:**
- Consumes: `SubjectConnectorBase`, `ConnectorMetrics`, `ConnectorDiagnostics`.
- Produces: `OpcUaServerDiagnostics : ConnectorDiagnostics`, sealed, with `int ActiveSessionCount` and `int ConsecutiveFailures` and nothing else of its own.

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.OpcUa.Tests/Server/OpcUaServerDiagnosticsTests.cs`. Model the arrange block on the existing `OpcUaServerDeliveryRuleTests.cs`, which already constructs a server without starting it.

```csharp
namespace Namotion.Interceptor.OpcUa.Tests.Server;

public class OpcUaServerDiagnosticsTests
{
    [Fact]
    public void WhenNeverStarted_ThenTheServerReportsNotOperational()
    {
        // Arrange & Act
        using var server = CreateServer();

        // Assert
        Assert.False(server.Diagnostics.IsOperational);
        Assert.Null(server.Diagnostics.StartTime);
        Assert.Null(server.Diagnostics.LastError);
        Assert.Equal(0, server.Diagnostics.ConsecutiveFailures);
    }

    [Fact]
    public void WhenDisposed_ThenTheServerReportsNotOperational()
    {
        // Arrange
        var server = CreateServer();

        // Act
        server.Dispose();

        // Assert
        Assert.False(server.Diagnostics.IsOperational);
    }

    [Fact]
    public void WhenThroughputIsInstrumented_ThenBothDirectionsReportARate()
    {
        // Arrange & Act
        using var server = CreateServer();

        // Assert
        Assert.NotNull(server.Diagnostics.Throughput.IncomingPerSecond);
        Assert.NotNull(server.Diagnostics.Throughput.OutgoingPerSecond);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaServerDiagnosticsTests"`

Expected: compile failure, `Diagnostics` has no `IsOperational`.

- [ ] **Step 3: Change the server's base class and constructor**

In `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServer.cs`, change the declaration at `:14`:

```csharp
internal class OpcUaSubjectServer : SubjectConnectorBase, IOpcUaSubjectServer, IFaultInjectable
```

`ISubjectConnector` is dropped from the list because `SubjectConnectorBase` already implements it.

Replace the throughput fields at `:39-40` and split the constructor:

```csharp
    public OpcUaSubjectServer(/* ...existing parameters... */)
        : this(/* ...existing arguments..., */ new ThroughputCounter(), new ThroughputCounter())
    {
    }

    private OpcUaSubjectServer(
        /* ...existing parameters..., */
        ThroughputCounter incoming,
        ThroughputCounter outgoing)
        : base(new ConnectorMetrics(incoming, outgoing))
    {
        IncomingThroughput = incoming;
        OutgoingThroughput = outgoing;
        Diagnostics = new OpcUaServerDiagnostics(this, Metrics);

        // ...rest of the existing constructor body, minus the old Diagnostics assignment at :111...
    }

    internal ThroughputCounter IncomingThroughput { get; }

    internal ThroughputCounter OutgoingThroughput { get; }

    /// <inheritdoc cref="SubjectConnectorBase.Diagnostics" />
    public override OpcUaServerDiagnostics Diagnostics { get; }
```

- [ ] **Step 4: Move the pump and wire liveness**

Rename `ExecuteAsync` (`:210`) to `RunAsync`, keeping the signature otherwise identical:

```csharp
    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
```

In `ExecuteServerLoopAsync`, mark the server operational where it starts serving and not operational where it stops. Replace the block at `:266-268`:

```csharp
                    _consecutiveFailures = 0;
                    Metrics.MarkOperational();
```

Delete `_startTime = DateTimeOffset.UtcNow;` and `_lastError = null;` from that block. The start time is now `Diagnostics.StartTime`, which does not move on an internal restart, and `LastError` is sticky, so clearing it on recovery would erase the only evidence of a transient fault.

In the inner `finally` (`:271-277`), replace `_startTime = null;` with:

```csharp
                    Metrics.MarkNotOperational();
```

Route every remaining `_lastError = ...` assignment in the file through `Metrics.ReportError(exception)` and delete the `_lastError` field and the internal `LastError` property. Delete the `_startTime` field and the internal `StartTime` property.

Run `grep -n "IsRunning" src/Namotion.Interceptor.OpcUa` before deleting the internal `IsRunning` at `:80`: if only the diagnostics type reads it, delete it; if `OpcUaStandardServer` or a test reads it, keep the internal member and only remove it from the diagnostics surface.

- [ ] **Step 5: Register the outbound change queue**

In `ExecuteServerLoopAsync`, replace `using var changeQueueProcessor = CreateChangeQueueProcessor();` (`:262`) with a try/finally around the `ProcessAsync` call, matching Task 8's shape:

```csharp
                    var changeQueueProcessor = CreateChangeQueueProcessor();
                    Metrics.OutboundChanges.Register(
                        () => changeQueueProcessor.QueueDepth, () => changeQueueProcessor.DropCount, capacity: null);
                    try
                    {
                        // ...the existing certificate check, StartAsync, liveness mark and ProcessAsync...
                    }
                    finally
                    {
                        Metrics.OutboundChanges.Deregister();
                        changeQueueProcessor.Dispose();
                    }
```

The two test call sites of `CreateChangeQueueProcessor` (`OpcUaServerDeliveryRuleTests.cs:31` and `MqttServerDeliveryRuleTests.cs:32`) stay `using var` and do not register: registration belongs at the production call site so an embedded or test processor does not wire itself into the server's metrics.

- [ ] **Step 6: Rewrite `OpcUaServerDiagnostics`**

Replace `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerDiagnostics.cs` entirely:

```csharp
using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// What the OPC UA server reports about its transport, on top of the shared connector diagnostics.
/// </summary>
/// <remarks>
/// <see cref="ConnectorDiagnostics.IsOperational"/> means the server has started and is accepting
/// client connections. It replaces the former <c>IsRunning</c>, and
/// <see cref="ConnectorDiagnostics.OperationalChangeTime"/> replaces the former <c>StartTime</c> and
/// <c>Uptime</c>: it moves on every internal restart, where
/// <see cref="ConnectorDiagnostics.StartTime"/> does not.
/// </remarks>
public sealed class OpcUaServerDiagnostics : ConnectorDiagnostics
{
    private readonly OpcUaSubjectServer _server;

    internal OpcUaServerDiagnostics(OpcUaSubjectServer server, ConnectorMetrics metrics)
        : base(metrics)
    {
        _server = server;
    }

    /// <summary>
    /// Gets the number of currently active client sessions.
    /// </summary>
    public int ActiveSessionCount => _server.ActiveSessionCount;

    /// <summary>
    /// Gets the number of consecutive startup failures. A gauge that resets on a successful start,
    /// which is why it carries no <c>Total</c> prefix.
    /// </summary>
    public int ConsecutiveFailures => _server.ConsecutiveFailures;
}
```

- [ ] **Step 7: Run the tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category!=Integration"`

Expected: PASS, apart from tests reading the members this task removed. Fix those by reading the new equivalents: `IsRunning` becomes `IsOperational`, `StartTime`/`Uptime` become `OperationalChangeTime`, `IncomingChangesPerSecond` becomes `Throughput.IncomingPerSecond`, `OutgoingChangesPerSecond` becomes `Throughput.OutgoingPerSecond`.

`OpcUaServerSelfWriteTests.cs:53,82,88` and `SelfEchoReproTests.cs:188` assert incoming throughput is 0 and are the #425 regression tests. `OpcUaReadWriteTests.cs:74-75` asserts the positive mirror. Preserve all five assertions exactly, only changing the member path. Losing them would silently reopen #425.

- [ ] **Step 8: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa src/Namotion.Interceptor.OpcUa.Tests
git commit -m "Move the OPC UA server onto SubjectConnectorBase (#277)"
```

---

### Task 10: MQTT server onto `SubjectConnectorBase`

Same shape as Task 9, with no throughput counters: the MQTT server measures neither direction today, and instrumenting it is a follow-up rather than part of this change.

**Files:**
- Modify: `src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServer.cs`
- Create: `src/Namotion.Interceptor.Mqtt/Server/MqttServerDiagnostics.cs`
- Test: `src/Namotion.Interceptor.Mqtt.Tests/Server/MqttServerDiagnosticsTests.cs`

**Interfaces:**
- Produces: `MqttServerDiagnostics : ConnectorDiagnostics`, sealed, with `int ConnectedClientCount`.

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.Mqtt.Tests/Server/MqttServerDiagnosticsTests.cs`:

```csharp
namespace Namotion.Interceptor.Mqtt.Tests.Server;

public class MqttServerDiagnosticsTests
{
    [Fact]
    public void WhenNeverStarted_ThenTheServerReportsNotOperationalAndNoThroughput()
    {
        // Arrange & Act
        using var server = CreateServer();

        // Assert
        Assert.False(server.Diagnostics.IsOperational);
        Assert.Null(server.Diagnostics.StartTime);
        Assert.Equal(0, server.Diagnostics.ConnectedClientCount);
        Assert.Null(server.Diagnostics.Throughput.IncomingPerSecond);
        Assert.Null(server.Diagnostics.Throughput.OutgoingPerSecond);
    }

    [Fact]
    public void WhenDisposed_ThenTheServerReportsNotOperational()
    {
        // Arrange
        var server = CreateServer();

        // Act
        server.Dispose();

        // Assert
        Assert.False(server.Diagnostics.IsOperational);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Mqtt.Tests --filter "FullyQualifiedName~MqttServerDiagnosticsTests"`

Expected: compile failure, `MqttSubjectServer` has no `Diagnostics`.

- [ ] **Step 3: Change the server**

In `src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServer.cs`, change the declaration at `:25`:

```csharp
public class MqttSubjectServer : SubjectConnectorBase, IFaultInjectable, IAsyncDisposable
```

Add to the constructor initializer `: base(new ConnectorMetrics())` and, in the body:

```csharp
        Diagnostics = new MqttServerDiagnostics(this, Metrics);
```

Add the property:

```csharp
    /// <inheritdoc cref="SubjectConnectorBase.Diagnostics" />
    public override MqttServerDiagnostics Diagnostics { get; }
```

Delete `IsListening` (`:68`) and `NumberOfClients` (`:73`) as public members. Keep `_isListening` and `_numberOfClients` as fields: they still drive the internal logic at `:632`.

Rename `ExecuteAsync` (`:146`) to `RunAsync`.

Wire liveness at the four existing `_isListening` writes:

- `:182` becomes `Volatile.Write(ref _isListening, 1); Metrics.MarkOperational();`
- `:195`, `:208` and `:652` each become `Volatile.Write(ref _isListening, 0); Metrics.MarkNotOperational();`

Register the outbound change queue at `:188`, replacing `using var changeQueueProcessor = CreateChangeQueueProcessor();` with the same try/finally shape used in Task 9.

Route the server's failure handling through `Metrics.ReportError(exception)` wherever it currently logs a start failure, so `LastError` is populated for a connector that has never had one.

- [ ] **Step 4: Write `MqttServerDiagnostics`**

Create `src/Namotion.Interceptor.Mqtt/Server/MqttServerDiagnostics.cs`:

```csharp
using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Mqtt.Server;

/// <summary>
/// What the MQTT server reports about its transport, on top of the shared connector diagnostics.
/// </summary>
/// <remarks>
/// <see cref="ConnectorDiagnostics.IsOperational"/> means the broker is listening. It replaces the
/// former <c>IsListening</c>. Neither throughput direction is measured, so both rates are
/// <c>null</c> rather than 0.
/// </remarks>
public sealed class MqttServerDiagnostics : ConnectorDiagnostics
{
    private readonly MqttSubjectServer _server;

    internal MqttServerDiagnostics(MqttSubjectServer server, ConnectorMetrics metrics)
        : base(metrics)
    {
        _server = server;
    }

    /// <summary>
    /// Gets the number of clients currently connected to the broker.
    /// </summary>
    public int ConnectedClientCount => _server.ConnectedClientCount;
}
```

Add the internal accessor the diagnostics reads, next to the deleted public one:

```csharp
    internal int ConnectedClientCount => Volatile.Read(ref _numberOfClients);
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test src/Namotion.Interceptor.Mqtt.Tests --filter "Category!=Integration"`

Expected: PASS. Fix any test reading `IsListening` or `NumberOfClients` to read `Diagnostics.IsOperational` and `Diagnostics.ConnectedClientCount`.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Mqtt src/Namotion.Interceptor.Mqtt.Tests
git commit -m "Move the MQTT server onto SubjectConnectorBase (#277)"
```

---

### Task 11: WebSocket server and handler onto `SubjectConnectorBase`

`WebSocketSubjectHandler` is public (`:21`) and declares its own `ConnectionCount` (`:40`) and `CurrentSequence` (`:42`), read directly by `SequenceCounterTests.cs:30,36,49`. Removing only the server's forwarders would leave two public spellings of the same number, so the handler is in scope for the same rule.

`WebSocketSubjectChangeProcessor.cs:33` deliberately does not register: it is the embedded mode's own processor, and the factory it calls is shared with the server, so registering inside the factory would wire embedded mode into the server's metrics.

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectServer.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs`
- Create: `src/Namotion.Interceptor.WebSocket/Server/WebSocketServerDiagnostics.cs`
- Test: `src/Namotion.Interceptor.WebSocket.Tests/Server/WebSocketServerDiagnosticsTests.cs`

**Interfaces:**
- Produces: `WebSocketServerDiagnostics : ConnectorDiagnostics`, sealed, with `int ConnectionCount` and `long CurrentSequence`.

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.WebSocket.Tests/Server/WebSocketServerDiagnosticsTests.cs`:

```csharp
namespace Namotion.Interceptor.WebSocket.Tests.Server;

public class WebSocketServerDiagnosticsTests
{
    [Fact]
    public void WhenNeverStarted_ThenTheServerReportsNotOperational()
    {
        // Arrange & Act
        using var server = CreateServer();

        // Assert
        Assert.False(server.Diagnostics.IsOperational);
        Assert.Null(server.Diagnostics.StartTime);
        Assert.Equal(0, server.Diagnostics.ConnectionCount);
        Assert.Equal(0, server.Diagnostics.CurrentSequence);
    }

    [Fact]
    public void WhenDisposed_ThenTheServerReportsNotOperational()
    {
        // Arrange
        var server = CreateServer();

        // Act
        server.Dispose();

        // Assert
        Assert.False(server.Diagnostics.IsOperational);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "FullyQualifiedName~WebSocketServerDiagnosticsTests"`

Expected: compile failure.

- [ ] **Step 3: Change the server**

In `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectServer.cs`, change the declaration at `:19`:

```csharp
public sealed class WebSocketSubjectServer : SubjectConnectorBase, IFaultInjectable, IAsyncDisposable
```

Add `: base(new ConnectorMetrics())` to the constructor initializer and, in the body:

```csharp
        Diagnostics = new WebSocketServerDiagnostics(this, Metrics);
```

Add:

```csharp
    /// <inheritdoc cref="SubjectConnectorBase.Diagnostics" />
    public override WebSocketServerDiagnostics Diagnostics { get; }
```

Delete the public `ConnectionCount` (`:36`) and `CurrentSequence` (`:41`) forwarders and add internal ones in their place so the diagnostics type can read them:

```csharp
    internal int ConnectionCount => _handler.ConnectionCount;

    internal long CurrentSequence => _handler.CurrentSequence;
```

Rename `ExecuteAsync` (`:80`) to `RunAsync`.

Mark liveness around the Kestrel lifetime. After `await _app.StartAsync(stoppingToken).ConfigureAwait(false);` (`:97`):

```csharp
                Metrics.MarkOperational();
```

and in the iteration's `finally`, where `_app` is torn down:

```csharp
                Metrics.MarkNotOperational();
```

Register the outbound change queue at `:98`, replacing `using var changeQueueProcessor = _handler.CreateChangeQueueProcessor(_logger);` with the try/finally shape from Task 9.

Route the server's start failures through `Metrics.ReportError(exception)`.

- [ ] **Step 4: Narrow the handler's duplicate spellings**

In `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs`, change `ConnectionCount` (`:40`) and `CurrentSequence` (`:42`) from `public` to `internal`. They stay readable from the server and from `SequenceCounterTests.cs:30,36,49` in the same assembly's test project only if that project has `InternalsVisibleTo`; check `src/Namotion.Interceptor.WebSocket/Namotion.Interceptor.WebSocket.csproj` first. If it does not, update those three test reads to go through the server's `Diagnostics` instead, which is the surface this change is establishing.

- [ ] **Step 5: Write `WebSocketServerDiagnostics`**

Create `src/Namotion.Interceptor.WebSocket/Server/WebSocketServerDiagnostics.cs`:

```csharp
using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// What the WebSocket server reports about its transport, on top of the shared connector diagnostics.
/// </summary>
/// <remarks>
/// <see cref="ConnectorDiagnostics.IsOperational"/> means the listener is accepting connections.
/// Neither throughput direction is measured, so both rates are <c>null</c> rather than 0.
/// </remarks>
public sealed class WebSocketServerDiagnostics : ConnectorDiagnostics
{
    private readonly WebSocketSubjectServer _server;

    internal WebSocketServerDiagnostics(WebSocketSubjectServer server, ConnectorMetrics metrics)
        : base(metrics)
    {
        _server = server;
    }

    /// <summary>
    /// Gets the number of currently connected WebSocket clients.
    /// </summary>
    public int ConnectionCount => _server.ConnectionCount;

    /// <summary>
    /// Gets the sequence number most recently assigned to an outgoing message. A monotonic position
    /// in the message stream rather than a count of events, which is why it carries no <c>Total</c>
    /// prefix.
    /// </summary>
    public long CurrentSequence => _server.CurrentSequence;
}
```

- [ ] **Step 6: Fix the WebSocket test fallout**

Run: `grep -rn --include='*.cs' "ConnectionCount\|CurrentSequence" src/Namotion.Interceptor.WebSocket.Tests`

Expected: `WebSocketServerClientTests.cs:308,311,321,322,337,340,357,361` and `SequenceNumberTests.cs:78,94,97,132,135,138,141,375,444,498,501,504,549,552`. Each becomes `server.Diagnostics.ConnectionCount` or `server.Diagnostics.CurrentSequence`.

- [ ] **Step 7: Run the tests**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "Category!=Integration"`

Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket src/Namotion.Interceptor.WebSocket.Tests
git commit -m "Move the WebSocket server onto SubjectConnectorBase (#277)"
```

---

### Task 12: Put `Diagnostics` on the interfaces

Every implementer now has the member, so adding it to the interfaces is a small change. Doing it last is what keeps every earlier task's build green.

Covariant *implicit* interface implementation does not exist in C#: a `public SourceDiagnostics Diagnostics { get; }` does not satisfy `ISubjectConnector.Diagnostics` (CS0738). Every hand-written implementer therefore needs **two** members. `SubjectConnectorBase` supplies the explicit forwarder for everything that derives from it.

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ISubjectConnector.cs`
- Modify: `src/Namotion.Interceptor.Connectors/ISubjectSource.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SubjectConnectorBase.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/SourceSubscriptionTests.cs:245`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceExtensionsTests.cs:500`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/SourceMonitorTests.cs:601`
- Modify: `src/Namotion.Interceptor.Benchmark/SubjectTransactionBenchmark.cs:109`
- Modify: `src/Namotion.Interceptor.ConnectorTester.Tests/Connectors/FaultTargetResolverTests.cs:21`

**Interfaces:**
- Produces: `ISubjectConnector.Diagnostics` returning `ConnectorDiagnostics`; `ISubjectSource.Diagnostics` returning `SourceDiagnostics`, declared with `new`.

- [ ] **Step 1: Add the interface members**

In `src/Namotion.Interceptor.Connectors/ISubjectConnector.cs`:

```csharp
    /// <summary>
    /// Gets what this connector reports about the transport it drives.
    /// </summary>
    /// <remarks>
    /// Answers "what is the transport doing". Whether the model can be trusted is a separate question
    /// answered by <see cref="ISubjectSource.State"/>.
    /// </remarks>
    Diagnostics.ConnectorDiagnostics Diagnostics { get; }
```

In `src/Namotion.Interceptor.Connectors/ISubjectSource.cs`, add the narrowed member. The `new` is required: without it the member hides the inherited one and produces CS0108, which this repository treats as an error.

```csharp
    /// <summary>
    /// Gets what this source reports about its transport and its buffers.
    /// </summary>
    new SourceDiagnostics Diagnostics { get; }
```

- [ ] **Step 2: Add the explicit forwarder to the base**

In `src/Namotion.Interceptor.Connectors/SubjectConnectorBase.cs`, below the abstract `Diagnostics`:

```csharp
    // Covariant implicit interface implementation does not exist, so a derived class returning a
    // narrower type does not satisfy the interface member on its own. Interface dispatch plus a
    // virtual call, not a free read.
    ConnectorDiagnostics ISubjectConnector.Diagnostics => Diagnostics;
```

`SubjectSourceBase.Diagnostics` returns exactly `SourceDiagnostics`, so it satisfies `ISubjectSource.Diagnostics` implicitly and needs no forwarder of its own.

- [ ] **Step 3: Fix the five hand-written implementers**

Each of the four `ISubjectSource` implementers gets both members:

```csharp
        public SourceDiagnostics Diagnostics { get; } = new(new SourceMetrics());

        ConnectorDiagnostics ISubjectConnector.Diagnostics => Diagnostics;
```

`FaultTargetResolverTests.cs:21` implements `ISubjectConnector` directly and needs only one:

```csharp
        public ConnectorDiagnostics Diagnostics { get; } = new(new ConnectorMetrics());
```

Those same four classes already gained `StateChangeTime` and lost `LastSynchronizedAt` and `PendingWriteCount` in Task 7, so `Diagnostics` is the only member they still need.

- [ ] **Step 4: Build and run the full unit suite**

Run: `dotnet build src/Namotion.Interceptor.slnx`

Expected: PASS.

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`

Expected: PASS. The roughly eighty `Mock<ISubjectSource>` and ten `Mock.Of<ISubjectSource>` sites auto-stub `Diagnostics` to null. No production code dereferences `ISubjectSource.Diagnostics`, so nothing should break. If a test does fail on a null `Diagnostics`, set it up in that test rather than adding a shared helper: a single failing site is not evidence of a systemic problem.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors src/Namotion.Interceptor.Connectors.Tests src/Namotion.Interceptor.Benchmark src/Namotion.Interceptor.ConnectorTester.Tests
git commit -m "Expose Diagnostics on ISubjectConnector and ISubjectSource (#277)"
```

---

### Task 13: Hoist the OPC UA client's sub-metrics so their totals survive a reconnect

Nine counters are about to be renamed to `Total*`, and on arrival they would all violate the convention. `_sessionManager = new SessionManager(...)` sits inside `StartListeningAsync` (`OpcUaSubjectClientSource.cs:118`), which `SubjectSourceBase` calls once per **connect attempt**, including attempts that fail, and `PollingManager` and `ReadAfterWriteManager` are built in that constructor (`SessionManager.cs:143`, `:152`). All nine reset on every attempt, so during a reconnect storm they sit near zero permanently.

The counters do not live in `SessionManager`; they live in `PollingMetrics` and `ReadAfterWriteMetrics`, which it merely constructs. Hoisting those two objects up one level is two constructor parameters and makes all nine survive by construction.

**One counter needs more than the hoist.** `PollingManager.CircuitBreakerTrips` reads `_circuitBreaker.TripCount` (`:85`), and `CircuitBreaker` is built inside `PollingManager`'s own constructor (`:53`), so it dies with every `SessionManager`. Hoisting the breaker itself is wrong: it also carries `IsOpen`, which must start closed on a fresh connection. `PollingMetrics` gains its own trip counter instead, and `IsCircuitBreakerOpen` keeps reading the live breaker.

Hoisting changes what these counters mean for anyone reading them, from per-attempt to per-source. Nothing in this repository reads them outside `PollingMetricsTests` and `ReadAfterWriteManagerTests`, which construct the metrics directly.

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Polling/PollingMetrics.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Polling/PollingManager.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/ReadAfterWrite/ReadAfterWriteMetrics.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/ReadAfterWrite/ReadAfterWriteManager.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/ReconnectionMetrics.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/SessionManager.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Client/HoistedMetricsTests.cs`

**Interfaces:**
- Consumes: `IResettableMetrics`, `ConnectorMetrics.RegisterResettable`.
- Produces: `PollingMetrics : IResettableMetrics` with `CircuitBreakerTrips` and `RecordCircuitBreakerTrip()`; `ReadAfterWriteMetrics : IResettableMetrics`; `ReconnectionMetrics : IResettableMetrics`; `SessionManager(..., PollingMetrics pollingMetrics, ReadAfterWriteMetrics readAfterWriteMetrics)`; `PollingManager(..., PollingMetrics metrics)`; `ReadAfterWriteManager(..., ReadAfterWriteMetrics metrics)`; `OpcUaSubjectClientSource.PollingMetrics` and `.ReadAfterWriteMetrics` (internal).

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.OpcUa.Tests/Client/HoistedMetricsTests.cs`:

```csharp
namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class HoistedMetricsTests
{
    [Fact]
    public void WhenPollingMetricsAreReset_ThenEveryCumulativeCounterReturnsToZero()
    {
        // Arrange
        var metrics = new PollingMetrics();
        metrics.RecordRead();
        metrics.RecordFailedRead();
        metrics.RecordValueChange();
        metrics.RecordSlowPoll();
        metrics.RecordCircuitBreakerTrip();

        // Act
        metrics.Reset();

        // Assert
        Assert.Equal(0, metrics.TotalReads);
        Assert.Equal(0, metrics.FailedReads);
        Assert.Equal(0, metrics.ValueChanges);
        Assert.Equal(0, metrics.SlowPolls);
        Assert.Equal(0, metrics.CircuitBreakerTrips);
    }

    [Fact]
    public void WhenReadAfterWriteMetricsAreReset_ThenEveryCumulativeCounterReturnsToZero()
    {
        // Arrange
        var metrics = new ReadAfterWriteMetrics();
        metrics.RecordScheduled();
        metrics.RecordExecuted(2);
        metrics.RecordCoalesced();
        metrics.RecordFailed();

        // Act
        metrics.Reset();

        // Assert
        Assert.Equal(0, metrics.Scheduled);
        Assert.Equal(0, metrics.Executed);
        Assert.Equal(0, metrics.Coalesced);
        Assert.Equal(0, metrics.Failed);
    }

    [Fact]
    public void WhenReconnectionMetricsAreReset_ThenCountersClearButTheLastConnectionSurvives()
    {
        // Arrange
        var metrics = new ReconnectionMetrics();
        metrics.RecordAttemptStart();
        metrics.RecordSuccess();
        metrics.RecordFailure();
        metrics.RecordAbandoned();
        var lastConnected = metrics.LastConnectedAt;

        // Act
        metrics.Reset();

        // Assert
        Assert.Equal(0, metrics.TotalAttempts);
        Assert.Equal(0, metrics.Successful);
        Assert.Equal(0, metrics.Failed);
        Assert.Equal(0, metrics.Abandoned);
        Assert.Equal(lastConnected, metrics.LastConnectedAt);
    }

    [Fact]
    public async Task WhenTheSessionManagerIsRecreated_ThenPollingCountersAreNotRebased()
    {
        // Arrange
        var source = CreateClientSource();
        source.PollingMetrics.RecordRead();
        source.PollingMetrics.RecordCircuitBreakerTrip();

        // Act
        await source.RecreateSessionManagerForTestAsync();

        // Assert
        Assert.Equal(1, source.PollingMetrics.TotalReads);
        Assert.Equal(1, source.PollingMetrics.CircuitBreakerTrips);
    }
}
```

`RecordCircuitBreakerTrip` must be reachable from the test project; if `PollingMetrics` is `internal`, the OPC UA test project already has `InternalsVisibleTo` (it constructs `PollingMetrics` in `PollingMetricsTests`), so no visibility change is needed.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~HoistedMetricsTests"`

Expected: compile failure, `Reset` and `RecordCircuitBreakerTrip` do not exist.

- [ ] **Step 3: Make the three metrics objects resettable and give `PollingMetrics` the trip counter**

In `src/Namotion.Interceptor.OpcUa/Client/Polling/PollingMetrics.cs`:

```csharp
internal sealed class PollingMetrics : IResettableMetrics
{
    private long _totalReads;
    private long _failedReads;
    private long _valueChanges;
    private long _slowPolls;
    private long _circuitBreakerTrips;

    // ...existing getters and Record methods unchanged...

    /// <summary>
    /// Gets the total number of times the circuit breaker has tripped.
    /// </summary>
    /// <remarks>
    /// Counted here rather than read from the breaker, which is rebuilt with every session and would
    /// therefore rebase this counter on every connect attempt. The breaker still owns
    /// <c>IsOpen</c>, which must start closed on a fresh connection.
    /// </remarks>
    public long CircuitBreakerTrips => Interlocked.Read(ref _circuitBreakerTrips);

    /// <summary>
    /// Records a circuit breaker trip.
    /// </summary>
    public void RecordCircuitBreakerTrip() => Interlocked.Increment(ref _circuitBreakerTrips);

    /// <inheritdoc />
    public void Reset()
    {
        Interlocked.Exchange(ref _totalReads, 0);
        Interlocked.Exchange(ref _failedReads, 0);
        Interlocked.Exchange(ref _valueChanges, 0);
        Interlocked.Exchange(ref _slowPolls, 0);
        Interlocked.Exchange(ref _circuitBreakerTrips, 0);
    }
}
```

In `src/Namotion.Interceptor.OpcUa/Client/ReadAfterWrite/ReadAfterWriteMetrics.cs`, add:

```csharp
internal sealed class ReadAfterWriteMetrics : IResettableMetrics
{
    // ...unchanged...

    /// <inheritdoc />
    public void Reset()
    {
        Interlocked.Exchange(ref _scheduled, 0);
        Interlocked.Exchange(ref _executed, 0);
        Interlocked.Exchange(ref _coalesced, 0);
        Interlocked.Exchange(ref _failed, 0);
    }
}
```

In `src/Namotion.Interceptor.OpcUa/Client/ReconnectionMetrics.cs`, add:

```csharp
internal sealed class ReconnectionMetrics : IResettableMetrics
{
    // ...unchanged...

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="LastConnectedAt"/> is deliberately preserved: it records a discrete past event and
    /// survives the state it describes, which is what its <c>Last</c> prefix means.
    /// </remarks>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalAttempts, 0);
        Interlocked.Exchange(ref _successful, 0);
        Interlocked.Exchange(ref _failed, 0);
        Interlocked.Exchange(ref _abandoned, 0);
    }
}
```

- [ ] **Step 4: Count the trips in `PollingManager` and take the metrics by constructor**

In `src/Namotion.Interceptor.OpcUa/Client/Polling/PollingManager.cs`, replace `private readonly PollingMetrics _metrics = new();` (`:26`) with a constructor parameter:

```csharp
    private readonly PollingMetrics _metrics;

    public PollingManager(OpcUaSubjectClientSource source,
        SessionManager sessionManager,
        SubjectPropertyWriter propertyWriter,
        OpcUaClientConfiguration configuration,
        PollingMetrics metrics,
        ILogger logger)
    {
        // ...existing guards...
        _metrics = metrics;
        // ...rest unchanged...
    }
```

Change `CircuitBreakerTrips` (`:85`) to read the hoisted counter:

```csharp
    public long CircuitBreakerTrips => _metrics.CircuitBreakerTrips;
```

Find every `_circuitBreaker.RecordFailure()` call in the file and record the trip on its `true` return:

```csharp
        if (_circuitBreaker.RecordFailure())
        {
            _metrics.RecordCircuitBreakerTrip();
            // ...existing handling...
        }
```

If a call site currently discards the return value, capture it. `CircuitBreaker.RecordFailure` returns true exactly when that failure opened the circuit, which is the same event `_tripCount` counts, so the two stay in step.

- [ ] **Step 5: Take the read-after-write metrics by constructor**

In `src/Namotion.Interceptor.OpcUa/Client/ReadAfterWrite/ReadAfterWriteManager.cs`, replace the `Metrics` property's inline construction with a constructor parameter of the same name and type, keeping the property as a read-only forwarder so its existing readers do not change.

- [ ] **Step 6: Thread both through `SessionManager`**

In `src/Namotion.Interceptor.OpcUa/Client/SessionManager.cs`, add two constructor parameters and pass them to the two managers built at `:143` and `:152`:

```csharp
    public SessionManager(
        OpcUaSubjectClientSource source,
        SubjectPropertyWriter propertyWriter,
        OpcUaClientConfiguration configuration,
        PollingMetrics pollingMetrics,
        ReadAfterWriteMetrics readAfterWriteMetrics,
        ILogger logger)
```

- [ ] **Step 7: Own both on the source and enrol them in the reset**

In `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`, add beside `ReconnectionMetrics` (`:44`):

```csharp
    // Owned here rather than by SessionManager, which is rebuilt on every connect attempt including
    // failed ones. Without this the nine Total counters below would sit near zero during a reconnect
    // storm, which is exactly when they matter.
    internal PollingMetrics PollingMetrics { get; } = new();

    internal ReadAfterWriteMetrics ReadAfterWriteMetrics { get; } = new();
```

In the constructor body, enrol all three so `MarkStarted` reaches them:

```csharp
        Metrics.RegisterResettable(ReconnectionMetrics);
        Metrics.RegisterResettable(PollingMetrics);
        Metrics.RegisterResettable(ReadAfterWriteMetrics);
```

Update the `new SessionManager(...)` call at `:118` to pass `PollingMetrics` and `ReadAfterWriteMetrics`.

- [ ] **Step 8: Run the tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category!=Integration"`

Expected: PASS. `PollingMetricsTests` and `ReadAfterWriteManagerTests` construct the metrics directly and are unaffected except where they assert a trip count read through `PollingManager`.

- [ ] **Step 9: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa src/Namotion.Interceptor.OpcUa.Tests
git commit -m "Hoist OPC UA polling and read-after-write metrics above the session (#277)"
```

---

### Task 14: Rewrite `OpcUaClientDiagnostics` and wire the client's liveness

The largest single file change, and the one the whole design is aimed at. `OpcUaClientDiagnostics` loses everything the shared bases now provide and keeps only what is genuinely OPC UA.

**Files:**
- Rewrite: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientDiagnostics.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/SessionManager.cs`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaClientDiagnosticsTests.cs`

**Interfaces:**
- Produces: `OpcUaClientDiagnostics : SourceDiagnostics`, sealed; `ReconnectDiagnostics`, sealed; `PollingDiagnostics` and `ReadAfterWriteDiagnostics` with the nine renamed counters; `OpcUaSubjectClientSource.NotifySessionHealthy()` (internal).

**The nine renames.** Eight cumulative counters omit the `Total` marker, so a reader cannot tell them from gauges. `TotalReads` renames for a different reason: it counts successful reads only (`PollingManager.cs:364-366`), so beside a new `TotalFailedReads` it would read as the sum.

| Today | Becomes |
|---|---|
| `PollingDiagnostics.TotalReads` | `TotalSuccessfulReads` |
| `PollingDiagnostics.FailedReads` | `TotalFailedReads` |
| `PollingDiagnostics.ValueChanges` | `TotalValueChanges` |
| `PollingDiagnostics.SlowPolls` | `TotalSlowPolls` |
| `PollingDiagnostics.CircuitBreakerTrips` | `TotalCircuitBreakerTrips` |
| `ReadAfterWriteDiagnostics.Scheduled` | `TotalScheduledReads` |
| `ReadAfterWriteDiagnostics.Executed` | `TotalExecutedReads` |
| `ReadAfterWriteDiagnostics.Coalesced` | `TotalCoalescedReads` |
| `ReadAfterWriteDiagnostics.Failed` | `TotalFailedReads` |

The `ReadAfterWrite` members name their noun because that block name contains both "read" and "write", so a bare `TotalFailed` there would read as a failed write. `Reconnects` needs no such treatment. `Total` is a prefix throughout, matching the library's own existing usage (`TotalReads`, `TotalAttempts`, `TotalReconnectionAttempts`) with no suffix usage anywhere.

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaClientDiagnosticsTests.cs`:

```csharp
namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaClientDiagnosticsTests
{
    [Fact]
    public void WhenNeverConnected_ThenEveryGetterAnswersWithoutThrowing()
    {
        // Arrange & Act
        using var source = CreateClientSource();
        var diagnostics = source.Diagnostics;

        // Assert
        Assert.False(diagnostics.IsOperational);
        Assert.Null(diagnostics.OperationalChangeTime);
        Assert.Null(diagnostics.LastError);
        Assert.Null(diagnostics.StartTime);
        Assert.False(diagnostics.IsReconnecting);
        Assert.Null(diagnostics.SessionId);
        Assert.Equal(0, diagnostics.SubscriptionCount);
        Assert.Equal(0, diagnostics.MonitoredItemCount);
        Assert.Equal(0, diagnostics.ClaimedPropertyCount);
        Assert.Null(diagnostics.Polling);
        Assert.Null(diagnostics.ReadAfterWrite);
        Assert.Equal(0, diagnostics.Reconnects.TotalAttempts);
        Assert.Null(diagnostics.Reconnects.LastConnectionTime);
    }

    [Fact]
    public void WhenAnErrorIsReported_ThenItSurvivesARecovery()
    {
        // Arrange
        using var source = CreateClientSource();
        var error = new InvalidOperationException("session failed");

        // Act
        source.ReportErrorForTest(error);
        source.NotifySessionHealthy();

        // Assert
        Assert.Same(error, source.Diagnostics.LastError);
        Assert.True(source.Diagnostics.IsOperational);
    }

    [Fact]
    public void WhenTheConnectionIsLost_ThenLivenessFallsAndItsTimestampMoves()
    {
        // Arrange
        using var source = CreateClientSource();
        source.NotifySessionHealthy();
        var upAt = source.Diagnostics.OperationalChangeTime;

        // Act
        WaitForClockTick();
        source.NotifyConnectionLost();

        // Assert
        Assert.False(source.Diagnostics.IsOperational);
        Assert.True(source.Diagnostics.OperationalChangeTime > upAt);
    }

    [Fact]
    public void WhenPropertiesAreClaimed_ThenClaimedPropertyCountFollowsTheOwnershipManager()
    {
        // Arrange
        using var source = CreateClientSource();

        // Act
        source.ClaimPropertyForTest();

        // Assert
        Assert.Equal(1, source.Diagnostics.ClaimedPropertyCount);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaClientDiagnosticsTests"`

Expected: compile failure.

- [ ] **Step 3: Rewrite `OpcUaClientDiagnostics.cs`**

Replace the whole file:

```csharp
using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// What the OPC UA client reports about its session, on top of the shared source diagnostics.
/// </summary>
/// <remarks>
/// <see cref="ConnectorDiagnostics.IsOperational"/> means the session is usable and no reconnection
/// is in progress. It replaces the former <c>IsConnected</c> and carries the same meaning. True does
/// not mean the model is in sync: while the initial load runs the source state is
/// <see cref="Namotion.Interceptor.Connectors.Monitoring.SourceState.Synchronizing"/>. Read the two
/// together to tell a network outage from a connected client still loading. See
/// docs/connectors-monitoring.md.
/// </remarks>
public sealed class OpcUaClientDiagnostics : SourceDiagnostics
{
    private readonly OpcUaSubjectClientSource _source;

    internal OpcUaClientDiagnostics(OpcUaSubjectClientSource source, SourceMetrics metrics)
        : base(metrics)
    {
        _source = source;
        Reconnects = new ReconnectDiagnostics(source.ReconnectionMetrics);
    }

    /// <summary>
    /// Gets a value indicating whether the client is currently attempting to reconnect. A distinct
    /// sub-state of not being operational, not a second spelling of it.
    /// </summary>
    public bool IsReconnecting => _source.SessionManager?.IsReconnecting ?? false;

    /// <summary>
    /// Gets the current session identifier, or <c>null</c> if there is no session.
    /// </summary>
    public string? SessionId => _source.SessionManager?.CurrentSession?.SessionId?.ToString();

    /// <summary>
    /// Gets the number of active OPC UA subscriptions.
    /// </summary>
    public int SubscriptionCount => _source.SessionManager?.Subscriptions.Count ?? 0;

    /// <summary>
    /// Gets the number of monitored items across all subscriptions.
    /// </summary>
    public int MonitoredItemCount => _source.SessionManager?.SubscriptionManager.MonitoredItems.Count ?? 0;

    /// <summary>
    /// Gets the reconnection history.
    /// </summary>
    public ReconnectDiagnostics Reconnects { get; }

    /// <summary>
    /// Gets polling diagnostics, or <c>null</c> when the polling fallback is off.
    /// </summary>
    public PollingDiagnostics? Polling
    {
        get
        {
            var pollingManager = _source.SessionManager?.PollingManager;
            return pollingManager is not null ? new PollingDiagnostics(pollingManager) : null;
        }
    }

    /// <summary>
    /// Gets read-after-write diagnostics, or <c>null</c> when read-after-write is off.
    /// </summary>
    public ReadAfterWriteDiagnostics? ReadAfterWrite
    {
        get
        {
            var manager = _source.SessionManager?.ReadAfterWriteManager;
            return manager is not null ? new ReadAfterWriteDiagnostics(manager) : null;
        }
    }
}

/// <summary>
/// The client's reconnection history. Every counter is monotonic since
/// <see cref="ConnectorDiagnostics.StartTime"/>.
/// </summary>
public sealed class ReconnectDiagnostics
{
    private readonly ReconnectionMetrics _metrics;

    internal ReconnectDiagnostics(ReconnectionMetrics metrics)
    {
        _metrics = metrics;
    }

    /// <summary>
    /// Gets when the client last established a session, or <c>null</c> if it never has. Records a
    /// discrete past event and survives the disconnection that follows it, which is what the
    /// <c>Last</c> prefix means here.
    /// </summary>
    public DateTimeOffset? LastConnectionTime => _metrics.LastConnectedAt;

    /// <summary>
    /// Gets the number of reconnection attempts started. Once all in-flight attempts have resolved,
    /// this equals <see cref="TotalSucceeded"/> + <see cref="TotalFailed"/> + <see cref="TotalAbandoned"/>.
    /// </summary>
    public long TotalAttempts => _metrics.TotalAttempts;

    /// <summary>
    /// Gets the number of attempts that produced a usable session.
    /// </summary>
    public long TotalSucceeded => _metrics.Successful;

    /// <summary>
    /// Gets the number of attempts that ended with an exception.
    /// </summary>
    public long TotalFailed => _metrics.Failed;

    /// <summary>
    /// Gets the number of attempts that completed without an exception but produced an unusable
    /// result: a null session, a failed transfer, a preserved session after a server restart, a
    /// stall reset, or a kill cancellation.
    /// </summary>
    public long TotalAbandoned => _metrics.Abandoned;
}

/// <summary>
/// The polling fallback used for nodes that do not support subscriptions.
/// </summary>
public class PollingDiagnostics
{
    private readonly Polling.PollingManager _pollingManager;

    internal PollingDiagnostics(Polling.PollingManager pollingManager)
    {
        _pollingManager = pollingManager;
    }

    /// <summary>
    /// Gets the number of items currently being polled.
    /// </summary>
    public int ItemCount => _pollingManager.PollingItemCount;

    /// <summary>
    /// Gets the number of reads that succeeded.
    /// </summary>
    public long TotalSuccessfulReads => _pollingManager.TotalReads;

    /// <summary>
    /// Gets the number of reads that failed.
    /// </summary>
    public long TotalFailedReads => _pollingManager.FailedReads;

    /// <summary>
    /// Gets the number of value changes detected.
    /// </summary>
    public long TotalValueChanges => _pollingManager.ValueChanges;

    /// <summary>
    /// Gets the number of polls whose duration exceeded the polling interval.
    /// </summary>
    public long TotalSlowPolls => _pollingManager.SlowPolls;

    /// <summary>
    /// Gets the number of times the circuit breaker has tripped.
    /// </summary>
    public long TotalCircuitBreakerTrips => _pollingManager.CircuitBreakerTrips;

    /// <summary>
    /// Gets whether the circuit breaker is currently open.
    /// </summary>
    public bool IsCircuitBreakerOpen => _pollingManager.IsCircuitOpen;

    /// <summary>
    /// Gets whether the polling loop is currently running. This is a sub-component's own state, not
    /// a second spelling of <see cref="ConnectorDiagnostics.IsOperational"/>, which describes the
    /// connector as a whole.
    /// </summary>
    public bool IsRunning => _pollingManager.IsRunning;
}

/// <summary>
/// The verification reads issued after an outbound write to a discrete property.
/// </summary>
/// <remarks>
/// Every counter here describes a read that follows a write. The block name contains both words, so
/// each member names its noun to keep a failed verification read from reading as a failed write.
/// </remarks>
public class ReadAfterWriteDiagnostics
{
    private readonly ReadAfterWrite.ReadAfterWriteManager _manager;

    internal ReadAfterWriteDiagnostics(ReadAfterWrite.ReadAfterWriteManager manager)
    {
        _manager = manager;
    }

    /// <summary>
    /// Gets the number of pending verification reads.
    /// </summary>
    public int PendingReads => _manager.PendingReadCount;

    /// <summary>
    /// Gets the number of verification reads scheduled.
    /// </summary>
    public long TotalScheduledReads => _manager.Metrics.Scheduled;

    /// <summary>
    /// Gets the number of verification reads executed.
    /// </summary>
    public long TotalExecutedReads => _manager.Metrics.Executed;

    /// <summary>
    /// Gets the number of scheduled verification reads replaced by a subsequent write.
    /// </summary>
    public long TotalCoalescedReads => _manager.Metrics.Coalesced;

    /// <summary>
    /// Gets the number of verification reads that failed.
    /// </summary>
    public long TotalFailedReads => _manager.Metrics.Failed;
}
```

`PendingReads` is new here and replaces the flat `PendingReadAfterWrites` on the client diagnostics, which was the duplication the spec's fourth problem names. `PollingDiagnostics.ItemCount` likewise replaces the flat `PollingItemCount`.

- [ ] **Step 4: Wire the client source**

In `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`:

Split the constructor so the throughput counters reach `base(...)` without touching `this`, following Task 9's shape, and pass them as the two new trailing `SubjectSourceBase` parameters:

```csharp
    public OpcUaSubjectClientSource(/* ...existing parameters... */)
        : this(/* ...existing arguments..., */ new ThroughputCounter(), new ThroughputCounter())
    {
    }

    private OpcUaSubjectClientSource(
        /* ...existing parameters..., */
        ThroughputCounter incoming,
        ThroughputCounter outgoing)
        : base(context, logger, bufferTime, retryTime, writeRetryQueueSize, incoming, outgoing)
    {
        IncomingThroughput = incoming;
        OutgoingThroughput = outgoing;

        // ...existing body up to and including the _ownership assignment...

        Metrics.RegisterClaimedProperties(() => _ownership.Count);
        Metrics.RegisterResettable(ReconnectionMetrics);
        Metrics.RegisterResettable(PollingMetrics);
        Metrics.RegisterResettable(ReadAfterWriteMetrics);

        Diagnostics = new OpcUaClientDiagnostics(this, Metrics);
    }

    /// <inheritdoc cref="SubjectSourceBase.Diagnostics" />
    public override OpcUaClientDiagnostics Diagnostics { get; }
```

Delete `ClearLastError` (`:49`) and the `_lastError` field, and replace the internal `LastError` property with nothing: it is now `Diagnostics.LastError`. Route every write that currently sets `_lastError` through `Metrics.ReportError(exception)`.

Add the liveness forwarder beside the existing `NotifyConnectionLost` (`:56`):

```csharp
    /// <summary>
    /// Forwards a healthy-session report from <c>SessionManager</c>, which lives outside this
    /// class's inheritance hierarchy and so cannot reach the protected metrics directly. Same
    /// pattern as <see cref="NotifyConnectionLost"/>.
    /// </summary>
    internal void NotifySessionHealthy() => Metrics.MarkOperational();
```

and make `NotifyConnectionLost` mark liveness as well as transitioning state:

```csharp
    internal void NotifyConnectionLost()
    {
        Metrics.MarkNotOperational();
        ReportConnectionLost();
    }
```

Call `NotifySessionHealthy()` from `HandleHealthySessionAsync` (`:361`) on its success path.

- [ ] **Step 5: Remove the clearing paths in `SessionManager`**

In `src/Namotion.Interceptor.OpcUa/Client/SessionManager.cs`, delete the `ClearLastError` call at `:413` and the two null-writes at `:156` and `:505`. `LastError` is sticky now: a cleared error erases the only evidence a transient fault happened. Replace each site's error handling with `_source.ReportError(exception)` where it currently assigns, adding an internal forwarder on the source if `Metrics` is not reachable from `SessionManager`.

- [ ] **Step 6: Fix the OPC UA test fallout**

Run: `grep -rn --include='*.cs' "IsConnected\|LastConnectedAt\|TotalReconnectionAttempts\|SuccessfulReconnections\|FailedReconnections\|AbandonedReconnections\|IncomingChangesPerSecond\|OutgoingChangesPerSecond\|PendingReadAfterWrites\|PollingItemCount\|PendingWriteCount" src/Namotion.Interceptor.OpcUa.Tests src/Namotion.Interceptor.ConnectorTester`

Roughly 40 reads across `OpcUaReconnectionTests`, `OpcUaStallDetectionTests`, `OpcUaConcurrencyTests`, `OpcUaReadWriteTests`, `OpcUaServerSelfWriteTests` and `SelfEchoReproTests`. Translate each:

| Old | New |
|---|---|
| `Diagnostics.IsConnected` | `Diagnostics.IsOperational` |
| `Diagnostics.LastConnectedAt` | `Diagnostics.Reconnects.LastConnectionTime` |
| `Diagnostics.TotalReconnectionAttempts` | `Diagnostics.Reconnects.TotalAttempts` |
| `Diagnostics.SuccessfulReconnections` | `Diagnostics.Reconnects.TotalSucceeded` |
| `Diagnostics.FailedReconnections` | `Diagnostics.Reconnects.TotalFailed` |
| `Diagnostics.AbandonedReconnections` | `Diagnostics.Reconnects.TotalAbandoned` |
| `Diagnostics.IncomingChangesPerSecond` | `Diagnostics.Throughput.IncomingPerSecond` |
| `Diagnostics.OutgoingChangesPerSecond` | `Diagnostics.Throughput.OutgoingPerSecond` |
| `Diagnostics.PendingReadAfterWrites` | `Diagnostics.ReadAfterWrite?.PendingReads` |
| `Diagnostics.PollingItemCount` | `Diagnostics.Polling?.ItemCount ?? 0` |
| `Diagnostics.PendingWriteCount` | `Diagnostics.OutboundRetries.Depth` |

The throughput assertions become nullable comparisons: `Assert.Equal(0.0, diagnostics.Throughput.IncomingPerSecond)` still works because the OPC UA connectors instrument both directions, so the value is never null there.

- [ ] **Step 7: Run the tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category!=Integration"`

Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa src/Namotion.Interceptor.OpcUa.Tests
git commit -m "Rewrite the OPC UA client diagnostics onto the shared model (#277)"
```

---

### Task 15: MQTT and WebSocket client liveness

Both client sources inherit `SourceDiagnostics` unchanged and need only their liveness transitions and their claimed-property registration. Neither measures throughput, so both rates stay null.

**Files:**
- Modify: `src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs`
- Test: `src/Namotion.Interceptor.Mqtt.Tests/Client/MqttClientDiagnosticsTests.cs`
- Test: `src/Namotion.Interceptor.WebSocket.Tests/Client/WebSocketClientDiagnosticsTests.cs`

**Interfaces:**
- Consumes: `SubjectSourceBase.Metrics`, `SourceMetrics.RegisterClaimedProperties`.
- Produces: no new types. Neither connector gets its own diagnostics class, because neither has a member to add beyond what `SourceDiagnostics` already provides.

- [ ] **Step 1: Write the failing tests**

Create both test files with the same two cases, adjusted for each connector's construction:

```csharp
    [Fact]
    public void WhenNeverConnected_ThenTheSourceReportsNotOperationalAndNoThroughput()
    {
        // Arrange & Act
        using var source = CreateClientSource();

        // Assert
        Assert.False(source.Diagnostics.IsOperational);
        Assert.Null(source.Diagnostics.Throughput.IncomingPerSecond);
        Assert.Null(source.Diagnostics.Throughput.OutgoingPerSecond);
        Assert.Equal(0, source.Diagnostics.ClaimedPropertyCount);
    }

    [Fact]
    public void WhenPropertiesAreClaimed_ThenClaimedPropertyCountFollowsTheOwnershipManager()
    {
        // Arrange
        using var source = CreateClientSource();

        // Act
        source.ClaimPropertyForTest();

        // Assert
        Assert.Equal(1, source.Diagnostics.ClaimedPropertyCount);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Mqtt.Tests src/Namotion.Interceptor.WebSocket.Tests --filter "FullyQualifiedName~DiagnosticsTests"`

Expected: FAIL on `ClaimedPropertyCount`, which is 0 until the provider is registered.

- [ ] **Step 3: Wire the MQTT client**

In `src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs`, register the gauge in the constructor after the ownership manager is created (`:63`):

```csharp
        Metrics.RegisterClaimedProperties(() => _ownership.Count);
```

Mark liveness at the two seams: after the successful connect at `:114`, immediately before the existing log call:

```csharp
            Metrics.MarkOperational();
            _logger.LogInformation("Connected to MQTT broker successfully.");
```

and at the top of `OnDisconnectedAsync` (`:531`):

```csharp
        Metrics.MarkNotOperational();
```

- [ ] **Step 4: Wire the WebSocket client**

In `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs`, register the gauge in the constructor after the ownership manager is created (`:71`):

```csharp
        Metrics.RegisterClaimedProperties(() => _ownership.Count);
```

Mark liveness after the welcome frame is accepted at `:265`, immediately before the existing log call:

```csharp
            Metrics.MarkOperational();
            _logger.LogInformation("Connected to WebSocket server (sequence: {Sequence})", welcome.Sequence);
```

and where the receive loop at `:413` exits, in its enclosing `finally`:

```csharp
            Metrics.MarkNotOperational();
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test src/Namotion.Interceptor.Mqtt.Tests src/Namotion.Interceptor.WebSocket.Tests --filter "Category!=Integration"`

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Mqtt src/Namotion.Interceptor.WebSocket src/Namotion.Interceptor.Mqtt.Tests src/Namotion.Interceptor.WebSocket.Tests
git commit -m "Report liveness and claimed properties from the MQTT and WebSocket clients (#277)"
```

---

### Task 16: HomeBlaze consumers and API snapshots

`HomeBlaze` is the in-repo consumer that proves the new surface is usable and is where a naming mistake shows up as a broken device page rather than a compile error.

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.OpcUa/OpcUaClient.cs:221-227`, `:311-318`
- Modify: `src/HomeBlaze/HomeBlaze.OpcUa/OpcUaServer.cs:191-193`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Modify: `src/Namotion.Interceptor.Mqtt.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Create: `src/Namotion.Interceptor.WebSocket.Tests/VerifyChecksTests.cs` and its `.verified.txt`

- [ ] **Step 1: Update the HomeBlaze OPC UA client**

In `src/HomeBlaze/HomeBlaze.OpcUa/OpcUaClient.cs`, translate the seven reads at `:221-227` and the null-out block at `:311-318` using the mapping table from Task 14 Step 6.

`:225` currently surfaces `IsConnected` as a device state property. It becomes `IsOperational`, which carries the same meaning by construction: the operational predicate chosen for this connector is "session usable and not reconnecting", which is what `IsConnected` reported. The device page therefore shows the same thing.

The null-out block at `:311-318` sets each diagnostics-derived property to null when the client is not configured. Keep that shape and only rename the members.

- [ ] **Step 2: Update the HomeBlaze OPC UA server**

In `src/HomeBlaze/HomeBlaze.OpcUa/OpcUaServer.cs:191-193`, the throughput pair becomes `Diagnostics.Throughput.IncomingPerSecond` and `Diagnostics.Throughput.OutgoingPerSecond`, both `double?`. `ActiveSessionCount` is unchanged.

If either HomeBlaze property is typed `double`, change it to `double?` rather than coalescing to 0: the null means "not measured", and coalescing it to 0 would show a real rate of zero where there is no measurement. Both OPC UA connectors do measure, so the value is never actually null here, but the type should say what the API says.

- [ ] **Step 3: Build HomeBlaze**

Run: `dotnet build src/Namotion.Interceptor.slnx`

Expected: PASS.

- [ ] **Step 4: Regenerate the API snapshots**

Run each snapshot test and accept the new output by replacing the `.verified.txt` with the produced `.received.txt`:

```bash
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~VerifyChecksTests.PublicApi"
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~VerifyChecksTests.PublicApi"
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Mqtt.Tests --filter "FullyQualifiedName~VerifyChecksTests.PublicApi"
```

**Read each diff before accepting it.** The snapshot is the last place a member that should not be public can be caught. Confirm that `ConnectorMetrics`, `SourceMetrics` and `QueueMetrics` appear with only their mutators public, that no `*Metrics` type is reachable from `ConnectorDiagnostics` or `SourceDiagnostics`, and that every removed member listed in the spec's breaking-changes section is actually gone.

- [ ] **Step 5: Add the missing WebSocket snapshot**

`WebSocketSubjectServer` is public but the WebSocket test project has no `VerifyChecksTests.PublicApi`. This change moves that surface, so close the gap here. Copy the test from `src/Namotion.Interceptor.Mqtt.Tests/VerifyChecksTests.cs`, adjust the assembly it points at, run it once with `DiffEngine_Disabled=true`, and check in the produced `.received.txt` as the first `.verified.txt`.

- [ ] **Step 6: Run everything**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`

Expected: PASS.

Run the connector integration suites, which are the only place the liveness transitions are actually exercised:

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests
dotnet test src/Namotion.Interceptor.Mqtt.Tests
dotnet test src/Namotion.Interceptor.WebSocket.Tests
```

Expected: PASS. The OPC UA integration tests need port 4840 free; stop any local OPC UA host first.

- [ ] **Step 7: Commit**

```bash
git add src/HomeBlaze src/Namotion.Interceptor.Connectors.Tests src/Namotion.Interceptor.OpcUa.Tests src/Namotion.Interceptor.Mqtt.Tests src/Namotion.Interceptor.WebSocket.Tests
git commit -m "Update HomeBlaze consumers and API snapshots for the new diagnostics (#277)"
```

---

### Task 17: Documentation

Shipping `MqttServerDiagnostics` and `WebSocketServerDiagnostics` undocumented would leave the headline problem half-solved, since "diagnostics exist only for OPC UA" is the first consequence the spec lists.

**Files:**
- Modify: `docs/connectors.md:271`, `:272`, `:277`, `:783`, `:785`
- Modify: `docs/connectors-monitoring.md:160`
- Modify: `docs/connectors-opcua-client.md:648`, `:650`, `:746`, `:747`, `:750`, `:769`, `:774`, `:775`, `:776`, `:782`, `:784`
- Modify: `docs/connectors-opcua-server.md:259`
- Modify: `docs/connectors-opcua.md:48`, `:76`
- Modify: `docs/connectors-mqtt.md` (new diagnostics section)
- Modify: `docs/connectors-websocket.md` (new diagnostics section)
- Modify: `src/HomeBlaze/HomeBlaze/Data/Docs/architecture/design/observability.md:57-58`

- [ ] **Step 1: Update the shared connector docs**

`docs/connectors.md:277` documents implementing `ISubjectSource` directly as supported. Add that a direct implementer now needs **two** diagnostics members, because covariant implicit interface implementation does not exist in C#:

```csharp
public SourceDiagnostics Diagnostics { get; } = new(new SourceMetrics());

ConnectorDiagnostics ISubjectConnector.Diagnostics => Diagnostics;
```

`docs/connectors.md:783` documents that writes to properties a source has not claimed yet are discarded. Add that this path is **not** counted by `OutboundRetries.TotalDropped`, because counting it needs an ownership-aware accumulator, and that it is tracked as a follow-up.

- [ ] **Step 2: Update the monitoring doc**

`docs/connectors-monitoring.md:160` describes `LastSynchronizedAt` and `PendingWriteCount`. Replace both. State plainly what changed and what was given up:

- `StateChangeTime` reads with `State`: `Synchronized` plus T means in sync since T, `Synchronizing` plus T means stale since T.
- Nothing now reports when a currently stale source was last in sync. That is a deliberate trade: the stale-duration question is the one operators ask during an incident.
- `PendingWriteCount` is now `Diagnostics.OutboundRetries.Depth`.

Add a short section separating the two surfaces: `State` answers "can I trust these values" and drives program behaviour through `WaitForSynchronizationAsync`; `Diagnostics` answers "what is the transport doing" and gates nothing.

- [ ] **Step 3: Update the OPC UA docs**

Rewrite the diagnostics sections in `connectors-opcua-client.md`, `connectors-opcua-server.md` and `connectors-opcua.md` against the new member tree. Every renamed member in the tables from Task 14 appears in these files.

- [ ] **Step 4: Write the MQTT and WebSocket diagnostics sections**

Add a diagnostics section to `docs/connectors-mqtt.md` and `docs/connectors-websocket.md` covering: what `IsOperational` means for that connector, the three buffers and how to read them, that neither measures throughput today, and the connector's own additional members.

State the buffer relationship once in each:

- `OutboundChanges` growing means changes are produced faster than they flush.
- `OutboundRetries` growing means the far end is rejecting writes.
- `InboundBuffer` growing means an initial load is still in progress.

- [ ] **Step 5: Update the HomeBlaze observability doc**

`src/HomeBlaze/HomeBlaze/Data/Docs/architecture/design/observability.md:57-58` documents both the OPC UA client and server surfaces. Update both to the new paths.

- [ ] **Step 6: Delete the spec's temporary status**

The design spec at `docs/superpowers/specs/2026-08-11-connector-diagnostics-design.md` is temporary. Its permanent content now lives in the docs above. Leave the file in place for the PR's reviewers and note in the PR description that the permanent documentation has been updated to the implemented design.

- [ ] **Step 7: Commit**

```bash
git add docs src/HomeBlaze/HomeBlaze/Data/Docs
git commit -m "Document the shared connector diagnostics model (#277)"
```

---

## The member tree for the PR description

The spec requires the PR description to carry the full tree. It is what made three naming defects visible during design where prose surfaced none of them. Mark gauges and `Total` counters, and keep the base types first.

```
ISubjectConnector.Diagnostics -> ConnectorDiagnostics
  IsOperational                     bool           gauge, per-connector predicate
  OperationalChangeTime             DateTimeOffset?  moves with IsOperational
  LastError                         Exception?     sticky, cleared only by restart
  StartTime                         DateTimeOffset?  epoch for every Total below
  Throughput                        ThroughputDiagnostics
    IncomingPerSecond               double?        null = not measured
    OutgoingPerSecond               double?        null = not measured
  OutboundChanges                   QueueDiagnostics
    Depth                           int            gauge
    Capacity                        int?           null = unbounded, 0 = disabled
    TotalDropped                    long           Total

ISubjectSource.Diagnostics -> SourceDiagnostics : ConnectorDiagnostics
  ClaimedPropertyCount              int            gauge
  OutboundRetries                   QueueDiagnostics
  InboundBuffer                     QueueDiagnostics

OpcUaClientDiagnostics : SourceDiagnostics
  IsReconnecting                    bool           gauge
  SessionId                         string?
  SubscriptionCount                 int            gauge
  MonitoredItemCount                int            gauge
  Reconnects                        ReconnectDiagnostics
    LastConnectionTime              DateTimeOffset?  survives the disconnect
    TotalAttempts                   long           Total
    TotalSucceeded                  long           Total
    TotalFailed                     long           Total
    TotalAbandoned                  long           Total
  Polling                           PollingDiagnostics?   null when polling is off
    ItemCount                       int            gauge
    TotalSuccessfulReads            long           Total
    TotalFailedReads                long           Total
    TotalValueChanges               long           Total
    TotalSlowPolls                  long           Total
    TotalCircuitBreakerTrips        long           Total
    IsCircuitBreakerOpen            bool           gauge
    IsRunning                       bool           gauge, sub-component state
  ReadAfterWrite                    ReadAfterWriteDiagnostics?  null when off
    PendingReads                    int            gauge
    TotalScheduledReads             long           Total
    TotalExecutedReads              long           Total
    TotalCoalescedReads             long           Total
    TotalFailedReads                long           Total

OpcUaServerDiagnostics : ConnectorDiagnostics
  ActiveSessionCount                int            gauge
  ConsecutiveFailures               int            gauge, resets on a successful start

MqttServerDiagnostics : ConnectorDiagnostics
  ConnectedClientCount              int            gauge

WebSocketServerDiagnostics : ConnectorDiagnostics
  ConnectionCount                   int            gauge
  CurrentSequence                   long           position, not a count

MqttSubjectClientSource.Diagnostics       -> SourceDiagnostics (no additions)
WebSocketSubjectClientSource.Diagnostics  -> SourceDiagnostics (no additions)
```

## Self-review

**Spec coverage.** Every section of the spec maps to a task: the problem's five consequences to Tasks 9 through 15; drop counting to Task 8; the type model to Tasks 1 through 3; the abstract-`Diagnostics` mechanics to Tasks 6 and 12; the sealed `ExecuteAsync` to Task 4; atomic state to Tasks 2 and 7; naming and the `Total` convention to Task 14; the hoist to Task 13; ownership and lifetime to Tasks 1, 5 and 8; production changes to Tasks 8 through 15; breaking changes to Tasks 7, 9, 11, 14 and 16; error handling to Tasks 2 and 4; testing to every task's first step; documentation obligations to Task 17 and the tree above.

**Not covered, and deliberately so.** The spec's follow-ups stay follow-ups: throughput for the MQTT and WebSocket servers, counting the unclaimed-property discard, making `WebSocketSubjectChangeProcessor` its own connector, and bounding the queues (#281, #352).

**Known risks the plan carries rather than resolves.** Two facts are stated in the tasks that use them and are worth naming once more. `OutboundRetries.TotalDropped` under-reports when a source is configured with `writeRetryQueueSize: 0`, because the dominant loss path in that configuration is the unfiltered drain, which cannot be attributed to one source. And `StateChangeTime` loses the ability to say when a currently stale source was last in sync.

**Line citations drift.** Every `file:line` in this plan was verified against the tree on 2026-08-12. Re-grep rather than trusting a line number if the surrounding code does not match what the step describes.




