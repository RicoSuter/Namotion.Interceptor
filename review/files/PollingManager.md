# PollingManager.cs - Code Review

**Status:** ✅ Complete
**Reviewer:** Claude
**File:** `src/Namotion.Interceptor.OpcUa/Client/Polling/PollingManager.cs`
**Lines:** 476

## Overview

`PollingManager` provides polling fallback for OPC UA nodes that don't support subscriptions. It's a critical resilience component that ensures all properties remain synchronized even with legacy servers or nodes that fail subscription creation.

### Key Responsibilities
1. Timer-based polling loop with configurable interval
2. Batched OPC UA read operations for efficiency
3. Circuit breaker pattern for resilience during persistent failures
4. Session change detection with value cache reset
5. Value change detection to avoid redundant notifications
6. Metrics collection for diagnostics

## Architecture Assessment

### Strengths
- **Clean separation of concerns**: Metrics (`PollingMetrics`), circuit breaker (`CircuitBreaker`), and core polling logic are well-separated
- **Proper idempotency**: `Start()` is idempotent, `DisposeAsync()` uses `Interlocked.Exchange` for safe double-call
- **Good resilience patterns**: Circuit breaker prevents resource exhaustion, session change detection resets state properly
- **Efficient batching**: Uses `ArraySegment<T>` for zero-allocation batch processing
- **Optimistic concurrency**: Uses `TryUpdate` pattern for safe concurrent modifications

### Design Concerns

**1. Code Duplication with ReadAfterWriteManager**

Both classes share nearly identical patterns:
- CircuitBreaker initialization and usage
- Session change detection logic
- Batched `ReadValueIdCollection` + `session.ReadAsync()` pattern
- Disposal patterns with CancellationTokenSource

**ReadAfterWriteManager** (lines 287-305):
```csharp
var readValues = new ReadValueIdCollection(dueCount);
for (var i = 0; i < dueCount; i++)
{
    readValues.Add(new ReadValueId { NodeId = ..., AttributeId = Attributes.Value });
}
var response = await session.ReadAsync(..., readValues, cancellationToken);
```

**PollingManager** (lines 340-357):
```csharp
var nodesToRead = new ReadValueIdCollection(batch.Count);
foreach (var item in batch)
{
    nodesToRead.Add(new ReadValueId { NodeId = item.NodeId, AttributeId = Attributes.Value });
}
var response = await session.ReadAsync(..., nodesToRead, cancellationToken);
```

**Recommendation**: Extract a shared `BatchedNodeReader` helper or base class.

**2. Timer Strategy Differs**

- `PollingManager`: Uses `PeriodicTimer` (fixed interval, always running)
- `ReadAfterWriteManager`: Uses `Timer` with dynamic rescheduling (event-driven)

Both are valid for their use cases, but the inconsistency adds cognitive load.

## Thread Safety Analysis

### Overall Assessment: Good ✅

The class demonstrates good thread-safety practices with some minor issues.

### Issues Identified

| Issue | Severity | Location | Description |
|-------|----------|----------|-------------|
| Start/Dispose race | Low | Lines 113-132, 440-468 | Task could be created after disposal begins |
| TOCTOU in AddItem | Low | Lines 138-162 | Item could be added to disposing manager |
| Stale TryUpdate | Low | Lines 314-319 | `ResetPolledValues()` silent failure could leave stale cache |
| Missing Volatile.Read | Very Low | Line 453 | `_pollingTask` null check should use Volatile.Read |

### Details

**1. Start/Dispose Race (Low)**
```csharp
// Start() at line 115-128
lock (_startLock)
{
    if (Volatile.Read(ref _disposed) == 1)  // Thread B sets _disposed = 1 HERE
        throw new ObjectDisposedException(...);
    // ... creates task ...
}
```
The `_startLock` is not acquired in `DisposeAsync()`, so disposal can interleave.

**Recommendation**: Either acquire `_startLock` in `DisposeAsync()` or accept the benign race (task will be canceled anyway).

**2. ResetPolledValues TryUpdate (Low)**
```csharp
// Line 317
_pollingItems.TryUpdate(key, item with { LastValue = null }, item);
// If TryUpdate fails, stale LastValue remains
```

**Recommendation**: Use `AddOrUpdate` to guarantee reset:
```csharp
_pollingItems.AddOrUpdate(
    key,
    item with { LastValue = null },
    (_, existing) => existing with { LastValue = null }
);
```

**3. Missing Volatile.Read (Very Low)**
```csharp
// Line 453 - should be:
var task = Volatile.Read(ref _pollingTask);
if (task != null)
{
    await task.WaitAsync(_configuration.PollingDisposalTimeout);
}
```

## Data Flow Analysis

```
┌─────────────────────────────────────────────────────────────────┐
│                      PollingManager                              │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  SubscriptionManager                                             │
│         │                                                        │
│         ▼ (AddItem when subscription fails)                      │
│  ┌──────────────┐                                                │
│  │ _pollingItems│ (ConcurrentDictionary<string, PollingItem>)    │
│  └──────┬───────┘                                                │
│         │                                                        │
│         ▼ (PollLoopAsync - PeriodicTimer)                        │
│  ┌──────────────────────────────────────────────────┐            │
│  │ PollItemsAsync                                   │            │
│  │  1. Check circuit breaker                        │            │
│  │  2. Check session connectivity                   │            │
│  │  3. Detect session change → reset values         │            │
│  │  4. Snapshot items → batch read                  │            │
│  └──────┬───────────────────────────────────────────┘            │
│         │                                                        │
│         ▼ (ReadBatchAsync)                                       │
│  ┌──────────────────────────────────────────────────┐            │
│  │ OPC UA session.ReadAsync()                       │            │
│  │  - Batches up to PollingBatchSize                │            │
│  │  - Respects cancellation token                   │            │
│  └──────┬───────────────────────────────────────────┘            │
│         │                                                        │
│         ▼ (ProcessValueChange)                                   │
│  ┌──────────────────────────────────────────────────┐            │
│  │ 1. Compare with cached LastValue                 │            │
│  │ 2. TryUpdate with optimistic concurrency         │            │
│  │ 3. Queue update via SubjectPropertyWriter        │            │
│  └──────┬───────────────────────────────────────────┘            │
│         │                                                        │
│         ▼                                                        │
│  ┌──────────────────────────────────────────────────┐            │
│  │ Subject Property Updated                         │            │
│  │ (SetValueFromSource)                             │            │
│  └──────────────────────────────────────────────────┘            │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

## SRP/SOLID Analysis

### Single Responsibility: Mostly Good ✅

The class has one clear purpose: poll OPC UA nodes. However, it contains several sub-responsibilities that could be extracted:

1. **Timer management** - Start/Stop logic
2. **Batch reading** - `ReadBatchAsync` could be a standalone utility
3. **Value change detection** - `ValuesAreEqual` is generic
4. **Session change detection** - Duplicated with ReadAfterWriteManager

### Open/Closed: Good ✅

- Uses dependency injection for configuration, logger, session manager
- Delegates value conversion to `SubjectPropertyWriter`
- Circuit breaker is pluggable

### Interface Segregation: N/A

Internal class, no interface needed.

### Dependency Inversion: Good ✅

- Depends on abstractions: `ILogger`, `RegisteredSubjectProperty`
- Configuration injected via `OpcUaClientConfiguration`

## Test Coverage Assessment

### Unit Tests: ⚠️ Insufficient

**PollingMetricsTests.cs** - Only tests the metrics helper class:
- `InitialState_AllMetricsAreZero()`
- `RecordRead_IncrementsCounter()`
- `RecordFailedRead_IncrementsCounter()`
- `RecordValueChange_IncrementsCounter()`
- `RecordSlowPoll_IncrementsCounter()`
- `ConcurrentRecordRead_IsThreadSafe()`
- `ConcurrentMixedOperations_IsThreadSafe()`

**Missing Unit Tests:**
- `PollingManager.Start()` idempotency
- `PollingManager.AddItem()` / `RemoveItem()` behavior
- Circuit breaker triggering during poll failures
- Session change detection and value reset
- Batch size boundary conditions
- Slow poll detection

### Integration Tests: ⚠️ Indirect

No integration tests specifically enable `EnablePollingFallback = true` and simulate subscription failures. The polling fallback path is not exercised in CI.

**Recommendation**: Add integration test that:
1. Configures a server node to reject subscriptions
2. Enables `EnablePollingFallback = true`
3. Verifies values synchronize via polling

## Modern C# Best Practices

### Good ✅
- Uses `record struct` for `PollingItem` (line 471)
- Uses `Lock` class instead of `object` for locking (line 31)
- Uses `PeriodicTimer` for modern async timer pattern
- Uses `Volatile` and `Interlocked` correctly (mostly)
- Uses `ConfigureAwait(false)` consistently
- Uses collection expressions where appropriate
- Uses `ArraySegment<T>` to avoid allocations

### Could Improve
- Line 453: Plain `null` check instead of `Volatile.Read`
- Could use `required` properties in configuration

## Dead Code / Unused Code

None found. All public methods are called from `SubscriptionManager` or `SessionManager`.

## Potential Simplifications

### 1. Extract BatchedNodeReader
~50 lines of duplication with `ReadAfterWriteManager` could be extracted:
```csharp
internal static class BatchedNodeReader
{
    public static async Task<IReadOnlyList<DataValue>> ReadAsync(
        ISession session,
        IEnumerable<NodeId> nodeIds,
        CancellationToken ct);
}
```

### 2. Extract SessionChangeDetector
Session change detection with value reset is duplicated:
```csharp
internal class SessionChangeDetector
{
    public bool DetectChange(ISession? current);
    public event Action? OnSessionChanged;
}
```

### 3. Consider IAsyncEnumerable
The polling loop could yield results as `IAsyncEnumerable<PropertyUpdate>` for cleaner composition, though current design is simpler.

## Summary

| Aspect | Rating | Notes |
|--------|--------|-------|
| Architecture | ✅ Good | Clean design, proper separation |
| Thread Safety | ✅ Good | Minor issues, no critical problems |
| Test Coverage | ⚠️ Needs Work | No direct unit tests for PollingManager |
| Code Duplication | ⚠️ Moderate | ~100 lines shared with ReadAfterWriteManager |
| Modern C# | ✅ Good | Uses modern patterns correctly |
| SOLID | ✅ Good | Minor SRP improvements possible |
| Dead Code | ✅ None | All code is used |

## Recommendations

### High Priority
1. **Add unit tests** for `PollingManager` core functionality (Start, AddItem, RemoveItem, circuit breaker)
2. **Add integration test** that exercises polling fallback path

### Medium Priority
3. **Fix Volatile.Read** missing at line 453
4. **Fix ResetPolledValues** to use `AddOrUpdate` instead of `TryUpdate`

### Low Priority (Future)
5. **Extract BatchedNodeReader** to reduce duplication with ReadAfterWriteManager
6. **Consider SessionChangeDetector** abstraction if more consumers emerge
