# Code Review: OpcUaHelper.cs

**File:** `src/Namotion.Interceptor.OpcUa/OpcUaHelper.cs`
**Lines:** ~325
**Status:** Complete
**Reviewer:** Claude
**Date:** 2026-02-04

---

## Overview

`OpcUaHelper` is a static utility class providing OPC UA browse operations and collection index parsing. It consolidates common OPC UA patterns used across client and server code.

### Methods
| Method | Lines | Purpose |
|--------|-------|---------|
| `FindChildNodeIdAsync` | 19-34 | Find child node by browse name |
| `FindParentNodeIdAsync` | 43-54 | Find parent via inverse references |
| `ReadNodeDetailsAsync` | 63-96 | Read node attributes (BrowseName, DisplayName, NodeClass) |
| `TryParseCollectionIndex` (overload 1) | 106-133 | Parse "Name[index]" → baseName + index |
| `TryParseCollectionIndex` (overload 2) | 143-158 | Validate "Name[index]" with expected name (delegates to overload 1) |
| `ReindexFirstCollectionIndex` | 169-189 | Reindex first occurrence of collection index in NodeId string |
| `BrowseInverseReferencesAsync` | 198-252 | Browse parent references with pagination |
| `BrowseNodeAsync` | 262-323 | Browse child references with pagination |

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

### Issue 1: Unsafe Null Cast (MEDIUM)

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

### Issue 2: No Exception Handling (LOW)

**Location:** All async methods

**Problem:** No try-catch around session operations. Callers handle exceptions inconsistently:
- `OpcUaClientGraphChangeSender.cs` - catches `ServiceResultException`
- `OpcUaSubjectClientSource.cs` - catches `ServiceResultException`
- `OpcUaTypeResolver.cs` - catches `Exception`

**Impact:** Session disconnection during browse propagates uncaught exceptions.

**Note:** This follows the "let it throw" pattern where low-level utilities don't catch exceptions. This is acceptable if documented.

**Recommendation:** Add XML doc noting that callers must handle `ServiceResultException`.

---

## CODE DUPLICATION ISSUES

### Issue 3: BrowseNodeAsync / BrowseInverseReferencesAsync Duplication (MEDIUM)

**Location:** Lines 198-252 and 262-323

**Problem:** ~85% identical code - only `BrowseDirection` and `NodeClassMask` specification differ.

**Recommendation:** Extract shared helper:
```csharp
private static async Task<ReferenceDescriptionCollection> BrowseAsync(
    ISession session, NodeId nodeId, BrowseDirection direction,
    CancellationToken ct, uint maxReferencesPerNode = 0)
{
    // Shared implementation with pagination
}

public static Task<ReferenceDescriptionCollection> BrowseNodeAsync(...)
    => BrowseAsync(session, nodeId, BrowseDirection.Forward, ct, maxReferencesPerNode);

public static Task<ReferenceDescriptionCollection> BrowseInverseReferencesAsync(...)
    => BrowseAsync(session, nodeId, BrowseDirection.Inverse, ct);
```

**Note:** Minor difference - `BrowseNodeAsync` accepts `maxReferencesPerNode` parameter while `BrowseInverseReferencesAsync` does not.

---

## USAGE ANALYSIS

### Method Call Counts

| Method | Calls | Primary Users |
|--------|-------|---------------|
| `BrowseNodeAsync` | 13+ | OpcUaClientGraphChangeReceiver, OpcUaClientGraphChangeSender, OpcUaSubjectLoader |
| `FindChildNodeIdAsync` | 6 | OpcUaClientGraphChangeReceiver |
| `TryParseCollectionIndex` (both) | 4 | OpcUaSubjectLoader, OpcUaClientGraphChangeReceiver, OpcUaServerGraphChangeReceiver |
| `FindParentNodeIdAsync` | 2 | OpcUaClientGraphChangeReceiver |
| `ReadNodeDetailsAsync` | 1 | OpcUaClientGraphChangeReceiver |
| `ReindexFirstCollectionIndex` | TBD | New method for collection reindexing |
| `BrowseInverseReferencesAsync` | 0 | Only called internally by FindParentNodeIdAsync |

### Consolidation Status (Previously Identified)

1. **`OpcUaClientGraphChangeSender.TryFindChildNodeAsync`** - **RESOLVED**
   - Now properly delegates to `OpcUaHelper.BrowseNodeAsync`
   - Provides higher-level logic for collections/containers (legitimate abstraction)

2. **`OpcUaSubjectLoader.BrowseNodeAsync`** - **RESOLVED**
   - Duplicate removed; now uses `OpcUaHelper.BrowseNodeAsync` throughout

3. **`TryParseCollectionIndex` overloads** - **RESOLVED**
   - Second overload now delegates to first (proper consolidation)

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
| `ReindexFirstCollectionIndex` | ✅ 2 test cases | Implicit |
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
| Correctness | ⚠️ Minor Issue | Unsafe cast on line 91 |
| Architecture | ⚠️ Acceptable | Static helper class (could be extension methods) |
| Thread Safety | ✅ Good | Stateless, pure functions |
| Test Coverage | ⚠️ Partial | Good for parsing, none for async |
| Code Quality | ⚠️ Duplication | Browse methods ~85% identical |
| Error Handling | ✅ Acceptable | "Let it throw" pattern (callers handle) |

---

## Recommendations

### Must Fix (HIGH)

1. **Fix unsafe cast on line 91**
   - Add null check before casting NodeClass
   - **Effort:** ~5 minutes

### Should Fix (MEDIUM)

2. **Extract shared BrowseAsync helper** to reduce Browse/BrowseInverse duplication
   - ~85% code overlap between the two browse methods
   - **Effort:** ~20 minutes

### Consider (LOW)

3. **Convert to extension methods** on `ISession`
   - Aligns with codebase patterns (25+ extension classes)
   - **Effort:** ~1 hour (all call sites need updating)

4. **Add unit tests for async methods** with mocked ISession
   - **Effort:** ~2 hours

5. **Add XML doc noting exception handling responsibility**
   - Document that callers must handle `ServiceResultException`
   - **Effort:** ~10 minutes

---

## Related Files

- `OpcUaSubjectLoader.cs` - Uses BrowseNodeAsync (previously had duplicate, now consolidated)
- `OpcUaClientGraphChangeSender.cs` - Uses BrowseNodeAsync via TryFindChildNodeAsync
- `OpcUaClientGraphChangeReceiver.cs` - Primary consumer of helper methods
- `OpcUaServerGraphChangeReceiver.cs` - Uses TryParseCollectionIndex
- `OpcUaHelperTests.cs` - Unit tests for parsing methods

---

## Change History

| Date | Changes |
|------|---------|
| 2026-02-04 | Updated: Pagination now implemented in both browse methods. TryParseCollectionIndex overloads consolidated. OpcUaSubjectLoader duplicate removed. Reduced from 9 recommendations to 5. |
| 2026-02-02 | Initial review |
