# Code Review: OpcUaHelper.cs

**File:** `src/Namotion.Interceptor.OpcUa/OpcUaHelper.cs`
**Lines:** ~247
**Status:** Complete
**Reviewer:** Claude
**Date:** 2026-02-02

---

## Overview

`OpcUaHelper` is a static utility class providing OPC UA browse operations and collection index parsing. It consolidates common OPC UA patterns used across client and server code.

### Methods
| Method | Lines | Purpose |
|--------|-------|---------|
| `FindChildNodeIdAsync` | 19-34 | Find child node by browse name |
| `FindParentNodeIdAsync` | 43-54 | Find parent via inverse references |
| `ReadNodeDetailsAsync` | 63-96 | Read node attributes (BrowseName, DisplayName, NodeClass) |
| `TryParseCollectionIndex` (overload 1) | 106-135 | Parse "Name[index]" → baseName + index |
| `TryParseCollectionIndex` (overload 2) | 144-168 | Validate "Name[index]" with expected name |
| `BrowseInverseReferencesAsync` | 177-204 | Browse parent references |
| `BrowseNodeAsync` | 213-246 | Browse child references |

---

## ARCHITECTURAL ANALYSIS

### Does This Class Make Sense?

**Verdict: Yes, but WRONG PATTERN for this codebase**

#### Current Design
```csharp
internal static class OpcUaHelper
{
    public static async Task<NodeId?> FindChildNodeIdAsync(
        ISession session, NodeId parentNodeId, string browseName, ...)
}
```

#### Problem: Inconsistent with Codebase Patterns

| Pattern | Count in Codebase | OpcUaHelper |
|---------|-------------------|-------------|
| Extension methods | **25+ classes** | ❌ Not used |
| Static helpers | 2 classes | ✅ Current |

The codebase strongly prefers extension methods (`InterceptorSubjectExtensions`, `SubjectRegistryExtensions`, `OpcUaPropertyExtensions`, etc.) but OpcUaHelper uses static methods.

#### Recommended: Convert to Extension Methods

```csharp
// Current usage:
var children = await OpcUaHelper.BrowseNodeAsync(session, nodeId, ct);

// Recommended (extension method):
var children = await session.BrowseNodeAsync(nodeId, ct);
```

**Benefits:**
- Aligns with 25+ extension classes in codebase
- Better discoverability via IntelliSense on `ISession`
- More fluent: `session.BrowseNodeAsync()` vs `OpcUaHelper.BrowseNodeAsync(session, ...)`

---

## CRITICAL ISSUES

### Issue 1: Missing Pagination/Continuation Point Handling (HIGH)

**Location:** Lines 195-196, 233-238

**Problem:** OPC UA browse can return partial results with continuation points. `BrowseNodeAsync` and `BrowseInverseReferencesAsync` don't handle pagination.

**Compare with OpcUaSubjectLoader.cs (lines 512-570):**
```csharp
// OpcUaSubjectLoader handles pagination:
continuationPoint = response.Results[0].ContinuationPoint;
while (continuationPoint is { Length: > 0 })
{
    // ... call BrowseNextAsync to get remaining results
}
```

**Impact:** If a node has many children, only the first batch is returned, causing data loss.

**Recommendation:** Add continuation point handling or document the limitation.

---

### Issue 2: No Exception Handling (HIGH)

**Location:** All async methods (lines 25, 48, 75-80, 195-196, 233-238)

**Problem:** No try-catch around session operations. Compare with callers:
- `OpcUaClientGraphChangeSender.cs:443` - catches `ServiceResultException`
- `OpcUaSubjectClientSource.cs:180` - catches `ServiceResultException`
- `OpcUaTypeResolver.cs:102-105` - catches `Exception`

**Impact:** Session disconnection during browse propagates uncaught exceptions.

**Recommendation:** Add consistent exception handling or document that callers must handle exceptions.

---

### Issue 3: Unsafe Null Cast (MEDIUM)

**Location:** Line 91

```csharp
NodeClass = (NodeClass)(int)response.Results[2].Value  // UNSAFE
```

**Problem:** Direct cast without null check. If `Value` is null, throws `NullReferenceException`.

**Fix:**
```csharp
NodeClass = response.Results[2].Value is int nodeClass
    ? (NodeClass)nodeClass
    : NodeClass.Unspecified
```

---

## CODE DUPLICATION ISSUES

### Issue 4: TryParseCollectionIndex Overload Duplication (MEDIUM)

**Location:** Lines 106-135 and 144-168

**TODO in code (line 108):**
```csharp
// TODO: Do we really need both overloads? Seems like a lot of duplication...
```

**Analysis:** Both overloads contain identical parsing logic (~40% duplicated).

**Recommendation:** Consolidate:
```csharp
public static bool TryParseCollectionIndex(
    string browseName, string? propertyName, out int index)
{
    if (!TryParseCollectionIndex(browseName, out var baseName, out index))
        return false;
    return baseName == propertyName;
}
```

---

### Issue 5: BrowseNodeAsync / BrowseInverseReferencesAsync Duplication (MEDIUM)

**Location:** Lines 177-204 and 213-246

**Problem:** 80% identical code - only `BrowseDirection` differs.

**Recommendation:** Extract shared helper:
```csharp
private static async Task<ReferenceDescriptionCollection> BrowseAsync(
    ISession session, NodeId nodeId, BrowseDirection direction, CancellationToken ct)
{
    // Shared implementation
}

public static Task<ReferenceDescriptionCollection> BrowseNodeAsync(...)
    => BrowseAsync(session, nodeId, BrowseDirection.Forward, ct);

public static Task<ReferenceDescriptionCollection> BrowseInverseReferencesAsync(...)
    => BrowseAsync(session, nodeId, BrowseDirection.Inverse, ct);
```

---

### Issue 6: Duplicate Browse Logic in OpcUaSubjectLoader (MEDIUM)

**Location:** `OpcUaSubjectLoader.cs` lines 512-568

**Problem:** Contains a private `BrowseNodeAsync` that duplicates `OpcUaHelper.BrowseNodeAsync` but WITH pagination.

**Recommendation:** Enhance `OpcUaHelper.BrowseNodeAsync` to support pagination, then delete duplicate.

---

## USAGE ANALYSIS

### Method Call Counts

| Method | Calls | Primary Users |
|--------|-------|---------------|
| `BrowseNodeAsync` | 13 | OpcUaClientGraphChangeReceiver, OpcUaClientGraphChangeSender |
| `FindChildNodeIdAsync` | 6 | OpcUaClientGraphChangeReceiver |
| `TryParseCollectionIndex` (both) | 4 | OpcUaSubjectLoader, OpcUaClientGraphChangeReceiver, OpcUaServerGraphChangeReceiver |
| `FindParentNodeIdAsync` | 2 | OpcUaClientGraphChangeReceiver |
| `ReadNodeDetailsAsync` | 1 | OpcUaClientGraphChangeReceiver |
| `BrowseInverseReferencesAsync` | 0 | Only called internally by FindParentNodeIdAsync |

### Duplicate Patterns Not Using OpcUaHelper

**cleanup.md mentions these should be consolidated:**

1. **`OpcUaClientGraphChangeSender.TryFindChildNodeAsync`** (lines 166-272)
   - Re-implements browse and search logic
   - Should use `OpcUaHelper.FindChildNodeIdAsync` instead
   - **~100 lines could be reduced**

2. **`OpcUaSubjectLoader.BrowseNodeAsync`** (lines 512-568)
   - Duplicates OpcUaHelper but adds pagination
   - Should be consolidated

---

## THREAD SAFETY ANALYSIS

| Aspect | Status | Notes |
|--------|--------|-------|
| Shared state | ✅ None | Static class with no static fields |
| Concurrent calls | ✅ Safe | Pure functions, no side effects |
| ISession thread safety | ⚠️ Relies on SDK | OPC UA SDK handles session concurrency |

**Verdict:** Thread-safe by design (stateless utility methods).

---

## TEST COVERAGE ANALYSIS

| Method | Unit Tests | Integration Tests |
|--------|------------|-------------------|
| `TryParseCollectionIndex` (overload 1) | ✅ 9 test cases | Implicit |
| `TryParseCollectionIndex` (overload 2) | ✅ 3 test cases | Implicit |
| `FindChildNodeIdAsync` | ❌ None | ✅ Exercised in receivers |
| `FindParentNodeIdAsync` | ❌ None | ✅ Exercised in receivers |
| `ReadNodeDetailsAsync` | ❌ None | ✅ Exercised in receivers |
| `BrowseNodeAsync` | ❌ None | ✅ Heavily exercised |
| `BrowseInverseReferencesAsync` | ❌ None | ✅ Via FindParentNodeIdAsync |

**Test File:** `OpcUaHelperTests.cs`

**Edge Cases Tested for TryParseCollectionIndex:**
- ✅ Empty strings
- ✅ Missing brackets
- ✅ Empty brackets `[]`
- ✅ Negative indices
- ✅ Non-integer indices
- ✅ Large indices
- ✅ Property name mismatches

**Gap:** Async methods have no direct unit tests. Would require mocking `ISession`.

---

## ASYNC PATTERN ANALYSIS

| Check | Status | Notes |
|-------|--------|-------|
| ConfigureAwait(false) | ✅ All methods | Lines 25, 48, 80, 196, 238 |
| CancellationToken | ✅ Passed through | All async methods |
| Blocking calls | ✅ None | Pure async |

---

## SOLID PRINCIPLES ASSESSMENT

| Principle | Status | Notes |
|-----------|--------|-------|
| **S**ingle Responsibility | ✅ | OPC UA browse utilities only |
| **O**pen/Closed | ⚠️ | Static class can't be extended |
| **L**iskov Substitution | N/A | No inheritance |
| **I**nterface Segregation | ⚠️ | No interface defined |
| **D**ependency Inversion | ❌ | Callers depend on concrete static class |

---

## Summary

| Category | Rating | Notes |
|----------|--------|-------|
| Correctness | ⚠️ Issues | Missing pagination, unsafe cast |
| Architecture | ⚠️ Needs Work | Should be extension methods |
| Thread Safety | ✅ Good | Stateless, pure functions |
| Test Coverage | ⚠️ Partial | Good for parsing, none for async |
| Code Quality | ⚠️ Duplication | TODO acknowledged, 2 consolidation opportunities |
| Error Handling | ❌ Missing | No exception handling |

---

## Recommendations

### Must Fix (HIGH)

1. **Add pagination support** to `BrowseNodeAsync` and `BrowseInverseReferencesAsync`
   - Nodes with many children return incomplete results
   - **Effort:** ~30 minutes

2. **Fix unsafe cast on line 91**
   - Add null check before casting NodeClass
   - **Effort:** ~5 minutes

3. **Add exception handling** or document that callers must handle `ServiceResultException`
   - **Effort:** ~15 minutes

### Should Fix (MEDIUM)

4. **Consolidate TryParseCollectionIndex overloads** (resolve TODO on line 108)
   - **Effort:** ~15 minutes

5. **Extract shared BrowseAsync helper** to reduce Browse/BrowseInverse duplication
   - **Effort:** ~20 minutes

6. **Consolidate OpcUaSubjectLoader.BrowseNodeAsync** into OpcUaHelper
   - **Effort:** ~30 minutes

### Consider (LOW)

7. **Convert to extension methods** on `ISession`
   - Aligns with codebase patterns (25+ extension classes)
   - **Effort:** ~1 hour (all call sites need updating)

8. **Add unit tests for async methods** with mocked ISession
   - **Effort:** ~2 hours

9. **Consolidate OpcUaClientGraphChangeSender.TryFindChildNodeAsync**
   - Should delegate to OpcUaHelper.FindChildNodeIdAsync
   - **Effort:** ~1 hour

---

## Related Files

- `OpcUaSubjectLoader.cs` - Contains duplicate BrowseNodeAsync with pagination
- `OpcUaClientGraphChangeSender.cs` - Contains TryFindChildNodeAsync that should use helper
- `OpcUaClientGraphChangeReceiver.cs` - Primary consumer of helper methods
- `OpcUaServerGraphChangeReceiver.cs` - Uses TryParseCollectionIndex
- `OpcUaHelperTests.cs` - Unit tests for parsing methods
