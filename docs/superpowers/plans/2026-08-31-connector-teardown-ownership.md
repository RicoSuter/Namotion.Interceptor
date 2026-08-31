# Connector Teardown Ownership Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace PR 485's overlapping teardown ownership with one synchronous processor-to-retry handoff that keeps shutdown bounded, accounts every terminally unconfirmed batch exactly once, and ends with fewer production C# lines than `master`.

**Architecture:** `ChangeQueueProcessor` owns direct/server batches and closes them with one atomic delivery state. Source-client batches transfer synchronously into `WriteRetryQueue` before the first await; the retry queue then owns pending, waiting, in-flight, failed, and retired states. A small outer task/deadline coordinator owns cancellation and cleanup, while one source completion hook uses the same teardown token to flush parked writes before retirement.

**Tech Stack:** C# 13, .NET 9/10, xUnit, Moq, `System.Threading` atomics and synchronization primitives, `ValueTask`, PublicApiGenerator/Verify.

**Spec:** `docs/superpowers/specs/2026-08-31-connector-teardown-ownership-design.md`

## Global Constraints

- Successful atomic admission is the write-start linearization point; physical callback entry after return is allowed only when admission won before close.
- Exactly one component owns each batch, and terminal accounting occurs exactly once.
- The teardown deadline is one internal five-second constant; do not add another timeout or public configuration.
- Preserve delivery ordering, merge rules, retry behavior, partial-failure semantics, capacity-zero behavior, and diagnostics attribution.
- Potentially blocking cancellation callbacks, loggers, and user drop callbacks must not extend the five-second bound.
- Keep `QueueMetrics.CreateDropReporter` out of the public API; official connector assemblies may use it through explicit friend-assembly access.
- Keep the approved `AGENTS.md` comment-policy additions.
- No hardcoded sleeps in tests. Use `TaskCompletionSource`, `ManualResetEventSlim`, `CountdownEvent`, or `AsyncTestHelpers.WaitUntilAsync`.
- Net non-test production C# lines versus starting commit `082bb1cee82f2428fe8e94839294b5405138d79c` must be negative.
- Do not modify the existing PR worktree or the user's main checkout.

---

## File Map

- `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs`: direct delivery admission, bounded cancellation coordinator, final drain, detached cleanup.
- `src/Namotion.Interceptor.Connectors/WriteRetryQueue.cs`: source write admission, pending/in-flight ownership, retirement, retry settlement.
- `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`: synchronous ownership handoff and one shared-deadline completion flush.
- `src/Namotion.Interceptor.Connectors/Diagnostics/SourceDiagnostics.cs`: capacity-zero retry accounting contract.
- `src/Namotion.Interceptor.Connectors/Diagnostics/QueueMetrics.cs`: epoch-bound late drop reporting.
- `src/Namotion.Interceptor.Connectors/Namotion.Interceptor.Connectors.csproj`: friend access for the official connector assemblies.
- `src/Namotion.Interceptor.{Mqtt,OpcUa,WebSocket}/**`: timeout configuration removal and epoch-bound reporter use.
- `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs`: direct-handler races and bounded teardown.
- `src/Namotion.Interceptor.Connectors.Tests/WriteRetryQueueTests.cs`: retry ownership and terminal settlement.
- `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceBaseTests.cs`: cross-layer ownership and exact combined diagnostics.
- `src/Namotion.Interceptor.Connectors.Tests/Diagnostics/OutboundDropCountingTests.cs`: capacity-zero and attribution regressions.
- `src/Namotion.Interceptor.Connectors.Tests/Diagnostics/QueueMetricsTests.cs`: epoch isolation.
- `src/**/VerifyChecksTests.PublicApi.verified.txt`: approved timeout API removals only.
- `docs/connectors.md` and connector-specific docs: fixed internal teardown contract.
- `AGENTS.md`: retain the approved comment-policy guidance.

---

### Task 1: Epoch-Bound Drop Reporters and Repository Guidance

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Diagnostics/QueueMetrics.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Namotion.Interceptor.Connectors.csproj`
- Test: `src/Namotion.Interceptor.Connectors.Tests/Diagnostics/QueueMetricsTests.cs`
- Modify: `AGENTS.md`

**Interfaces:**
- Produces: `internal Action<long> QueueMetrics.CreateDropReporter()`.
- Produces: friend access for `Namotion.Interceptor.Mqtt`, `Namotion.Interceptor.OpcUa`, and `Namotion.Interceptor.WebSocket`.
- Consumes: existing `QueueMetrics.Reset()` and `QueueDiagnostics.TotalDropped`.

- [ ] **Step 1: Add the failing epoch-isolation test**

Add this test to `QueueMetricsTests`:

```csharp
[Fact]
public void WhenAReporterBelongsToThePreviousEpoch_ThenItsLateDropsAreIgnored()
{
    // Arrange
    var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundChanges));
    var diagnostics = new QueueDiagnostics(metrics);
    var oldReporter = metrics.CreateDropReporter();
    oldReporter(1);

    // Act
    metrics.Reset();
    var currentReporter = metrics.CreateDropReporter();
    oldReporter(2);
    currentReporter(3);

    // Assert
    Assert.Equal(3, diagnostics.TotalDropped);
}
```

- [ ] **Step 2: Run the test and verify RED**

Run:

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --filter "FullyQualifiedName~WhenAReporterBelongsToThePreviousEpoch_ThenItsLateDropsAreIgnored"
```

Expected: compilation fails because `CreateDropReporter` does not exist.

- [ ] **Step 3: Implement epoch-bound reporting**

Change the immutable snapshot and drop methods to this shape:

```csharp
private sealed record Snapshot(long Accumulated, long Epoch, Registration? Active, int? Capacity);

private Snapshot _snapshot = new(0, 0, null, null);

public void AddDropped(long count) => AddDropped(count, epoch: null);

internal Action<long> CreateDropReporter()
{
    var epoch = Volatile.Read(ref _snapshot).Epoch;
    return count => AddDropped(count, epoch);
}

private void AddDropped(long count, long? epoch)
{
    if (count <= 0)
    {
        return;
    }

    lock (_snapshotLock)
    {
        var current = _snapshot;
        if (epoch is null || current.Epoch == epoch)
        {
            Volatile.Write(ref _snapshot, current with { Accumulated = current.Accumulated + count });
        }
    }
}

internal void Reset()
{
    lock (_snapshotLock)
    {
        var current = _snapshot;
        Volatile.Write(ref _snapshot, current with { Accumulated = 0, Epoch = current.Epoch + 1 });
    }
}
```

Preserve `Epoch` in `Register` and `Release` by using `current with { ... }` instead of constructing a snapshot without it.

Add these entries to the connector project file next to the existing test friends:

```xml
<InternalsVisibleTo Include="Namotion.Interceptor.Mqtt" />
<InternalsVisibleTo Include="Namotion.Interceptor.OpcUa" />
<InternalsVisibleTo Include="Namotion.Interceptor.WebSocket" />
```

Add these approved comment-policy bullets from PR 485 to `AGENTS.md` under `## Coding Style`:

```markdown
- **Inline comments: the why a reader cannot derive.** Length is earned by preventing a plausible wrong edit, such as a lock discipline, a pooled buffer that must not be read after release, or an ordering constraint. It is not earned by defending a decision against alternatives, which belongs in the pull request or `docs/design/`. Never restate the line below.
- **XML docs state the contract**, not the reasoning. `<remarks>` is for a caveat a caller must act on.
- **One canonical location per concept**, cross-referenced. Three copies drift.
```

- [ ] **Step 4: Run the focused metrics tests and API snapshot test**

Run:

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --filter "FullyQualifiedName~QueueMetricsTests|FullyQualifiedName~VerifyChecksTests.PublicApi"
```

Expected: all tests pass and the public API snapshot remains unchanged because the factory is internal.

- [ ] **Step 5: Commit**

```bash
git add AGENTS.md src/Namotion.Interceptor.Connectors/Namotion.Interceptor.Connectors.csproj src/Namotion.Interceptor.Connectors/Diagnostics/QueueMetrics.cs src/Namotion.Interceptor.Connectors.Tests/Diagnostics/QueueMetricsTests.cs
git commit -m "fix: isolate late queue drop reports by run"
```

---

### Task 2: Terminal Retry-Queue Ownership

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/WriteRetryQueue.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/WriteRetryQueueTests.cs`

**Interfaces:**
- Produces: `public void Retire()`.
- Produces: lock-protected `_retired` and `_activeWriteCount` state.
- Preserves: `Enqueue`, `FlushAsync`, `DrainForLocalReapply`, `PendingWriteCount`, and ring-buffer ordering.

- [ ] **Step 1: Add failing retirement tests**

Add deterministic tests with these exact names and outcomes:

Add one class-level watchdog used only by `WaitAsync`, never to make a race pass:

```csharp
private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
```

```csharp
[Fact]
public async Task WhenTheQueueIsRetiredWhileAFlushIsInFlight_ThenTheBatchIsCountedExactlyOnce()
{
    // Arrange
    var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundRetries));
    var diagnostics = new QueueDiagnostics(metrics);
    using var queue = new WriteRetryQueue(100, NullLogger.Instance, metrics);
    var source = new Mock<ISubjectSource>();
    var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    source.Setup(item => item.WriteChangesAsync(
            It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
        .Returns(async (ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
        {
            writeStarted.TrySetResult();
            await releaseWrite.Task.ConfigureAwait(false);
            return WriteResult.Success;
        });
    queue.Enqueue(CreateChanges(3));
    var flush = queue.FlushAsync(source.Object, CancellationToken.None);
    await writeStarted.Task.WaitAsync(TestTimeout);

    // Act
    queue.Retire();

    // Assert
    Assert.Equal(3, diagnostics.TotalDropped);
    releaseWrite.TrySetResult();
    Assert.True(await flush.AsTask().WaitAsync(TestTimeout));
    Assert.Equal(3, diagnostics.TotalDropped);
}
```

Also add:

- `WhenRetireIsCalledTwice_ThenPendingAndActiveWritesAreCountedOnce` with two pending and one blocked active write, final total three.
- `WhenAWriteIsEnqueuedAfterRetirement_ThenItIsCountedWithoutEnteringTheQueue` with final depth zero and exact dropped count.
- `WhenAFailingFlushSettlesAfterRetirement_ThenItIsNotRequeuedOrCountedAgain` with exact final count after awaiting the late continuation.

- [ ] **Step 2: Run the retirement tests and verify RED**

Run:

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --filter "FullyQualifiedName~WriteRetryQueueTests&FullyQualifiedName~Retire"
```

Expected: compilation fails because `Retire` does not exist.

- [ ] **Step 3: Add terminal state under the existing queue lock**

Add fields:

```csharp
private bool _retired;
private int _activeWriteCount;
```

Implement retirement with accounting outside the lock:

```csharp
public void Retire()
{
    int stranded;
    lock (_lock)
    {
        if (_retired)
        {
            return;
        }

        _retired = true;
        stranded = _pendingWrites.Count + _activeWriteCount;
        _pendingWrites.Clear();
        _activeWriteCount = 0;
        Volatile.Write(ref _count, 0);
    }

    _metrics.AddDropped(stranded);
}
```

Update `Enqueue` so the `_retired` check and pending insertion happen in the same lock. A rejected batch reports `changes.Length` outside the lock and never enters `_pendingWrites`.

When `FlushAsync` removes `count` pending entries, add `count` to `_activeWriteCount` in that same critical section. If retirement won first, the pending list is empty and no source call occurs. Settle success or failure under `_lock`:

```csharp
private int SettleFlushedChanges(ReadOnlySpan<SubjectPropertyChange> failedChanges, int attemptedCount)
{
    lock (_lock)
    {
        if (_retired)
        {
            return 0;
        }

        _activeWriteCount -= attemptedCount;
        _pendingWrites.InsertRange(0, failedChanges);
        return TrimToCapacity();
    }
}
```

Call it with an empty span on success and with normalized `FailedChanges` on failure. `Dispose()` calls `Retire()` before disposing the semaphore.

- [ ] **Step 4: Run all retry-queue tests**

Run:

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --filter "FullyQualifiedName~WriteRetryQueueTests"
```

Expected: all tests pass, including existing ordering, capacity, batching, cancellation, and partial-failure tests.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/WriteRetryQueue.cs src/Namotion.Interceptor.Connectors.Tests/WriteRetryQueueTests.cs
git commit -m "fix: give retry writes terminal ownership"
```

---

### Task 3: Synchronous Ownership of Current Source Writes

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/WriteRetryQueue.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/WriteRetryQueueTests.cs`

**Interfaces:**
- Consumes: `Retire()` and active-count settlement from Task 2.
- Produces: `public ValueTask WriteAsync(ISubjectSource source, ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)`.
- Guarantees: current batch ownership is registered synchronously before the returned `ValueTask` can suspend.

- [ ] **Step 1: Add the current-write handoff race test**

Add:

```csharp
[Fact]
public async Task WhenACurrentWriteWaitsBehindAnOlderRetryAtRetirement_ThenBothAreCountedOnce()
{
    // Arrange
    var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundRetries));
    var diagnostics = new QueueDiagnostics(metrics);
    using var queue = new WriteRetryQueue(100, NullLogger.Instance, metrics);
    var source = new Mock<ISubjectSource>();
    var olderWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseOlderWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var currentWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    source.Setup(item => item.WriteChangesAsync(
            It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
        .Returns(async (ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
        {
            if (changes.Span[0].GetOldValue<int>() == 0)
            {
                olderWriteStarted.TrySetResult();
                await releaseOlderWrite.Task.ConfigureAwait(false);
            }
            else
            {
                currentWriteStarted.TrySetResult();
            }

            return WriteResult.Success;
        });
    queue.Enqueue(CreateChanges(1, startId: 0));
    var olderFlush = queue.FlushAsync(source.Object, CancellationToken.None);
    await olderWriteStarted.Task.WaitAsync(TestTimeout);
    var currentWrite = queue.WriteAsync(source.Object, CreateChanges(1, startId: 1), CancellationToken.None);

    // Act
    queue.Retire();
    releaseOlderWrite.TrySetResult();
    await olderFlush.AsTask().WaitAsync(TestTimeout);
    await currentWrite.AsTask().WaitAsync(TestTimeout);

    // Assert
    Assert.False(currentWriteStarted.Task.IsCompleted);
    Assert.Equal(2, diagnostics.TotalDropped);
}
```

Add `WhenACurrentPartialFailureSettlesBeforeRetirement_ThenOnlyFailedChangesAreCounted` and assert that a three-change result with one failed change produces exactly one drop at retirement.

- [ ] **Step 2: Run the current-write tests and verify RED**

Run:

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --filter "FullyQualifiedName~ACurrentWrite|FullyQualifiedName~ACurrentPartialFailure"
```

Expected: compilation fails because `WriteAsync` does not exist.

- [ ] **Step 3: Register ownership before constructing the async operation**

Keep the public entry point non-async and report rejection outside `_lock`:

```csharp
public ValueTask WriteAsync(
    ISubjectSource source,
    ReadOnlyMemory<SubjectPropertyChange> changes,
    CancellationToken cancellationToken)
{
    var rejected = false;
    lock (_lock)
    {
        if (_retired)
        {
            rejected = true;
        }
        else
        {
            _activeWriteCount += changes.Length;
        }
    }

    if (rejected)
    {
        _metrics.AddDropped(changes.Length);
        return ValueTask.CompletedTask;
    }

    return WriteCoreAsync(source, changes, cancellationToken);
}
```

Extract the body after semaphore acquisition from `FlushAsync` into `FlushCoreAsync`. Both public operations use the same gate:

```csharp
public async ValueTask<bool> FlushAsync(ISubjectSource source, CancellationToken cancellationToken)
{
    if (!await TryEnterFlushAsync(cancellationToken).ConfigureAwait(false))
    {
        return false;
    }

    try
    {
        return await FlushCoreAsync(source, cancellationToken).ConfigureAwait(false);
    }
    finally
    {
        ReleaseFlush();
    }
}
```

Remove the unlocked `IsEmpty` return before semaphore acquisition. It lets a current write overtake an older batch that has left `_pendingWrites` but is still in flight, especially for `ISupportsConcurrentWrites` sources.

`WriteCoreAsync` acquires `_flushSemaphore` once, calls `FlushCoreAsync`, and keeps the semaphore through the current source write. This makes the sequence exactly “older retries, then current write” without a gap in which another flush can overtake it. After an older flush failure, move the unattempted current batch from active ownership to the end of pending ownership, preserving the existing requeue order. After attempting the current batch, move only its normalized failed changes to the front of anything enqueued while the source call was in flight. Before invoking the current source write, check `_retired` under `_lock`; when retirement already claimed the active count, return without invoking or reporting. A retirement that wins after this check may still race with physical callback entry, which is allowed because synchronous admission already won.

Use one helper for settlement after the current source call:

```csharp
private int SettleCurrentWrite(
    ReadOnlySpan<SubjectPropertyChange> failedChanges,
    int attemptedCount)
{
    lock (_lock)
    {
        if (_retired)
        {
            return 0;
        }

        _activeWriteCount -= attemptedCount;
        _pendingWrites.InsertRange(0, failedChanges);
        return TrimToCapacity();
    }
}
```

Use a separate short locked branch for the “older flush failed before current was attempted” case: decrement `_activeWriteCount`, append the current span to `_pendingWrites`, apply `TrimToCapacity`, and report any capacity eviction outside the lock. Late paths that observe `_retired` perform no requeue or reporting because `Retire` already claimed the active count.

- [ ] **Step 4: Run all retry-queue tests**

Run:

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --filter "FullyQualifiedName~WriteRetryQueueTests"
```

Expected: all retry tests pass and exact totals remain unchanged after late continuations settle.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/WriteRetryQueue.cs src/Namotion.Interceptor.Connectors.Tests/WriteRetryQueueTests.cs
git commit -m "fix: hand current writes to the retry owner"
```

---

### Task 4: One Terminal State for Direct Delivery

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs`

**Interfaces:**
- Produces: `internal static readonly TimeSpan TeardownFlushBound = TimeSpan.FromSeconds(5)`.
- Produces internal constructor parameters: `bool writeHandlerOwnsChanges`, `Action? terminalHandler`, and `Func<CancellationToken, ValueTask>? completionHandler`.
- Direct mode owns admitted writes; handler-owned mode delegates admission and settlement to its synchronous handler.

- [ ] **Step 1: Add deterministic direct-delivery teardown tests**

Port or write these exact behaviors:

- `WhenAnImmediateWriteIgnoresCancellation_ThenStoppingEndsAtTheBoundAndCountsItOnce`: enter the handler, cancel, wait for `ProcessAsync` to return within `TeardownFlushBound + 5 seconds`, assert `DropCount == 1` before releasing the handler, release and assert the final count remains one.
- `WhenABufferedChangeFinishesFilteringAfterTheDeadline_ThenTheHandlerIsNotInvoked`: use the internal constructor to install a completion-handler barrier, block in `propertyFilter`, cancel and await the bound, dispose the processor while the filter is still blocked, release the filter, wait for the completion barrier and `DropCount == 1`, and assert the write handler was not entered. Reaching the completion barrier proves the late core passed its final merge/flush without touching an already disposed merger.
- `WhenABufferedWriteIsCancelledBeforeTheDeadline_ThenMergedSurvivorsAreRetried`: cancel the first handler attempt, allow the completion flush to succeed, and assert only merged survivors are delivered.
- `WhenTheDropCallbackBlocks_ThenStoppingStillEndsAtTheBound`: block the callback after it receives a positive count, assert `ProcessAsync` returns, then release it.
- `WhenACancellationCallbackBlocks_ThenStoppingStillEndsAtTheBound`: register a blocking callback on the processing token seen by the handler and assert external cancellation plus `ProcessAsync` remain bounded.
- `WhenTheLoggerBlocks_ThenStoppingStillEndsAtTheBound`: block the warning logger and assert the same bound.

Use a watchdog cancellation source only to release test barriers after 60 seconds; never use it as the success signal.

- [ ] **Step 2: Run the new processor tests and verify RED**

Run each new fully qualified test filter separately. Expected failures are timeouts beyond five seconds, missing drop counts, or late handler entry. Do not run the whole class while a RED test can leave a watchdog-blocked worker alive.

- [ ] **Step 3: Replace configurable teardown with a fixed coordinator**

Remove `DefaultTeardownFlushTimeout`, `_teardownFlushTimeout`, `ValidateTeardownFlushTimeout`, and both constructor timeout parameters. Add:

```csharp
internal static readonly TimeSpan TeardownFlushBound = TimeSpan.FromSeconds(5);
private const int ClosedDelivery = -1;
private int _deliveryState;
private int _processingActive;
private int _mergerDisposed;
private readonly bool _writeHandlerOwnsChanges;
private readonly Action? _terminalHandler;
private readonly Func<CancellationToken, ValueTask>? _completionHandler;
```

Use these direct-delivery transitions:

```csharp
private bool TryAdmitDelivery(int count) =>
    Interlocked.CompareExchange(ref _deliveryState, count, 0) == 0;

private bool TryCompleteDelivery(int count) =>
    Interlocked.CompareExchange(ref _deliveryState, 0, count) == count;

private int CloseDelivery() =>
    Math.Max(0, Interlocked.Exchange(ref _deliveryState, ClosedDelivery));
```

Before a direct handler call, admit the exact immediate or merged survivor count. A failed admission counts the batch and skips invocation. On success, complete it. On processing cancellation before close, complete and enqueue the exact immediate change or each merged survivor back into `_changes` while merger memory is still valid, so the completion flush can retry it with the teardown token. On cancellation after close, do neither because close already claimed it. On another exception, count the batch only when completion wins before close, then log outside the ownership transition; close already counted it otherwise. Handler-owned mode invokes its internal handler without processor admission because the handler synchronously transfers ownership.

- [ ] **Step 4: Replace `FlushRemainingChangesAsync` with one outer deadline**

Extract the existing dequeue/timer body into `ProcessCoreAsync(processingToken, teardownToken)`. Atomically set `_processingActive` before scheduling it and reject concurrent or post-disposal starts. Start it through `Task.Run` so a synchronous filter or handler cannot block the caller before `ProcessAsync` obtains the task. In the core's outermost `finally`, clear `_processingActive` only after its final merger use, then invoke a compare-exchange-guarded `DisposeMergerOnce` when disposal was requested.

The outer method follows this structure:

```csharp
public async Task ProcessAsync(CancellationToken cancellationToken)
{
    var processingTokenSource = new CancellationTokenSource();
    var teardownTokenSource = new CancellationTokenSource();
    var lifetimeTransferred = false;
    Task? processingCancellationTask = null;
    Task? teardownCancellationTask = null;
    var processingTask = Task.Run(
        () => ProcessCoreAsync(processingTokenSource.Token, teardownTokenSource.Token),
        CancellationToken.None);

    try
    {
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (await Task.WhenAny(processingTask, cancellationTask).ConfigureAwait(false) == processingTask)
        {
            await processingTask.ConfigureAwait(false);
            return;
        }

        lifetimeTransferred = true;
        processingCancellationTask = processingTokenSource.CancelAsync();
        try
        {
            await processingTask.WaitAsync(TeardownFlushBound).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            teardownCancellationTask = teardownTokenSource.CancelAsync();
            CountTimedOutDelivery(CloseDelivery() + DrainRemainingCount());
            _terminalHandler?.Invoke();
        }
        finally
        {
            _ = ObserveLateLifetimeAsync(
                processingTask,
                processingCancellationTask,
                teardownCancellationTask,
                processingTokenSource,
                teardownTokenSource);
        }
    }
    finally
    {
        if (!lifetimeTransferred)
        {
            processingTokenSource.Dispose();
            teardownTokenSource.Dispose();
        }
    }
}
```

Do not invoke arbitrary callbacks inline in `CountTimedOutDelivery`; update `_dropCount` first, then dispatch the epoch-bound reporter and warning on detached work. `ObserveLateLifetimeAsync` observes faults and owns both token sources until the core and both non-null cancellation tasks exit. If a cancellation callback never returns, the sources remain intentionally retained instead of being disposed out from under live callbacks. The no-cancellation path still disposes both sources inline.

Inside `ProcessCoreAsync`, stop the periodic timer with the processing token, await its task, flush remaining processor changes with the fresh teardown token, then invoke `_completionHandler` with that same token. The outer coordinator alone owns the deadline.

- [ ] **Step 5: Make disposal terminal without releasing live buffers**

`Dispose()` closes delivery, drains `_changes`, and counts the combined nonzero result exactly once. When `_processingActive` is nonzero, leave the merger to the core's outer `finally` even when the flush gate is currently free; a worker blocked in filtering can acquire that gate later. When no processing lifetime exists, call `DisposeMergerOnce` immediately. The helper uses `_mergerDisposed` so the disposal/core race cannot return the pooled buffer twice. Never write zero into `_deliveryState` from cleanup.

- [ ] **Step 6: Run all processor tests**

Run:

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --filter "FullyQualifiedName~ChangeQueueProcessorTests"
```

Expected: all prior delivery, merge, capacity, echo-suppression, cancellation, and new teardown tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs
git commit -m "fix: close processor delivery at one deadline"
```

---

### Task 5: Source-to-Retry Ownership Handoff

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Diagnostics/SourceDiagnostics.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/TestSubjectSource.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceBaseTests.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/Diagnostics/OutboundDropCountingTests.cs`

**Interfaces:**
- Consumes: `WriteRetryQueue.WriteAsync`, `WriteRetryQueue.Retire`, handler-owned processor mode, terminal handler, completion handler.
- Produces: a non-null lifetime `WriteRetryQueue`, including capacity zero.
- Preserves: source-lifetime subscription, connect-window reconciliation, and one shared teardown deadline.

- [ ] **Step 1: Add the cross-layer exact-accounting tests**

Add:

- `WhenACurrentWriteWaitsBehindAnOlderRetryAtTheDeadline_ThenCombinedDropsEqualTheTwoChanges`: block the older retry write, start a current processor write, stop the source, await stop, release all continuations, await their completion, and assert `OutboundChanges.TotalDropped + OutboundRetries.TotalDropped == 2` with the current handler never entering the transport after retry retirement.
- `WhenACurrentWriteFailsAfterRetryRetirement_ThenItIsCountedOnlyByOutboundRetries`: enter the current source write, stop through the deadline, return a failed result, await final settlement, assert outbound changes zero and retries one.
- `WhenStoppingWithParkedWrites_ThenRetryHandoffUsesTheProcessorDeadline`: cover both an already consumed deadline and an available deadline; assert no fresh five-second window is created.
- `WhenTheSourceStopsWithRetryCapacityZero_ThenEveryOwnedWriteIsCounted`: assert exact total and zero depth.

In `OutboundDropCountingTests`, snapshot `OutboundRetries.TotalDropped` after the connected-phase probe and assert the failing current write adds exactly one, because capacity-zero connect-window ownership is now counted too. Replace `WhenAWriteIsCancelledByTheStop_ThenItIsNotCountedAsDropped` with a deadline test that expects the still-unconfirmed write to be counted after the shared completion-flush deadline. Keep `WhenTheDisabledQueueDrainRuns_ThenNothingIsCounted`, rename it to `WhenTheDisabledQueueDrainRuns_ThenAnUnownedChangeIsNotCounted`, and preserve its zero assertion because the captured property is not owned by the source.

Each test must await explicit late-worker completion before its final exact assertions.

- [ ] **Step 2: Run each new source test and verify RED**

Run each test by full name. Expected failures are double totals, missing retry totals, a second deadline, or current source invocation after retirement.

- [ ] **Step 3: Give every source a retry owner**

Construct `WriteRetryQueue` for every non-negative capacity, including zero, and register its depth/capacity once for the source lifetime. Remove nullable queue branches and validate capacity with `ArgumentOutOfRangeException.ThrowIfNegative(writeRetryQueueSize)`.

Replace the async source handler body with the synchronous handoff:

```csharp
private ValueTask WriteChangesViaRetryQueueAsync(
    ReadOnlyMemory<SubjectPropertyChange> changes,
    CancellationToken cancellationToken) =>
    WriteRetryQueue.WriteAsync(this, changes, cancellationToken);
```

Construct the connected processor with:

```csharp
writeHandlerOwnsChanges: true,
terminalHandler: WriteRetryQueue.Retire,
completionHandler: async teardownToken =>
{
    await WriteRetryQueue.FlushAsync(this, teardownToken).ConfigureAwait(false);
}
```

Use `Metrics.OutboundChanges.CreateDropReporter()` for processor-owned drops. In the source `finally`, call `WriteRetryQueue.Retire()` before publishing `Stopped`; this is idempotent with the processor's timeout path.

Delete `_stoppingToken` and `IsExpectedShutdown` after the retry owner becomes the only component settling source failures.

Update `SourceDiagnostics.OutboundRetries` remarks to state that capacity zero keeps depth zero while failed and owned connect-window writes are included in `TotalDropped`.

- [ ] **Step 4: Run source and retry tests together**

Run:

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --filter "FullyQualifiedName~SubjectSourceBaseTests|FullyQualifiedName~WriteRetryQueueTests"
```

Expected: all source lifecycle, connect-window, reconciliation, retry, and new exact-accounting tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs src/Namotion.Interceptor.Connectors/Diagnostics/SourceDiagnostics.cs src/Namotion.Interceptor.Connectors.Tests/TestSubjectSource.cs src/Namotion.Interceptor.Connectors.Tests/SubjectSourceBaseTests.cs src/Namotion.Interceptor.Connectors.Tests/Diagnostics/OutboundDropCountingTests.cs
git commit -m "fix: transfer source writes to one retry owner"
```

---

### Task 6: Remove the Unreleased Timeout Surface and Update Official Connectors

**Files:**
- Modify: `src/Namotion.Interceptor.Mqtt/Client/MqttClientConfiguration.cs`
- Modify: `src/Namotion.Interceptor.Mqtt/Server/MqttServerConfiguration.cs`
- Modify: `src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs`
- Modify: `src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServer.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientConfiguration.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerConfiguration.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServer.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Client/WebSocketClientConfiguration.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketServerConfiguration.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectServer.cs`
- Modify: official connector tests and four public API snapshots containing `TeardownFlushTimeout`
- Modify: `docs/connectors.md`, `docs/connectors-opcua-client.md`, `docs/connectors-opcua-server.md`

**Interfaces:**
- Consumes: fixed `ChangeQueueProcessor.TeardownFlushBound` and internal epoch reporters.
- Removes: public `DefaultTeardownFlushTimeout`, constructor parameters, and six connector configuration properties.
- Preserves: every other public API member.

- [ ] **Step 1: Remove timeout validation tests and add the fixed-contract documentation assertion**

Delete tests that configure or reject negative `TeardownFlushTimeout`. Keep timing tests against the internal five-second bound. Update configuration default tests so they no longer mention the removed property.

Update the public API verified files by removing only:

- `ChangeQueueProcessor.DefaultTeardownFlushTimeout`;
- the final `teardownFlushTimeout` constructor parameter;
- the protected `SubjectSourceBase` timeout parameter;
- the MQTT, OPC UA, and WebSocket client/server configuration properties.

- [ ] **Step 2: Remove the production configuration surface**

Remove the six properties, their validation branches, and constructor forwarding. Update server processor call sites to use:

```csharp
dropHandler: Metrics.OutboundChanges.CreateDropReporter()
```

with no timeout argument.

Update docs to state that final delivery shares one internal five-second safety bound and cannot be configured per connector.

- [ ] **Step 3: Run all affected API and configuration tests**

Run:

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi|FullyQualifiedName~Teardown"
dotnet test src/Namotion.Interceptor.Mqtt.Tests/Namotion.Interceptor.Mqtt.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi|FullyQualifiedName~Configuration"
dotnet test src/Namotion.Interceptor.OpcUa.Tests/Namotion.Interceptor.OpcUa.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi|FullyQualifiedName~Registration"
dotnet test src/Namotion.Interceptor.WebSocket.Tests/Namotion.Interceptor.WebSocket.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi|FullyQualifiedName~Configuration"
```

Expected: all pass with no unexpected `.received.txt` files.

- [ ] **Step 4: Commit**

Stage the task's exact files and commit:

```bash
git add docs/connectors.md docs/connectors-opcua-client.md docs/connectors-opcua-server.md src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt src/Namotion.Interceptor.Mqtt/Client/MqttClientConfiguration.cs src/Namotion.Interceptor.Mqtt/Server/MqttServerConfiguration.cs src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServer.cs src/Namotion.Interceptor.Mqtt.Tests/MqttClientConfigurationTests.cs src/Namotion.Interceptor.Mqtt.Tests/VerifyChecksTests.PublicApi.verified.txt src/Namotion.Interceptor.OpcUa/Client/OpcUaClientConfiguration.cs src/Namotion.Interceptor.OpcUa/Server/OpcUaServerConfiguration.cs src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServer.cs src/Namotion.Interceptor.OpcUa.Tests/OpcUaRegistrationTests.cs src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt src/Namotion.Interceptor.WebSocket/Client/WebSocketClientConfiguration.cs src/Namotion.Interceptor.WebSocket/Server/WebSocketServerConfiguration.cs src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectServer.cs src/Namotion.Interceptor.WebSocket.Tests/Client/WebSocketClientConfigurationTests.cs src/Namotion.Interceptor.WebSocket.Tests/Server/WebSocketServerConfigurationTests.cs src/Namotion.Interceptor.WebSocket.Tests/VerifyChecksTests.PublicApi.verified.txt
git commit -m "refactor: use one internal connector teardown bound"
```

---

### Task 7: Simplify, Measure, and Verify the Complete Redesign

**Files:**
- Modify only files already touched when simplification is required.
- Test: all affected test projects.

**Interfaces:**
- Consumes: Tasks 1 through 6.
- Produces: verified final tree with negative production-line delta.

- [ ] **Step 1: Remove superseded machinery and duplication**

Search:

```bash
rg -n "Preparing|TimedOut|ClaimInFlight|TryAdmitWrite|teardownFlushTimeout|DefaultTeardownFlushTimeout|ProcessingCancellationState" src docs
```

Expected: no production matches except historical text in the approved design and implementation plan. Remove duplicated settlement branches by keeping one locked queue settlement helper and one processor completion helper. Do not add a generic lease abstraction.

- [ ] **Step 2: Enforce the production size gate**

Run:

```bash
git diff --numstat 082bb1cee82f2428fe8e94839294b5405138d79c -- src | awk '$3 ~ /\.cs$/ && $3 !~ /\.Tests\// && $3 !~ /Tests\.cs$/ { added += $1; deleted += $2 } END { print "production C# added", added, "deleted", deleted, "net", added - deleted; exit !(added - deleted < 0) }'
```

Expected: a negative net value and exit code zero. If not negative, simplify before proceeding. Do not delete tests, public XML documentation, or correctness checks to satisfy this gate.

- [ ] **Step 3: Repeat focused race tests**

Run the ownership filter five separate times:

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --filter "FullyQualifiedName~Retire|FullyQualifiedName~Deadline|FullyQualifiedName~IgnoresCancellation|FullyQualifiedName~Ownership"
```

Expected on every run: zero failures and no test lasting beyond its watchdog.

- [ ] **Step 4: Run the complete connector suite**

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj
```

Expected: zero failures.

- [ ] **Step 5: Run the affected connector integration suites**

```bash
dotnet test src/Namotion.Interceptor.Mqtt.Tests/Namotion.Interceptor.Mqtt.Tests.csproj
dotnet test src/Namotion.Interceptor.OpcUa.Tests/Namotion.Interceptor.OpcUa.Tests.csproj
dotnet test src/Namotion.Interceptor.WebSocket.Tests/Namotion.Interceptor.WebSocket.Tests.csproj
```

Expected: zero failures. These full project runs are required because shutdown wiring changes in all three connector implementations even though their wire protocols do not change.

- [ ] **Step 6: Run whole-solution non-integration tests**

```bash
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: zero failures.

- [ ] **Step 7: Run the Connector Tester load and chaos verification**

Create a detached baseline worktree for `082bb1cee82f2428fe8e94839294b5405138d79c`; do not reuse the user's main checkout:

```bash
test ! -e /tmp/namotion-pr485-load-baseline
git worktree add --detach /tmp/namotion-pr485-load-baseline 082bb1cee82f2428fe8e94839294b5405138d79c
```

On baseline and final, run each load profile for one complete fifteen-minute cycle, one profile at a time so the processes do not distort one another:

```bash
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile opcua-load --configuration Release
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile mqtt-load --configuration Release
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile websocket-load --configuration Release
```

Stop each load run with Ctrl-C only after the first complete PASS cycle. Compare final throughput, P50/P95/P99 latency, allocation rate, and process/heap levels with its same-profile baseline. Any material regression blocks integration until explained and approved.

On the final branch, run these chaos profiles, which may run concurrently because they bind separate endpoints:

```bash
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile opcua-chaos --configuration Release
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile mqtt-chaos --configuration Release
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile websocket-chaos --configuration Release
```

Wait for at least 100 PASS cycles per profile, then stop each with Ctrl-C. Require zero failed convergence cycles, inspect `findings.log`, `chaos-events.csv`, and the last ten cycle logs, and verify the post-GC `HeapMB` series in `cycles.csv` has no sustained upward trend. Record each log directory and the measured baseline/final load values for the PR body. These runs take roughly two hours when the three chaos profiles run in parallel, plus about ninety minutes of sequential baseline/final load cycles.

- [ ] **Step 8: Run final hygiene checks**

```bash
pwsh scripts/diff-composition.ps1 -PerProject
git diff --check
git status --short
```

Expected: diff-composition output ready for the PR body, no whitespace errors, and no generated `.received.txt`, package, build, or unrelated files.

- [ ] **Step 9: Commit verified simplifications**

If Step 1 changed files, inspect `git status --short`, invoke `git add` with each reviewed path listed explicitly, and commit:

```bash
git status --short
git commit -m "refactor: simplify terminal write ownership"
```

Do not use `git add -A`, stage unrelated files, or create an empty commit.

- [ ] **Step 10: Request an independent read-only review**

Give the reviewer only the approved spec, starting commit, final commit, full diff, and these criteria: correctness, races, exact combined accounting, bounded teardown, public API, performance, simplicity, tests, and negative production size. For every Critical or Important issue, complete a new red/green cycle, rerun Steps 2 through 8, and proceed to Step 11.

- [ ] **Step 11: Commit review fixes**

If Step 10 changed files after the required verification rerun:

```bash
git status --short
git commit -m "fix: address connector ownership review"
```

Before the commit, invoke `git add` with every reviewed changed path from `git status --short` listed explicitly. Do not use `git add -A` or stage an unrelated file. If no files changed, do not create an empty commit. After any fix commit, request a follow-up review and require the verdict `Ready to merge: Yes`; when the first review has no Critical or Important issue, use its verdict directly.

---

### Task 8: Incorporate the Redesign into Existing PR 485

**Files:**
- Modify remotely: existing PR 485 branch and description.
- Do not create: another GitHub pull request.

**Interfaces:**
- Consumes: verified redesign branch and existing remote PR head.
- Produces: one merge commit whose first-parent tree is the verified redesign and whose second parent records the superseded PR history.

- [ ] **Step 1: Refresh and record the existing PR head**

```bash
git fetch origin master fix/change-queue-teardown-hardening
git rev-parse origin/fix/change-queue-teardown-hardening
```

Confirm the returned PR head is the reviewed old branch or inspect any new commits before continuing.

- [ ] **Step 2: Record the old PR history without reintroducing its tree**

From `codex/pr485-ownership-redesign`, run:

```bash
git merge -s ours --no-ff origin/fix/change-queue-teardown-hardening -m "fix: replace connector teardown with single ownership"
```

The `ours` strategy is intentional: the redesign tree is the complete verified replacement, while the second parent makes the old PR head an ancestor so the existing branch can advance without force-pushing.

- [ ] **Step 3: Verify the merge commit tree is unchanged**

```bash
git diff HEAD^1 HEAD --exit-code
git merge-base --is-ancestor origin/fix/change-queue-teardown-hardening HEAD
git diff --check
```

Expected: no tree diff, ancestor check exit zero, whitespace check clean.

- [ ] **Step 4: Push to the existing PR branch**

```bash
git push origin HEAD:fix/change-queue-teardown-hardening
```

Expected: a normal fast-forward update because the old PR head is the merge commit's second parent.

- [ ] **Step 5: Update the existing PR description**

Read `.github/pull_request_template.md` and the current body with `gh pr view 485`. Create `/tmp/pr485-redesign-body.md` with `apply_patch`, delete all template comments, and update PR 485 without creating a new PR. The body must include:

- an opening consumer-facing summary;
- `## Why` with the shutdown loss and double-accounting race;
- `## Contract` with one-owner admission, synchronous source handoff, exact terminal accounting, and the fixed shared five-second bound;
- `## Breaking changes` explaining removal of the unreleased timeout field, constructor arguments, and six configuration properties, with no migration beyond removing those settings;
- `## Diff composition` containing the exact output from Task 7;
- `## Verification` with actual test totals, independent review verdict, and unchecked or struck-through integration, benchmark, load, and chaos entries with reasons;
- one-owner admission and synchronous source handoff;
- fixed internal five-second bound and approved timeout API removal;
- exact retry and direct drop accounting;
- retained `AGENTS.md` guidance;
- deterministic race tests and full verification commands;
- actual negative production-line delta;
- independent reviewer verdict.

Run:

```bash
gh pr edit 485 --body-file /tmp/pr485-redesign-body.md
```

Then run `gh pr view 485 --json url,title,body` and verify the rendered body retains every applicable template heading and contains no comments, placeholders, hard wrapping, em dashes, or AI attribution.

- [ ] **Step 6: Wait for updated PR checks**

```bash
gh pr checks 485 --watch
```

Expected: every required check passes. If a check fails, inspect it, reproduce locally where possible, fix on the redesign branch, create another normal descendant commit, push to the same PR branch, and wait again.

- [ ] **Step 7: Report completion**

Report the final commit, PR URL, production-line delta, focused and full test totals, CI result, and independent review verdict. State explicitly that the user's main checkout and the old PR worktree were not modified.
