# ConnectorReferenceCounter.cs Review

**Status:** Complete
**Reviewer:** Claude
**File:** `src/Namotion.Interceptor.Connectors/ConnectorReferenceCounter.cs`
**Lines:** 113

## Overview

`ConnectorReferenceCounter<TData>` tracks connector-scoped reference counts for subjects with associated data. It's the companion class to `ConnectorSubjectMapping`, used together in OPC UA client and server components to manage subject lifecycle with connector-specific data (e.g., `NodeState`, `List<MonitoredItem>`).

### Usage Sites
- `OpcUaSubjectClientSource` - owns instance, TData=`List<MonitoredItem>`
- `OpcUaServerNodeCreator` - receives via DI, TData=`NodeState`
- `CustomNodeManager` - owns instance, TData=`NodeState`
- `OpcUaServerGraphChangeReceiver` - receives via DI (read-only lookups only)

---

## Correctness Analysis

### Thread Safety (Internal): PASS
- Uses C# 13 `Lock` type for all operations
- All methods acquire lock before accessing dictionary
- `GetAllEntries()` and `Clear()` return `.ToList()` snapshots

### Reference Counting Logic: PASS
- `IncrementAndCheckFirst()` correctly returns `true` only for first reference
- `DecrementAndCheckLast()` correctly returns `true` only when last reference removed
- Factory pattern ensures data creation happens exactly once per subject

### Factory Execution: CONCERN
The factory is called **inside the lock** (line 32):
```csharp
lock (_lock)
{
    // ...
    data = dataFactory();  // ⚠️ INSIDE LOCK
    _entries[subject] = (1, data);
}
```
**Current status:** Safe because all factories in codebase are O(1) (`() => []`).
**Risk:** If factory performs blocking I/O, will stall all concurrent operations.

---

## Critical Issues Found

### 1. Race Condition in TrackSubject (CRITICAL)

**Location:** `OpcUaSubjectClientSource.cs:196-204`

```csharp
var isFirst = _subjectRefCounter.IncrementAndCheckFirst(subject, monitoredItemsFactory, out _);
if (isFirst)
{
    _subjectMapping.Register(subject, nodeId);  // ⚠️ RACE WINDOW
}
```

**Problem:** The two operations are not atomic. Between `IncrementAndCheckFirst` releasing its lock and `Register` acquiring its lock, another thread could:
1. Call `DecrementAndCheckLast` (sees ref count = 1, decrements to 0, removes)
2. Call `Unregister` (removes from mapping)
3. First thread then calls `Register` on a subject being removed

**Contrast with server-side (correct):** `CustomNodeManager.RemoveSubjectNodes()` wraps both operations in `_structureLock`.

**Recommendation:** Wrap both calls in `_structureLock` in TrackSubject:
```csharp
await _structureLock.WaitAsync();
try
{
    var isFirst = _subjectRefCounter.IncrementAndCheckFirst(...);
    if (isFirst)
    {
        _subjectMapping.Register(subject, nodeId);
    }
}
finally
{
    _structureLock.Release();
}
```

### 2. TData Cleanup Not Captured (MEDIUM)

**Location:** `OpcUaSubjectClientSource.cs:106, 870`

```csharp
// Line 106 - returned data discarded
var isLast = _subjectRefCounter.DecrementAndCheckLast(subject, out _);

// Line 870 - return value ignored
_subjectRefCounter.Clear();
```

**Problem:** If `TData` implements `IDisposable`, resources leak. Currently safe because `List<MonitoredItem>` doesn't need disposal, but architecture is fragile.

**Recommendation:** Capture and handle returned data:
```csharp
var isLast = _subjectRefCounter.DecrementAndCheckLast(subject, out var data);
if (isLast && data is IDisposable disposable)
{
    disposable.Dispose();
}
```

### 3. Pairing Inconsistency Risk (MEDIUM)

**Problem:** `ConnectorReferenceCounter` and `ConnectorSubjectMapping` must always stay synchronized, but they use separate locks. If one operation succeeds and the other fails:

| Scenario | Result |
|----------|--------|
| Increment succeeds, Register throws | Counter has entry, mapping empty |
| Decrement succeeds, Unregister throws | Counter removed, mapping has stale entry |

**Current mitigation:** `CustomNodeManager` uses external `_structureLock` for atomic operations. `OpcUaSubjectClientSource.TrackSubject` does NOT.

---

## Architectural Analysis

### Should This Class Exist?

**Question:** Given 70-80% code duplication with `ConnectorSubjectMapping`, should they be merged?

**Analysis:**

| Aspect | ConnectorReferenceCounter | ConnectorSubjectMapping |
|--------|---------------------------|-------------------------|
| Purpose | Subject → Data + ref count | Subject ↔ ExternalId + ref count |
| Lookup | One-way (subject → data) | Bidirectional |
| Unique API | Factory pattern | `UpdateExternalId`, `TryGetSubject` |
| Type param | `TData` (any) | `TExternalId` (notnull) |

**Finding:** They solve different parts of the same problem (tracking connected subjects). The separation creates:
- 70-80% duplicated reference counting logic
- Risk of inconsistency when both must be updated
- No atomic guarantee across both structures

### Recommendation: Consider Merging

**Option A (Recommended):** Create unified `SubjectConnectorRegistry<TData, TExternalId>`:
```csharp
public class SubjectConnectorRegistry<TData, TExternalId> where TExternalId : notnull
{
    // Single lock, single atomic operation
    public bool RegisterAndTrack(IInterceptorSubject subject,
        Func<TData> dataFactory, TExternalId externalId,
        out TData data)
    {
        lock (_lock)
        {
            // Atomic: increment ref + set data + set ID + reverse mapping
        }
    }
}
```

**Benefits:**
- Single atomic lock for all operations
- Eliminates race conditions between paired operations
- Reduces code duplication ~40%
- Clearer intent

**Option B:** Keep separate but formalize pairing with coordinator class that handles rollback on failure.

**Option C:** Document that they MUST be used with external lock (current implicit contract).

---

## Code Quality Assessment

### Modern C# Best Practices: PASS
- Uses C# 13 `Lock` type
- Uses value tuples `(int Count, TData Data)`
- Proper nullable annotations
- Generic with clear constraints

### SOLID Principles: QUESTIONABLE
- **Single Responsibility:** Partially - does ref counting AND data storage
- **DRY Violation:** 70-80% duplication with `ConnectorSubjectMapping`

### XML Documentation: PASS
- All public methods documented
- Class-level documentation explains purpose

---

## Test Coverage

| Method | Unit Test | Concurrent Test | Integration |
|--------|-----------|-----------------|-------------|
| `IncrementAndCheckFirst()` | ✅ 2 tests | ✅ 100 threads | ✅ OPC UA |
| `DecrementAndCheckLast()` | ✅ 3 tests | ✅ 100 threads | ✅ OPC UA |
| `TryGetData()` | ✅ 2 tests | ❌ **MISSING** | ✅ OPC UA |
| `Clear()` | ✅ 2 tests | ❌ **MISSING** | ✅ OPC UA |
| `GetAllEntries()` | ✅ 2 tests | ❌ **MISSING** | - |
| Factory exception | ❌ **MISSING** | - | - |

**Overall Coverage:** ~75% (Good)

**Missing Tests:**
1. Concurrent access for `TryGetData()`, `Clear()`, `GetAllEntries()`
2. Exception handling when `dataFactory` throws
3. Multiple subjects under concurrent load

---

## Dead/Unused Code

**None in the class itself.**

**Potential dead parameter:**
`OpcUaServerGraphChangeReceiver` receives `ConnectorReferenceCounter` but only calls `TryGetData()` for read-only lookups - never increments or decrements. Could potentially be replaced with read-only interface or removed if lookup is available elsewhere.

---

## Recommendations Summary

| Priority | Issue | Action |
|----------|-------|--------|
| **CRITICAL** | Race condition in TrackSubject | Wrap in _structureLock |
| **HIGH** | Pairing inconsistency risk | Consider merging classes OR add coordinator |
| **MEDIUM** | TData cleanup not captured | Capture and dispose returned data |
| **MEDIUM** | Code duplication | Extract shared base or merge |
| **LOW** | Missing concurrent tests | Add tests for TryGetData, Clear, GetAllEntries |
| **LOW** | Factory inside lock | Document constraint or move outside |

---

## Verdict

**Overall:** Working correctly internally, but **critical issues at caller level** and **architectural concerns** about class separation.

The internal implementation is sound and well-tested for core operations. However:
1. **Race condition** in `OpcUaSubjectClientSource.TrackSubject` needs immediate fix
2. **Pairing with ConnectorSubjectMapping** creates consistency risks that should be addressed architecturally
3. **Code duplication** suggests the two classes should be merged or share a base

**Recommendation:** Fix critical race condition, then consider architectural unification with `ConnectorSubjectMapping` in a follow-up PR.
