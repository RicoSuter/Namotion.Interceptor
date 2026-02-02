# Code Review: OpcUaServerGraphChangeSender.cs

**File:** `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerGraphChangeSender.cs`
**Lines:** 45
**Status:** Complete
**Reviewer:** Claude
**Date:** 2026-02-02

---

## Overview

`OpcUaServerGraphChangeSender` is a minimal subclass of `GraphChangePublisher` that processes structural property changes (add/remove subjects) for the OPC UA server. It delegates all node operations to `CustomNodeManager`.

```csharp
internal class OpcUaServerGraphChangeSender : GraphChangePublisher
{
    private readonly CustomNodeManager _nodeManager;

    protected override Task OnSubjectAddedAsync(...)
        => { _nodeManager.CreateSubjectNode(...); return Task.CompletedTask; }

    protected override Task OnSubjectRemovedAsync(...)
        => { _nodeManager.RemoveSubjectNodes(...); _nodeManager.ReindexCollectionBrowseNames(...); }
}
```

---

## ARCHITECTURAL ANALYSIS

### Does This Class Make Sense?

**Verdict: Class is CORRECT but EXISTS ONLY due to Template Method pattern**

The class is functionally correct, but its existence is a symptom of the architectural issue identified in the `GraphChangePublisher` review. If `GraphChangePublisher` is refactored to interface-based composition, this class becomes unnecessary.

### Asymmetry with OpcUaClientGraphChangeSender

| Aspect | Server (45 lines) | Client (588 lines) |
|--------|-------------------|-------------------|
| Add operation | 1 method call | 8 complex operations |
| Remove operation | 2 method calls | No-op (delegated elsewhere) |
| Why small | Direct in-process control | Must query/modify remote server |

**The asymmetry is JUSTIFIED:**
- Server synchronously modifies its own address space
- Client must: validate session, discover nodes, call AddNodes RPC, write values, manage subscriptions

### With Recommended IGraphChangeHandler Interface

If `GraphChangePublisher` is refactored as recommended:

```csharp
// Current (Template Method - requires subclass)
internal class OpcUaServerGraphChangeSender : GraphChangePublisher { ... }

// Recommended (Interface - can be inline or simple class)
var handler = new DelegateGraphChangeHandler(
    (p, s, i) => { nodeManager.CreateSubjectNode(p, s, i); return Task.CompletedTask; },
    (p, s, i) => { nodeManager.RemoveSubjectNodes(s, p); ... });
_graphChangeProcessor = new GraphChangePublisher(handler);
```

**Result:** This class can be eliminated or replaced with a simpler handler implementation.

---

## CRITICAL ISSUES

### Issue 1: Race Condition Between Remove and Reindex (CRITICAL)

**Location:** Lines 32-41

```csharp
protected override Task OnSubjectRemovedAsync(...)
{
    _nodeManager.RemoveSubjectNodes(subject, property);  // Lock acquired, then released

    if (property.IsSubjectCollection)
    {
        _nodeManager.ReindexCollectionBrowseNames(property);  // Lock acquired AGAIN
    }
    return Task.CompletedTask;
}
```

**Problem:** Two separate lock acquisitions create a race window:

```
T1: RemoveSubjectNodes() releases _structureLock
T2: [WINDOW] Another thread could call CreateSubjectNode() or RemoveSubjectNodes()
T3: ReindexCollectionBrowseNames() acquires _structureLock
T4: Reindexing operates on potentially STALE child list
```

**Potential Consequences:**
- BrowseName sequence could become non-contiguous: `People[0], People[1], People[3]`
- New node added between operations might get overwritten

**Recommendation:** Create atomic method in CustomNodeManager:
```csharp
public void RemoveSubjectNodesAndReindex(
    IInterceptorSubject subject,
    RegisteredSubjectProperty property)
{
    _structureLock.Wait();
    try
    {
        RemoveSubjectNodesCore(subject, property);  // Private, no lock
        if (property.IsSubjectCollection)
            ReindexCollectionBrowseNamesCore(property);  // Private, no lock
    }
    finally { _structureLock.Release(); }
}
```

---

### Issue 2: Parent Class Not Thread-Safe (HIGH)

**Location:** Inherited from `GraphChangePublisher`

The parent class has a shared `CollectionDiffBuilder _diffBuilder` instance without synchronization. If `ProcessPropertyChangeAsync` is called concurrently, the internal state gets corrupted.

**Current Safety:** Safe only because `WriteChangesAsync` processes changes sequentially with `await`.

**Recommendation:** Document threading requirement or add synchronization.

---

### Issue 3: No Error Handling (MEDIUM)

**Location:** Lines 25-28, 32-43

```csharp
protected override Task OnSubjectAddedAsync(...)
{
    _nodeManager.CreateSubjectNode(property, subject, index);  // Can throw!
    return Task.CompletedTask;
}
```

**Problem:** If `CreateSubjectNode` throws, exception propagates to background service.

**Recommendation:** Add try-catch with logging, or document that caller must handle exceptions.

---

## USAGE ANALYSIS

### Instantiation
```csharp
// OpcUaSubjectServerBackgroundService.cs:179
if (_configuration.EnableGraphChangePublishing && server.NodeManager is not null)
{
    _graphChangeSender = new OpcUaServerGraphChangeSender(server.NodeManager);
}
```

### Lifecycle
- **Created:** When server starts and `EnableGraphChangePublishing` is true
- **Destroyed:** Set to `null` on shutdown (line 196)
- **Scope:** Tied to server instance lifetime

### Call Site
```csharp
// OpcUaSubjectServerBackgroundService.cs:95-108
var handled = await _graphChangeSender
    .ProcessPropertyChangeAsync(change, registeredProperty)
    .ConfigureAwait(false);

if (handled) { /* skip value processing */ }
```

---

## TEST COVERAGE ANALYSIS

### Unit Tests
**File:** `OpcUaServerGraphChangeSenderTests.cs`

| Test | Coverage |
|------|----------|
| `ProcessPropertyChangeAsync_SubjectAdded_CallsOnSubjectAdded` | ✅ Reference add |
| `ProcessPropertyChangeAsync_SubjectRemoved_CallsOnSubjectRemoved` | ✅ Reference remove |
| `ProcessPropertyChangeAsync_ValueChange_ReturnsFalse` | ✅ Non-structural |
| `ProcessPropertyChangeAsync_CollectionAdd_CallsOnSubjectAdded` | ✅ Collection add with index |
| `ProcessPropertyChangeAsync_CollectionRemove_CallsOnSubjectRemoved` | ✅ Collection remove |
| `ProcessPropertyChangeAsync_DictionaryAdd_CallsOnSubjectAdded` | ✅ Dictionary with key |

### Integration Tests
**File:** `ServerToClientCollectionTests.cs`

| Test | Coverage |
|------|----------|
| `AddToContainerCollection_ClientReceivesChange` | ✅ Container mode add |
| `RemoveFromContainerCollection_ClientReceivesChange` | ✅ Container mode remove |
| `RemoveMiddleItem_BrowseNamesReindexed` | ✅ **Key test for ReindexCollectionBrowseNames** |
| `MultipleAddRemove_SequentialOperations` | ✅ Sequential operations |

**Verdict:** Excellent test coverage for all code paths.

---

## THREAD SAFETY ANALYSIS

| Component | Status | Notes |
|-----------|--------|-------|
| `OpcUaServerGraphChangeSender` | ⚠️ Relies on parent | No additional state |
| `GraphChangePublisher._diffBuilder` | ❌ Not thread-safe | Assumes sequential calls |
| `CustomNodeManager.CreateSubjectNode` | ✅ Protected | Uses `_structureLock` |
| `CustomNodeManager.RemoveSubjectNodes` | ✅ Protected | Uses `_structureLock` |
| `Remove-then-Reindex sequence` | ❌ **Race condition** | Two separate lock acquisitions |

---

## CODE QUALITY

### Strengths
1. **Clean delegation**: All complexity in CustomNodeManager
2. **Clear documentation**: XML docs explain purpose
3. **Minimal footprint**: Only 45 lines
4. **Correct async pattern**: Uses `Task.CompletedTask` for sync operations

### Weaknesses
1. **Exists due to inheritance**: Would be unnecessary with interface composition
2. **Race condition in removal**: Two-step lock acquisition
3. **No error handling**: Exceptions propagate uncaught

---

## SOLID PRINCIPLES

| Principle | Status | Notes |
|-----------|--------|-------|
| **S**ingle Responsibility | ✅ | Only handles server graph change sending |
| **O**pen/Closed | ⚠️ | Locked to Template Method pattern |
| **L**iskov Substitution | ✅ | Correctly extends GraphChangePublisher |
| **I**nterface Segregation | ❌ | No interface, forced to subclass |
| **D**ependency Inversion | ❌ | Depends on concrete GraphChangePublisher |

---

## Summary

| Category | Rating | Notes |
|----------|--------|-------|
| Correctness | ⚠️ Issue | Race condition in remove-then-reindex |
| Architecture | ⚠️ Needs Work | Exists only due to Template Method pattern |
| Thread Safety | ⚠️ Fragile | Race window, relies on parent's sequential assumption |
| Test Coverage | ✅ Excellent | Unit + integration tests cover all paths |
| Code Quality | ✅ Good | Clean, minimal, well-documented |

---

## Recommendations

### Must Fix (CRITICAL)

1. **Fix race condition in removal sequence**
   - Create atomic `RemoveSubjectNodesAndReindex` in CustomNodeManager
   - Single lock acquisition for both operations
   - **Effort:** ~30 minutes

### Should Consider (MEDIUM)

2. **Refactor with GraphChangePublisher** (per GraphChangePublisher review)
   - When GraphChangePublisher moves to interface composition
   - This class can be eliminated or simplified to handler
   - **Effort:** Part of GraphChangePublisher refactoring

3. **Add error handling**
   - Wrap CustomNodeManager calls in try-catch
   - Log and handle gracefully
   - **Effort:** ~15 minutes

### Documentation (LOW)

4. **Document the asymmetry with client**
   - Explain why server is 45 lines vs client's 588 lines
   - Clarify that removal cleanup is done differently
   - **Effort:** ~10 minutes

---

## Related Files

- `GraphChangePublisher.cs` - Base class (needs interface refactoring)
- `CustomNodeManager.cs` - Contains actual node operations
- `OpcUaClientGraphChangeSender.cs` - Client counterpart (588 lines)
- `OpcUaSubjectServerBackgroundService.cs` - Usage context
- `OpcUaServerGraphChangeSenderTests.cs` - Unit tests
- `ServerToClientCollectionTests.cs` - Integration tests
