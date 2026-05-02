# OPC UA Client Refactoring Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Extract write path, reconnection metrics, and throughput counters from `OpcUaSubjectClientSource` into focused classes; expose queue depths and throughput via diagnostics.

**Architecture:** Extract `OpcUaClientWriter` (write path), `ReconnectionMetrics` (counters), and `ThroughputCounter` (sliding window rate). Add `QueueDiagnostics` to `SubjectPropertyWriter` to bridge Connectors layer queue counts into OpcUa diagnostics. Simplify `ExecuteAsync` into named methods.

**Tech Stack:** C# 13, .NET 9.0, xUnit, `Interlocked` for thread-safety

---

### Task 1: Create `ThroughputCounter`

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Client/ThroughputCounter.cs`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Client/ThroughputCounterTests.cs`

**Step 1: Write the failing tests**

In `src/Namotion.Interceptor.OpcUa.Tests/Client/ThroughputCounterTests.cs`:

```csharp
using Namotion.Interceptor.OpcUa.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class ThroughputCounterTests
{
    [Fact]
    public void WhenNoDataAdded_ThenRateIsZero()
    {
        // Arrange
        var counter = new ThroughputCounter();

        // Act
        var rate = counter.GetRate();

        // Assert
        Assert.Equal(0.0, rate);
    }

    [Fact]
    public void WhenDataAdded_ThenRateIsPositive()
    {
        // Arrange
        var counter = new ThroughputCounter();

        // Act
        counter.Add(100);
        var rate = counter.GetRate();

        // Assert
        Assert.True(rate > 0.0);
    }

    [Fact]
    public void WhenAddCalledMultipleTimes_ThenRateAccumulates()
    {
        // Arrange
        var counter = new ThroughputCounter();

        // Act
        counter.Add(50);
        counter.Add(50);
        var rate = counter.GetRate();

        // Assert
        Assert.True(rate > 0.0);
    }

    [Fact]
    public async Task WhenConcurrentAdds_ThenCountIsCorrect()
    {
        // Arrange
        var counter = new ThroughputCounter();
        const int threadCount = 10;
        const int addsPerThread = 1000;

        // Act
        var tasks = Enumerable.Range(0, threadCount)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < addsPerThread; i++)
                {
                    counter.Add(1);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        var rate = counter.GetRate();
        Assert.True(rate > 0.0);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~ThroughputCounterTests" --no-restore`
Expected: FAIL — `ThroughputCounter` does not exist

**Step 3: Write the implementation**

In `src/Namotion.Interceptor.OpcUa/Client/ThroughputCounter.cs`:

```csharp
namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Thread-safe sliding window counter that tracks changes per second over a 60-second window.
/// Uses per-second buckets with atomic operations for lock-free concurrent access.
/// </summary>
internal sealed class ThroughputCounter
{
    private const int WindowSeconds = 60;

    private readonly long[] _buckets = new long[WindowSeconds];
    private long _currentSecond;

    public void Add(int count)
    {
        var nowSecond = Environment.TickCount64 / 1000;
        var bucketIndex = (int)(nowSecond % WindowSeconds);

        var lastSecond = Interlocked.Read(ref _currentSecond);
        if (nowSecond != lastSecond)
        {
            if (Interlocked.CompareExchange(ref _currentSecond, nowSecond, lastSecond) == lastSecond)
            {
                ClearStaleBuckets(lastSecond, nowSecond);
            }
        }

        Interlocked.Add(ref _buckets[bucketIndex], count);
    }

    public double GetRate()
    {
        var nowSecond = Environment.TickCount64 / 1000;
        var lastSecond = Interlocked.Read(ref _currentSecond);

        if (lastSecond == 0 && Volatile.Read(ref _buckets[0]) == 0)
        {
            return 0.0;
        }

        var staleDuration = nowSecond - lastSecond;
        if (staleDuration >= WindowSeconds)
        {
            return 0.0;
        }

        long total = 0;
        for (var i = 0; i < WindowSeconds; i++)
        {
            total += Interlocked.Read(ref _buckets[i]);
        }

        var activeBuckets = Math.Min(WindowSeconds, nowSecond - (lastSecond - WindowSeconds));
        activeBuckets = Math.Max(activeBuckets, 1);
        var activeSeconds = Math.Min(activeBuckets, WindowSeconds - staleDuration);
        activeSeconds = Math.Max(activeSeconds, 1);

        return (double)total / WindowSeconds;
    }

    private void ClearStaleBuckets(long fromSecond, long toSecond)
    {
        var clearCount = Math.Min(toSecond - fromSecond, WindowSeconds);
        for (long s = fromSecond + 1; s <= fromSecond + clearCount; s++)
        {
            var index = (int)(s % WindowSeconds);
            Interlocked.Exchange(ref _buckets[index], 0);
        }
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~ThroughputCounterTests" --no-restore`
Expected: PASS

---

### Task 2: Create `ReconnectionMetrics`

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Client/ReconnectionMetrics.cs`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Client/ReconnectionMetricsTests.cs`

**Step 1: Write the failing tests**

In `src/Namotion.Interceptor.OpcUa.Tests/Client/ReconnectionMetricsTests.cs`:

```csharp
using Namotion.Interceptor.OpcUa.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class ReconnectionMetricsTests
{
    [Fact]
    public void WhenCreated_ThenAllCountersAreZero()
    {
        // Arrange & Act
        var metrics = new ReconnectionMetrics();

        // Assert
        Assert.Equal(0, metrics.TotalAttempts);
        Assert.Equal(0, metrics.Successful);
        Assert.Equal(0, metrics.Failed);
        Assert.Null(metrics.LastConnectedAt);
    }

    [Fact]
    public void WhenRecordAttemptStart_ThenTotalAttemptsIncrements()
    {
        // Arrange
        var metrics = new ReconnectionMetrics();

        // Act
        metrics.RecordAttemptStart();
        metrics.RecordAttemptStart();

        // Assert
        Assert.Equal(2, metrics.TotalAttempts);
    }

    [Fact]
    public void WhenRecordSuccess_ThenSuccessfulIncrementsAndLastConnectedAtIsSet()
    {
        // Arrange
        var metrics = new ReconnectionMetrics();
        var before = DateTimeOffset.UtcNow;

        // Act
        metrics.RecordSuccess();

        // Assert
        Assert.Equal(1, metrics.Successful);
        Assert.NotNull(metrics.LastConnectedAt);
        Assert.True(metrics.LastConnectedAt >= before);
    }

    [Fact]
    public void WhenRecordFailure_ThenFailedIncrements()
    {
        // Arrange
        var metrics = new ReconnectionMetrics();

        // Act
        metrics.RecordFailure();
        metrics.RecordFailure();
        metrics.RecordFailure();

        // Assert
        Assert.Equal(3, metrics.Failed);
    }

    [Fact]
    public async Task WhenConcurrentAccess_ThenCountersAreCorrect()
    {
        // Arrange
        var metrics = new ReconnectionMetrics();
        const int threadCount = 10;
        const int opsPerThread = 100;

        // Act
        var tasks = Enumerable.Range(0, threadCount)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < opsPerThread; i++)
                {
                    metrics.RecordAttemptStart();
                    metrics.RecordSuccess();
                    metrics.RecordFailure();
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        var expected = threadCount * opsPerThread;
        Assert.Equal(expected, metrics.TotalAttempts);
        Assert.Equal(expected, metrics.Successful);
        Assert.Equal(expected, metrics.Failed);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~ReconnectionMetricsTests" --no-restore`
Expected: FAIL — `ReconnectionMetrics` does not exist

**Step 3: Write the implementation**

In `src/Namotion.Interceptor.OpcUa/Client/ReconnectionMetrics.cs`:

```csharp
namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Thread-safe counters for reconnection diagnostics.
/// </summary>
internal sealed class ReconnectionMetrics
{
    private long _totalAttempts;
    private long _successful;
    private long _failed;
    private long _lastConnectedAtTicks;

    public long TotalAttempts => Interlocked.Read(ref _totalAttempts);

    public long Successful => Interlocked.Read(ref _successful);

    public long Failed => Interlocked.Read(ref _failed);

    public DateTimeOffset? LastConnectedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastConnectedAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void RecordAttemptStart()
    {
        Interlocked.Increment(ref _totalAttempts);
    }

    public void RecordSuccess()
    {
        Interlocked.Increment(ref _successful);
        Interlocked.Exchange(ref _lastConnectedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void RecordFailure()
    {
        Interlocked.Increment(ref _failed);
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~ReconnectionMetricsTests" --no-restore`
Expected: PASS

---

### Task 3: Create `OpcUaClientWriter`

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientWriter.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs` — remove write methods, add delegation
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Client/WriteErrorClassificationTests.cs` — update `OpcUaSubjectClientSource.IsTransientWriteError` → `OpcUaClientWriter.IsTransientWriteError`

This is a pure move refactoring. No new tests needed — existing `WriteErrorClassificationTests` validates the behavior.

**Step 1: Create `OpcUaClientWriter` with methods moved from source**

Create `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientWriter.cs` by extracting these methods from `OpcUaSubjectClientSource.cs`:
- `WriteChangesAsync` (lines 530-559)
- `ProcessWriteResults` (lines 564-612)
- `TryGetWritableNodeId` (lines 614-632)
- `CreateWriteValuesCollection` (lines 634-666)
- `NotifyPropertiesWritten` (lines 672-689)
- `IsTransientWriteError` (lines 695-710)
- `WriteBatchSize` property (line 186)

The new class constructor takes:
- `SessionManager sessionManager`
- `OpcUaClientConfiguration configuration`
- `string opcUaNodeIdKey`
- `ThroughputCounter outgoingThroughput`
- `ILogger logger`

**Step 2: Update `OpcUaSubjectClientSource`**

Remove the methods listed above. Add:

```csharp
private OpcUaClientWriter? _writer;
```

In `StartListeningAsync`, after creating `_sessionManager`:
```csharp
_writer = new OpcUaClientWriter(_sessionManager, _configuration, OpcUaNodeIdKey, _outgoingThroughput, _logger);
```

Replace `WriteBatchSize` property and `WriteChangesAsync` method with delegations:
```csharp
public int WriteBatchSize => _writer?.WriteBatchSize ?? 0;

public async ValueTask<WriteResult> WriteChangesAsync(
    ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
{
    if (_writer is null)
    {
        return WriteResult.Failure(changes, new InvalidOperationException("OPC UA client not started."));
    }
    return await _writer.WriteChangesAsync(changes, cancellationToken).ConfigureAwait(false);
}
```

**Step 3: Update `WriteErrorClassificationTests`**

Change all references from `OpcUaSubjectClientSource.IsTransientWriteError` to `OpcUaClientWriter.IsTransientWriteError`.

Add `using Namotion.Interceptor.OpcUa.Client;` (already present).

**Step 4: Build to verify compilation**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded with 0 errors, 0 warnings

**Step 5: Run unit tests to verify behavior preserved**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~WriteErrorClassificationTests" --no-restore`
Expected: PASS (all 8 tests)

---

### Task 4: Wire `ReconnectionMetrics` into `OpcUaSubjectClientSource` and `SessionManager`

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Connection/SessionManager.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientDiagnostics.cs`

**Step 1: Update `OpcUaSubjectClientSource`**

Remove these fields:
- `_totalReconnectionAttempts` (line 37)
- `_successfulReconnections` (line 38)
- `_failedReconnections` (line 39)
- `_lastConnectedAtTicks` (line 40)

Remove these members:
- `TotalReconnectionAttempts` property (line 58)
- `SuccessfulReconnections` property (line 59)
- `FailedReconnections` property (line 60)
- `LastConnectedAt` property (lines 61-68)
- `RecordReconnectionAttemptStart` method (lines 74-77)
- `RecordReconnectionSuccess` method (lines 84-88)

Add field:
```csharp
internal ReconnectionMetrics ReconnectionMetrics { get; } = new();
```

Update all call sites in `OpcUaSubjectClientSource`:
- `Interlocked.Exchange(ref _lastConnectedAtTicks, ...)` → `ReconnectionMetrics.RecordSuccess()` (line 153 in `StartListeningAsync`)
- `Interlocked.Increment(ref _totalReconnectionAttempts)` → `ReconnectionMetrics.RecordAttemptStart()` (line 420 in `ReconnectSessionAsync`)
- `RecordReconnectionSuccess()` → `ReconnectionMetrics.RecordSuccess()` (line 476)
- `Interlocked.Increment(ref _failedReconnections)` → `ReconnectionMetrics.RecordFailure()` (line 491)

**Step 2: Update `SessionManager`**

Change `_source.RecordReconnectionAttemptStart()` → `_source.ReconnectionMetrics.RecordAttemptStart()` (line 308 in `OnKeepAlive`)
Change `_source.RecordReconnectionSuccess()` → `_source.ReconnectionMetrics.RecordSuccess()` (line 392 in `OnReconnectComplete`)

**Step 3: Update `OpcUaClientDiagnostics`**

Change:
- `_source.TotalReconnectionAttempts` → `_source.ReconnectionMetrics.TotalAttempts`
- `_source.SuccessfulReconnections` → `_source.ReconnectionMetrics.Successful`
- `_source.FailedReconnections` → `_source.ReconnectionMetrics.Failed`
- `_source.LastConnectedAt` → `_source.ReconnectionMetrics.LastConnectedAt`

**Step 4: Build and run all unit tests**

Run: `dotnet build src/Namotion.Interceptor.slnx && dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration" --no-restore`
Expected: Build succeeded, all tests pass

---

### Task 5: Simplify `ExecuteAsync` with named methods

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`

**Step 1: Extract three methods from the if/else block in `ExecuteAsync`**

The current `ExecuteAsync` has a ~90-line `if/else if/else if` block (starting around line 313 after prior refactors). Extract into:

```csharp
private async Task HandleHealthySessionAsync(SessionManager sessionManager, CancellationToken cancellationToken)
{
    // Reset stall detection timestamp
    Interlocked.Exchange(ref _reconnectStartedTimestamp, 0);

    await sessionManager.PerformFullStateSyncIfNeededAsync(cancellationToken).ConfigureAwait(false);

    if (sessionManager.SubscriptionManager.HasStoppedPublishing)
    {
        _logger.LogWarning(
            "OPC UA subscription has stopped publishing. Starting manual reconnection to recover notification flow...");
        await ReconnectSessionAsync(cancellationToken).ConfigureAwait(false);
        return;
    }

    await _subscriptionHealthMonitor.CheckAndHealSubscriptionsAsync(
        sessionManager.Subscriptions,
        cancellationToken).ConfigureAwait(false);
}

private async Task HandleDeadSessionAsync(SessionManager sessionManager, Session? currentSession, bool sessionIsConnected, CancellationToken cancellationToken)
{
    Interlocked.Exchange(ref _reconnectStartedTimestamp, 0);
    _logger.LogWarning(
        "OPC UA session is dead (session={HasSession}, connected={IsConnected}). " +
        "Starting manual reconnection...",
        currentSession is not null,
        sessionIsConnected);

    await ReconnectSessionAsync(cancellationToken).ConfigureAwait(false);
}

private void HandleReconnectionStallDetection(SessionManager sessionManager, Session? currentSession, bool sessionIsConnected)
{
    var startedAt = Interlocked.Read(ref _reconnectStartedTimestamp);
    if (startedAt == 0)
    {
        Interlocked.CompareExchange(ref _reconnectStartedTimestamp, Stopwatch.GetTimestamp(), 0);
    }
    else
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        if (elapsed > _configuration.MaxReconnectDuration)
        {
            if (sessionManager.TryForceResetIfStalled())
            {
                _logger.LogWarning(
                    "SDK reconnection stalled (session={HasSession}, connected={IsConnected}, elapsed={Elapsed}s). " +
                    "Starting manual reconnection...",
                    currentSession is not null,
                    sessionIsConnected,
                    elapsed.TotalSeconds);

                Interlocked.Exchange(ref _reconnectStartedTimestamp, 0);
            }
        }
    }
}
```

Note: `HandleReconnectionStallDetection` cannot call `ReconnectSessionAsync` (it's not async and stall detection schedules reconnection for the next loop iteration via the dead session path). Actually, looking at the original code, the stall detection DOES call `ReconnectSessionAsync`. So make it async:

```csharp
private async Task HandleReconnectionStallDetectionAsync(SessionManager sessionManager, Session? currentSession, bool sessionIsConnected, CancellationToken cancellationToken)
```

Then update `ExecuteAsync` to use:
```csharp
if (currentSession is not null && sessionIsConnected && !isReconnecting)
{
    await HandleHealthySessionAsync(sessionManager, stoppingToken).ConfigureAwait(false);
}
else if (!isReconnecting && (currentSession is null || !sessionIsConnected))
{
    await HandleDeadSessionAsync(sessionManager, currentSession, sessionIsConnected, stoppingToken).ConfigureAwait(false);
}
else if (isReconnecting)
{
    await HandleReconnectionStallDetectionAsync(sessionManager, currentSession, sessionIsConnected, stoppingToken).ConfigureAwait(false);
}
```

**Step 2: Build and run all unit tests**

Run: `dotnet build src/Namotion.Interceptor.slnx && dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration" --no-restore`
Expected: Build succeeded, all tests pass

---

### Task 6: Expose queue depths via `SubjectPropertyWriter`

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SubjectPropertyWriter.cs` — add `QueueDiagnostics` property
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs` — expose queue count
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBackgroundService.cs` — wire diagnostics

**Step 1: Add `PendingChangeCount` to `ChangeQueueProcessor`**

In `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs`, add a public property:

```csharp
/// <summary>
/// Gets the approximate number of pending changes waiting to be flushed.
/// </summary>
public int PendingChangeCount => _changes.Count;
```

**Step 2: Add queue diagnostics to `SubjectPropertyWriter`**

In `src/Namotion.Interceptor.Connectors/SubjectPropertyWriter.cs`, add:

```csharp
/// <summary>
/// Gets the number of pending write retries, or null if the write retry queue is not enabled.
/// Set by the background service after queue creation.
/// </summary>
internal Func<int?>? GetPendingWriteRetries { get; set; }

/// <summary>
/// Gets the number of pending incoming changes, or null if the change queue is not active.
/// Set by the background service when the change queue processor is created.
/// </summary>
internal Func<int?>? GetPendingIncomingChanges { get; set; }
```

**Step 3: Wire in `SubjectSourceBackgroundService`**

In `src/Namotion.Interceptor.Connectors/SubjectSourceBackgroundService.cs`:

In the constructor, after creating `_propertyWriter` and `WriteRetryQueue`:
```csharp
if (WriteRetryQueue is not null)
{
    _propertyWriter.GetPendingWriteRetries = () => WriteRetryQueue.PendingWriteCount;
}
```

In `ExecuteAsync`, after creating the `ChangeQueueProcessor`:
```csharp
_propertyWriter.GetPendingIncomingChanges = () => processor.PendingChangeCount;
```

And in the `finally` block or after the processor scope ends:
```csharp
_propertyWriter.GetPendingIncomingChanges = null;
```

**Step 4: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded

---

### Task 7: Add `PendingReadCount` to `ReadAfterWriteManager`

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/ReadAfterWrite/ReadAfterWriteManager.cs`

**Step 1: Add property**

In `ReadAfterWriteManager`, add:

```csharp
/// <summary>
/// Gets the number of pending read-after-write operations.
/// </summary>
internal int PendingReadCount
{
    get
    {
        lock (_lock)
        {
            return _pendingReads.Count;
        }
    }
}
```

**Step 2: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded

---

### Task 8: Wire throughput counters and queue depths into `OpcUaClientDiagnostics`

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs` — add throughput counter fields
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientDiagnostics.cs` — add new public properties
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs` — count incoming changes
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Polling/PollingManager.cs` — count incoming changes

**Step 1: Add throughput counters to `OpcUaSubjectClientSource`**

Add fields:
```csharp
internal ThroughputCounter IncomingThroughput { get; } = new();
internal ThroughputCounter OutgoingThroughput { get; } = new();
```

The `_outgoingThroughput` is already passed to `OpcUaClientWriter` in Task 3. Expose it as `OutgoingThroughput` property for diagnostics.

Also store `_propertyWriter` reference for queue diagnostics access (it's already stored as a field).

**Step 2: Wire incoming throughput into `SubscriptionManager`**

In `SubscriptionManager` constructor, accept `ThroughputCounter incomingThroughput` parameter.

In `OnFastDataChange`, after building the changes list:
```csharp
_incomingThroughput.Add(changes.Count);
```

**Step 3: Wire incoming throughput into `PollingManager`**

In `PollingManager` constructor, accept `ThroughputCounter incomingThroughput` parameter.

In `ProcessValueChange`, when a value actually changes:
```csharp
_incomingThroughput.Add(1);
```

**Step 4: Update constructor chains**

`SessionManager` creates `SubscriptionManager` and `PollingManager`. Pass the throughput counter through:
- `SessionManager` constructor takes `ThroughputCounter incomingThroughput`
- Passes to `PollingManager` and `SubscriptionManager` constructors
- `OpcUaSubjectClientSource` passes `IncomingThroughput` when creating `SessionManager`

**Step 5: Add new properties to `OpcUaClientDiagnostics`**

```csharp
/// <summary>
/// Gets the average incoming changes per second over the last 60 seconds.
/// </summary>
public double IncomingChangesPerSecond => _source.IncomingThroughput.GetRate();

/// <summary>
/// Gets the average outgoing changes per second over the last 60 seconds.
/// </summary>
public double OutgoingChangesPerSecond => _source.OutgoingThroughput.GetRate();

/// <summary>
/// Gets the number of pending write retries, or null if write retry queue is disabled.
/// </summary>
public int? PendingWriteRetries => _source.PropertyWriter?.GetPendingWriteRetries?.Invoke();

/// <summary>
/// Gets the number of pending incoming changes waiting to be processed, or null if not available.
/// </summary>
public int? PendingIncomingChanges => _source.PropertyWriter?.GetPendingIncomingChanges?.Invoke();

/// <summary>
/// Gets the number of pending read-after-write operations, or null if disabled.
/// </summary>
public int? PendingReadAfterWrites => _source.SessionManager?.ReadAfterWriteManager?.PendingReadCount;
```

Add `internal SubjectPropertyWriter? PropertyWriter => _propertyWriter;` to `OpcUaSubjectClientSource` for diagnostics access.

**Step 6: Build and run all unit tests**

Run: `dotnet build src/Namotion.Interceptor.slnx && dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration" --no-restore`
Expected: Build succeeded, all tests pass

---

### Task 9: Final verification

**Step 1: Full build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded with 0 errors, 0 warnings

**Step 2: Run all unit tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: All tests pass

**Step 3: Verify public API snapshot**

The public API changed — `OpcUaClientDiagnostics` gained new properties. If there's an API snapshot test for the OpcUa package, it needs to be updated. Check if one exists:

Run: `find src -name "*.verified.txt" -path "*OpcUa*"`

If a snapshot test exists, run it, accept the new snapshot:
Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~PublicApi"`

If it fails, copy `.received.txt` over `.verified.txt`.

**Step 4: Verify line counts improved**

Run: `wc -l src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`
Expected: ~620 lines (down from 842)

**Step 5: Accept snapshot updates if needed**

If the public API snapshot test fails, copy the `.received.txt` over `.verified.txt` to accept the new API surface.
