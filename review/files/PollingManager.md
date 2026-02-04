# PollingManager.cs - Code Review

**Status:** ✅ Complete
**Reviewer:** Claude
**File:** `src/Namotion.Interceptor.OpcUa/Client/Polling/PollingManager.cs`
**Lines:** 477
**Last Updated:** 2026-02-04

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
- **Thread-safe Start()**: Disposal check inside `_startLock` prevents race conditions

### Design Concerns

**1. Code Duplication with ReadAfterWriteManager**

Both classes share similar patterns:
- CircuitBreaker initialization and usage
- Session change detection logic
- Batched `ReadValueIdCollection` + `session.ReadAsync()` pattern
- Disposal patterns with CancellationTokenSource

**ReadAfterWriteManager** (lines 289-304):
```csharp
var readValues = new ReadValueIdCollection(dueCount);
for (var i = 0; i < dueCount; i++)
{
    readValues.Add(new ReadValueId { NodeId = ..., AttributeId = Attributes.Value });
}
var response = await session.ReadAsync(..., readValues, cancellationToken);
```

**PollingManager** (lines 341-357):
```csharp
var nodesToRead = new ReadValueIdCollection(batch.Count);
foreach (var item in batch)
{
    nodesToRead.Add(new ReadValueId { NodeId = item.NodeId, AttributeId = Attributes.Value });
}
var response = await session.ReadAsync(..., nodesToRead, cancellationToken);
```

**Recommendation**: Consider extracting a shared `BatchedNodeReader` helper for consistency.

**2. Timer Strategy Differs**

- `PollingManager`: Uses `PeriodicTimer` (fixed interval, always running)
- `ReadAfterWriteManager`: Uses `Timer` with dynamic rescheduling (event-driven)

Both are valid for their use cases - `PeriodicTimer` is appropriate for continuous polling while `Timer` is better for on-demand scheduling.

## Thread Safety Analysis

### Overall Assessment: Good ✅

The class demonstrates good thread-safety practices.

### Issues Identified

| Issue | Severity | Location | Description |
|-------|----------|----------|-------------|
| Inconsistent Volatile.Read | Very Low | Line 453 | `_pollingTask` check could use Volatile.Read for consistency |

### Details

**1. Inconsistent Volatile.Read (Very Low)**
```csharp
// Line 453 in DisposeAsync:
if (_pollingTask != null)

// vs Line 103, 121 use Volatile.Read:
var task = Volatile.Read(ref _pollingTask);
```

The plain null check at line 453 is safe because it's already guarded by `Interlocked.Exchange(ref _disposed, 1)`, but for consistency with other usages in the file, it could use `Volatile.Read`.

**Note**: The `ResetPolledValues()` method at line 317 using `TryUpdate` is intentional - the comment explains that silent failure on concurrent removal is the desired behavior.

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

### Single Responsibility: Good ✅

The class has one clear purpose: poll OPC UA nodes. Sub-responsibilities are appropriately encapsulated:
- `PollingMetrics` for metrics tracking
- `CircuitBreaker` for resilience
- `SubjectPropertyWriter` for value application

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

### Unit Tests: Needs Work

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

### Integration Tests: Indirect Coverage

No integration tests specifically enable `EnablePollingFallback = true` and simulate subscription failures.

**Recommendation**: Add integration test that:
1. Configures a server node to reject subscriptions
2. Enables `EnablePollingFallback = true`
3. Verifies values synchronize via polling

## Modern C# Best Practices

### Good ✅
- Uses `record struct` for `PollingItem` (line 471)
- Uses `Lock` class instead of `object` for locking (line 31)
- Uses `PeriodicTimer` for modern async timer pattern
- Uses `Volatile` and `Interlocked` correctly
- Uses `ConfigureAwait(false)` consistently
- Uses collection expressions where appropriate
- Uses `ArraySegment<T>` to avoid allocations
- Proper disposal pattern with `Interlocked.Exchange`

## Dead Code / Unused Code

None found. All public methods are called from `SubscriptionManager` or `SessionManager`.

## Potential Simplifications

### 1. Extract BatchedNodeReader (Low Priority)
Similar read patterns exist in `ReadAfterWriteManager`. Could potentially extract:
```csharp
internal static class BatchedNodeReader
{
    public static async Task<IReadOnlyList<DataValue>> ReadAsync(
        ISession session,
        IEnumerable<NodeId> nodeIds,
        CancellationToken ct);
}
```
However, the current implementation is clear and the duplication is minimal.

## Summary

| Aspect | Rating | Notes |
|--------|--------|-------|
| Architecture | ✅ Good | Clean design, proper separation |
| Thread Safety | ✅ Good | No critical issues, proper use of Volatile/Interlocked |
| Test Coverage | ⚠️ Needs Work | No direct unit tests for PollingManager |
| Code Duplication | ✅ Acceptable | Similar patterns exist but duplication is minimal |
| Modern C# | ✅ Good | Uses modern patterns correctly |
| SOLID | ✅ Good | Clean separation of concerns |
| Dead Code | ✅ None | All code is used |

## Recommendations

### High Priority
1. **Add unit tests** for `PollingManager` core functionality (Start, AddItem, RemoveItem, circuit breaker)
2. **Add integration test** that exercises polling fallback path

### Low Priority
3. **Consistency**: Consider using `Volatile.Read` at line 453 for consistency with other usages (very minor)
