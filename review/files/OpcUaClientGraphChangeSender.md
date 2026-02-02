# OpcUaClientGraphChangeSender.cs - Code Review

**File:** `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientGraphChangeSender.cs`
**Status:** Complete
**Reviewer:** Claude (Multi-Agent)
**Date:** 2026-02-02
**Lines:** 587

---

## Overview

Processes structural property changes (add/remove subjects) for OPC UA client. Creates or removes MonitoredItems when the C# model changes. Calls AddNodes/DeleteNodes on the server when `EnableGraphChangePublishing` is enabled.

---

## Data Flow

```
C# Model Change → OpcUaSubjectClientSource.WriteChangesAsync()
                          │
                          ▼
              CurrentSession = session (line 774)
                          │
                          ▼
              GraphChangePublisher.ProcessPropertyChangeAsync()
                          │
                          ├─► OnSubjectAddedAsync()
                          │     ├─ Check if already tracked (shared subject)
                          │     ├─ Find parent NodeId
                          │     ├─ Browse for existing child node
                          │     ├─ TryCreateRemoteNodeAsync() if not found
                          │     ├─ LoadSubjectAsync() → MonitoredItems
                          │     └─ WriteInitialPropertyValuesAsync()
                          │
                          └─► OnSubjectRemovedAsync()
                                └─ (No-op - cleanup via OwnershipManager callback)
```

**Key Entry Points:**
- `OnSubjectAddedAsync()` - Called when subjects are added to collections/dictionaries/references
- `OnSubjectRemovedAsync()` - Empty; cleanup handled by `OpcUaSubjectClientSource.OnSubjectDetaching()`

---

## Thread Safety Analysis

**Verdict: MEDIUM RISK**

### Issues Found

| Issue | Location | Severity |
|-------|----------|----------|
| `_diffBuilder` in base class not thread-safe | `GraphChangePublisher.cs:16` | HIGH |
| Non-atomic check-then-act for shared subjects | Lines 61-68 | MEDIUM |
| `LoadSubjectAsync` modifies shared state without sync | Line 121 | MEDIUM |
| Session could disconnect mid-operation | Lines 51-136 | LOW |

### Critical Issue: `CollectionDiffBuilder` in Base Class

The base class `GraphChangePublisher` uses a mutable `_diffBuilder` instance field that is NOT thread-safe. If `ProcessPropertyChangeAsync` is called concurrently, internal state corruption can occur.

**Mitigation:** Current usage serializes calls via `ChangeQueueProcessor` flush gate when buffering is enabled. With buffering disabled, concurrent calls are possible.

### Shared Subject Detection Race

```csharp
// Lines 61-68 - Check-then-act is not atomic
if (_source.IsSubjectTracked(subject))          // Thread A reads false
{                                                // Thread B also reads false
    if (_source.TryGetSubjectNodeId(...))        // Both proceed...
    {
        _source.TrackSubject(...);               // Duplicate tracking possible
    }
}
```

---

## Architecture & Design Review

### SOLID Violations

| Principle | Status | Issue |
|-----------|--------|-------|
| **SRP** | VIOLATED | Class has 5+ responsibilities: browsing, node creation, type resolution, loading, value writing |
| **OCP** | VIOLATED | `ResolveWellKnownTypeDefinition` hardcodes type mappings |
| **DIP** | PARTIAL | Temporal coupling via `CurrentSession` property |

### Class Size

587 lines - strong indicator of SRP violation. Should be split into:

```
OpcUaClientGraphChangeSender (coordinator, ~100 lines)
├── OpcUaNodeBrowser (discovery, ~120 lines)
├── OpcUaNodeCreator (AddNodes, ~180 lines)
├── OpcUaTypeDefinitionResolver (mapping, ~30 lines)
└── OpcUaInitialValueWriter (property writing, ~90 lines)
```

### Empty `OnSubjectRemovedAsync` (Lines 141-145)

```csharp
protected override Task OnSubjectRemovedAsync(...) => Task.CompletedTask;
```

This creates asymmetry where:
- **ADD** flows through `GraphChangePublisher`
- **REMOVE** flows through `OwnershipManager` callback

The server implementation (`OpcUaServerGraphChangeSender`) has actual removal logic, making this inconsistency confusing.

---

## Code Quality

### Modern C# Practices

| Practice | Status |
|----------|--------|
| File-scoped namespace | OK |
| Nullable reference types | OK |
| Pattern matching (`is null`) | OK |
| `ConfigureAwait(false)` | OK |
| `CancellationToken` propagation | **MISSING** |

### Code Duplication

| Duplicated Pattern | Lines | Duplicate In |
|-------------------|-------|--------------|
| `TryFindContainerNodeAsync` | 456-472 | `OpcUaHelper.FindChildNodeIdAsync` (19-34) |
| Browse-and-find-by-name | 6+ occurrences | `OpcUaClientGraphChangeReceiver`, `OpcUaHelper` |
| `TryFindRootNodeIdAsync` | 147-164 | `OpcUaSubjectClientSource.TryGetRootNodeAsync` |

**Recommendation:** Replace `TryFindContainerNodeAsync` with `OpcUaHelper.FindChildNodeIdAsync`.

### Dead Code

| Item | Location | Issue |
|------|----------|-------|
| `wasCreatedRemotely` variable | Lines 103, 111 | Set but never read |
| `OnSubjectRemovedAsync` | Lines 141-145 | Empty method satisfying interface |

### Missing Logging

- `TryFindChildNodeAsync` (106 lines) has no debug logging
- Silent returns at lines 97-100, 114-117 without logging

### Magic Strings

Lines 479-491: Well-known type definition strings should be constants:
```csharp
"BaseObjectType", "FolderType", "BaseDataVariableType", ...
```

---

## Test Coverage

**Rating: MEDIUM**

### Covered via Integration Tests

| Scenario | Test File |
|----------|-----------|
| Add to collection (Container/Flat) | `ClientToServerCollectionTests.cs` |
| Add to dictionary | `ClientToServerDictionaryTests.cs` |
| Assign/clear/replace reference | `ClientToServerReferenceTests.cs` |
| Remote node creation via AddNodes | Integration tests |
| WriteInitialPropertyValuesAsync | Integration tests |

### NOT Covered

| Scenario | Method | Lines |
|----------|--------|-------|
| Null session handling | `OnSubjectAddedAsync` | 52-58 |
| Parent node not found | `OnSubjectAddedAsync` | 87-93 |
| TypeDefinition from attribute | `TryCreateRemoteNodeAsync` | 293-311 |
| TypeDefinition from TypeRegistry | `TryCreateRemoteNodeAsync` | 313-325 |
| AddNodes failure (bad status) | `TryCreateRemoteNodeAsync` | 437-440 |
| AddNodes ServiceResultException | `TryCreateRemoteNodeAsync` | 443-448 |
| Container not found | `TryFindContainerNodeAsync` | 470-471 |
| Write partial failure | `WriteInitialPropertyValuesAsync` | 570-574 |
| Well-known type resolution | `ResolveWellKnownTypeDefinition` | 477-493 |

**Recommendation:** Create `OpcUaClientGraphChangeSenderTests.cs` with mocked `ISession`.

---

## Summary

| Category | Rating | Notes |
|----------|--------|-------|
| Thread Safety | **MEDIUM RISK** | `_diffBuilder` not thread-safe, check-then-act races |
| Architecture | **NEEDS REFACTOR** | 587 lines, SRP violation, temporal coupling |
| Code Quality | **FAIR** | Duplication with OpcUaHelper, dead code, missing CancellationToken |
| Test Coverage | **MEDIUM** | Integration tests cover happy path, no unit tests |
| SOLID | **PARTIAL** | SRP, OCP violations |

**Overall: Functional but needs refactoring for maintainability**

---

## Actionable Items

### Must Fix (Before Merge)

1. **Remove dead code:** Delete unused `wasCreatedRemotely` variable (lines 103, 111)
2. **Add logging:** Log when `propertyName` is null (lines 97-100) and node creation skipped (lines 114-117)

### Should Fix (High Priority)

1. **Replace `TryFindContainerNodeAsync`** with `OpcUaHelper.FindChildNodeIdAsync`
2. **Document thread safety assumption:** Add comment that `ProcessPropertyChangeAsync` must be called serially
3. **Add `CancellationToken` parameter** to `OnSubjectAddedAsync` and propagate to async calls

### Nice to Have (Future)

1. Split class into focused components (Browser, NodeCreator, ValueWriter)
2. Extract well-known type definitions to constants
3. Create dedicated unit test file with mocked `ISession`
4. Consider making `OnSubjectRemovedAsync` actually perform cleanup (currently delegated)
5. Add atomic tracking for shared subjects using `ConcurrentDictionary.GetOrAdd` pattern

---

## Appendix: Data Flow Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                     OnSubjectAddedAsync()                       │
└───────────────────────────────┬─────────────────────────────────┘
                                │
                                ▼
                    ┌───────────────────────┐
                    │  Session null?        │──YES──► Log, return
                    └───────────┬───────────┘
                                │ NO
                                ▼
                    ┌───────────────────────┐
                    │  Already tracked?     │──YES──► Increment ref, return
                    └───────────┬───────────┘
                                │ NO
                                ▼
                    ┌───────────────────────┐
                    │  Find parent NodeId   │
                    └───────────┬───────────┘
                                │
                                ▼
                    ┌───────────────────────┐
                    │  Parent found?        │──NO──► Log, return
                    └───────────┬───────────┘
                                │ YES
                                ▼
                    ┌───────────────────────┐
                    │  TryFindChildNodeAsync│
                    └───────────┬───────────┘
                                │
                    ┌───────────┴───────────┐
                    │                       │
                Found                    Not Found
                    │                       │
                    │           ┌───────────┴───────────┐
                    │           │ EnablePublishing?     │
                    │           └───────────┬───────────┘
                    │                       │ YES
                    │           ┌───────────┴───────────┐
                    │           │ TryCreateRemoteNode   │
                    │           │ (AddNodes)            │
                    │           └───────────┬───────────┘
                    │                       │
                    └───────────┬───────────┘
                                │
                                ▼
                    ┌───────────────────────┐
                    │  LoadSubjectAsync     │
                    │  (recursive loading)  │
                    └───────────┬───────────┘
                                │
                                ▼
                    ┌───────────────────────┐
                    │  AddMonitoredItems    │
                    └───────────┬───────────┘
                                │
                                ▼
                    ┌───────────────────────┐
                    │  EnablePublishing?    │──YES──► WriteInitialPropertyValues
                    └───────────────────────┘
```
