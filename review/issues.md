# PR #121 Review: Outstanding Issues

**Branch:** `feature/opc-ua-full-sync`
**Generated:** 2026-02-04
**Source:** Automated review of 24 files in `review/files/`

---

## Critical Issues (Must Fix)

### 1. CustomNodeManager.ClearPropertyData - No Lock Protection
**File:** `CustomNodeManager.cs:69-92`
**Severity:** CRITICAL
**Type:** Race Condition

`ClearPropertyData()` iterates over `_subjectRegistry.GetAllSubjects()` and root subject properties without holding `_structureLock`. Concurrent structural changes can cause collection-modified exceptions or stale data processing.

**Fix:** Acquire `_structureLock` before iterating.

---

### 2. OpcUaServerNodeCreator - StateChanged Memory Leak
**File:** `OpcUaServerNodeCreator.cs:364-378`
**Severity:** CRITICAL
**Type:** Memory Leak

StateChanged event handlers are subscribed but never unsubscribed. Nodes remain in memory indefinitely even after removal from the address space.

**Fix:** Implement `IDisposable` pattern or explicit unsubscription in `RemoveSubjectNodes`.

---

### 3. OpcUaServerGraphChangePublisher - Lost Updates on Exception
**File:** `OpcUaServerGraphChangePublisher.cs:66-93`
**Severity:** CRITICAL
**Type:** Data Loss

When `ReportEvent` throws, the swapped-out `changesToEmit` list is permanently lost. Clients never receive notification of those changes.

**Fix:** Either re-queue failed changes or use a more robust emission pattern.

---

### 4. GraphChangePublisher._diffBuilder Not Thread-Safe
**File:** `GraphChangePublisher.cs:16`
**Severity:** HIGH
**Type:** Race Condition

The shared mutable `CollectionDiffBuilder _diffBuilder` field has no synchronization. Concurrent calls to `ProcessPropertyChangeAsync` cause internal state corruption.

**Affects:** `OpcUaClientGraphChangeSender`, `OpcUaServerGraphChangeSender`

**Fix:** Make `_diffBuilder` a local variable or add synchronization.

---

## High Priority Issues

### 5. OpcUaServerGraphChangeReceiver - Thread Safety
**File:** `OpcUaServerGraphChangeReceiver.cs`
**Severity:** HIGH (downgraded from CRITICAL)

Model modifications via `GraphChangeApplier` happen before `_structureLock` is acquired. Partial fix implemented but race window remains.

---

### 6. OpcUaSubjectLoader - AddMonitoredItemToSubject Not Thread-Safe
**File:** `OpcUaSubjectLoader.cs:466`
**Severity:** MEDIUM-HIGH

The method modifies subject data without proper synchronization.

---

### 7. OpcUaClientGraphChangeSender - Non-Atomic Shared Subject Check
**File:** `OpcUaClientGraphChangeSender.cs:65-72`
**Severity:** MEDIUM

Check-then-act pattern for shared subjects is not atomic.

---

## Medium Priority Issues

### 8. CustomNodeManager - Synchronous Lock Acquisition
**File:** `CustomNodeManager.cs`
**Severity:** MEDIUM

All locked methods use blocking `.Wait()` instead of `.WaitAsync()`. Can cause thread pool starvation under high load.

---

### 9. OpcUaServerGraphChangeReceiver - TOCTOU in RemoveSubjectFromExternal
**File:** `OpcUaServerGraphChangeReceiver.cs:142-152`
**Severity:** MEDIUM

Time-of-check-time-of-use vulnerability in removal logic.

---

### 10. OpcUaServerGraphChangeReceiver - Collection Index Race
**File:** `OpcUaServerGraphChangeReceiver.cs:344-345`
**Severity:** MEDIUM

Reads `currentCollection?.Count()` before adding to the collection - index could be stale.

---

### 11. OpcUaServerNodeCreator - Recursive Attributes No Depth Limit
**File:** `OpcUaServerNodeCreator.cs:296-297`
**Severity:** MEDIUM

Recursive attribute node creation has no depth limit, could cause stack overflow with circular references.

---

### 12. SessionManager - Risky Sync Dispose Pattern
**File:** `SessionManager.cs:547-561`
**Severity:** MEDIUM

Fire-and-forget pattern in synchronous Dispose could lose pending operations.

---

### 13. SubscriptionManager - O(n²) Item Removal
**File:** `SubscriptionManager.cs:285-288, 343-346`
**Severity:** MEDIUM (Performance)

```csharp
foreach (var item in itemsForThisSubscription)
{
    itemsToAdd.Remove(item);  // O(n) removal in a loop
}
```

---

## Low Priority Issues

### 14. OpcUaSubjectClientSource - Mixed Sync/Async Lock
**File:** `OpcUaSubjectClientSource.cs`
**Severity:** LOW

`RemoveItemsForSubject` uses synchronous `Wait()` while other methods use `WaitAsync()`.

---

### 15. SessionManager - Inconsistent Lock Type
**File:** `SessionManager.cs:26`
**Severity:** LOW

Uses `object` for locking instead of C# 13 `Lock` type used elsewhere in codebase.

---

### 16. OpcUaServerNodeCreator - Lock on Public Object
**File:** `OpcUaServerNodeCreator.cs:370-374`
**Severity:** LOW

Locking on `variableNode` which is a public SDK object - external code could also lock on it.

---

### 17. Magic Numbers Undocumented
**Files:** Multiple
**Severity:** LOW

- `DefaultChunkSize = 512` (WriteRetryQueue)
- `maxDepth = 10` (OpcUaClientGraphChangeReceiver, OpcUaSubjectLoader)

---

### 18. OpcUaClientGraphChangeDispatcher - Redundant CancelAsync
**File:** `OpcUaClientGraphChangeDispatcher.cs`
**Severity:** LOW

`StopAsync` calls `CancelAsync()` after consumer task completion - effectively dead code.

---

## Dead Code / Premature Abstraction

### 19. OpcUaServerExternalNodeValidator - Unused Methods
**File:** `OpcUaServerExternalNodeValidator.cs`
**Severity:** LOW (Cleanup)

`ValidateAddNodes()` and `ValidateDeleteNodes()` are never called in production code. Only `IsEnabled` property is used. The validation logic is duplicated in `OpcUaServerGraphChangeReceiver`.

**Recommendation:** Either use the validator or remove it.

---

### 20. ConnectorSubjectMapping - Obsolete File
**File:** `ConnectorSubjectMapping.cs`
**Severity:** LOW (Cleanup)

Class still exists but functionality merged into `SubjectConnectorRegistry`. Review file should be renamed/merged.

---

## Pattern/Style Issues (Nice to Have)

### 21. OpcUaHelper - Should Be Extension Methods
**File:** `OpcUaHelper.cs`
**Severity:** STYLE

Codebase uses extension methods extensively (25+ classes) but OpcUaHelper uses static methods. Convert to `ISession` extension methods for consistency.

```csharp
// Current
var children = await OpcUaHelper.BrowseNodeAsync(session, nodeId, ct);

// Recommended
var children = await session.BrowseNodeAsync(nodeId, ct);
```

---

### 22. GraphChangePublisher - Wrong Design Pattern
**File:** `GraphChangePublisher.cs`
**Severity:** STYLE

Uses Template Method inheritance pattern, but codebase strongly prefers composition over inheritance (see `IWriteInterceptor`, `IReadInterceptor`, `ChangeQueueProcessor`).

---

## Test Coverage Gaps

| File | Missing Tests |
|------|---------------|
| OpcUaServerGraphChangeReceiver | No unit tests |
| OpcUaServerNodeCreator | No unit tests |
| PollingManager | Core functionality, polling fallback |
| SessionManager | No direct unit tests |
| SubscriptionManager | Concurrent access, subscription transfer |
| WriteRetryQueue | Disposal, partial batch failure |

---

## Files Approved (No Issues)

| File | Status |
|------|--------|
| GraphChangeApplier.cs | APPROVED |
| OpcUaClientPropertyWriter.cs | APPROVED |
| OpcUaTypeRegistry.cs | APPROVED |

---

## Summary

| Priority | Count |
|----------|-------|
| Critical | 4 |
| High | 3 |
| Medium | 6 |
| Low | 5 |
| Dead Code | 2 |
| Style | 2 |
| **Total** | **22** |

---

## Recommended Fix Order

1. **Critical race conditions** (#1, #4) - Can cause data corruption
2. **Memory leak** (#2) - Causes gradual degradation
3. **Data loss** (#3) - Silent failures
4. **High priority thread safety** (#5, #6, #7)
5. **Performance** (#13)
6. **Cleanup dead code** (#19, #20)
7. **Style consistency** (#21, #22)
