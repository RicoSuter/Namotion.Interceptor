# Lifecycle Batch Scope Design

## Problem

When `SubjectUpdateApplier.ApplyUpdate` processes structural properties sequentially, a subject can be detached from one property and re-attached to another within the same update. Between detach and re-attach, the subject is removed from both `_attachedSubjects` (lifecycle) and `_knownSubjects` (registry). During this window:

1. The CQP flush thread calls `TryGetRegisteredProperty()` → null → drops value changes for the subject → permanent divergence (not self-healing without reconnection).
2. Concurrent structural mutations on the subject trigger the parent-dead check (`!_attachedSubjects.ContainsKey`) → lifecycle rolls back child tracking → registry leaks (`refCount=0, actual:FOUND`).

The previous fix (SuppressRemoval on SubjectRegistry) deferred registry cleanup but left lifecycle removal immediate. This desynchronized `_attachedSubjects` and `_knownSubjects` — the parent-dead check fired for concurrent mutations, causing a worse regression (re-sync failures, cycle 6).

## Solution: Lifecycle Batch Scope

Defer `isLastDetach` processing in `LifecycleInterceptor` itself. When a subject's last property reference is removed during a batch scope, the subject stays in `_attachedSubjects` with an empty reference set. No child cleanup, no `_lastProcessedValues` removal, no `ContextDetach`. On scope dispose, subjects whose reference set is still empty are genuinely orphaned — execute the full detach. Subjects re-attached during the batch are silently skipped.

### Why this works where SuppressRemoval failed

SuppressRemoval deferred at the wrong level (registry). The lifecycle still removed subjects from `_attachedSubjects` immediately, desynchronizing the two maps. The batch scope defers at the right level (lifecycle). Both maps stay synchronized:

| During batch | `_attachedSubjects` | `_knownSubjects` | `_subjectIdToSubject` |
|---|---|---|---|
| Subject deferred | Present (empty set) | Present (no ContextDetach) | Present (no cleanup) |
| Subject re-attached | Present (set refilled) | Present | Present |
| Parent-dead check | Passes (subject found) | N/A | N/A |
| CQP filter | N/A | Succeeds (subject found) | N/A |

### Thread model

The batch counter and deferred set are `[ThreadStatic]` — only the applier thread defers; concurrent mutation threads execute immediately. All `_attachedSubjects` access is under `lock(_attachedSubjects)` (existing pattern).

### What fires immediately (always)

- `PropertyReferenceRemoved` — per-link parent removal in registry
- `PropertyReferenceAdded` — per-link parent addition in registry
- Property reference removal from the subject's set in `_attachedSubjects` (`set.Remove(property)`)

### What is deferred (batch scope active, isLastDetach)

- `_attachedSubjects.Remove(subject)` — subject stays with empty set
- Recursive child cleanup (iterating structural properties, detaching children)
- `_lastProcessedValues` removal for the subject's properties
- `ContextDetach` event (which triggers registry cleanup)

### Resume logic (scope dispose)

```
lock (_attachedSubjects):
    for each subject in s_deferredLastDetaches:
        if _attachedSubjects[subject].Count == 0:
            // Genuinely orphaned — execute full detach (existing path)
            _attachedSubjects.Remove(subject)
            cleanup children, _lastProcessedValues
            fire ContextDetach → registry removes from _knownSubjects, _subjectIdToSubject
        // else: re-attached during batch → skip (no detach ever happened)
    clear s_deferredLastDetaches
```

HashSet deduplicates multiple deferrals of the same subject. Dispose checks live `set.Count`, not bookkeeping.

### Nested scopes

Counter supports nesting. Inner dispose decrements to N-1 (no processing). Outer dispose decrements to 0 and processes all deferred detaches.

### Performance

- Per-write during batch: zero overhead (the `isLastDetach` path is already behind a branch; adding one ThreadStatic read is negligible)
- Scope dispose: one iteration over deferred set under existing lock. Typical size: 10-100 subjects.
- No batch active: zero overhead (single `s_batchScopeCount > 0` check)
- Bonus: subjects that move between properties skip unnecessary child detach/re-attach entirely

## What this replaces

### SuppressRemoval (removed)

Remove from `SubjectRegistry.cs`:
- `[ThreadStatic] _suppressRemovalCount` and `_deferredDetaches` fields
- `SuppressRemoval()`, `ResumeRemoval()`, `RemovalSuppressionScope`
- The `if (_suppressRemovalCount > 0)` branch in `HandleLifecycleChange(IsContextDetach)`

### PreResolveSubjects (removed)

Remove from `SubjectUpdateApplyContext.cs`:
- `_preResolvedSubjects` dictionary
- `PreResolveSubjects()` method
- `TryResolveSubject()` method

PreResolveSubjects was a workaround for subjects disappearing from `_subjectIdToSubject` during the apply. With the batch scope, subjects stay in `_subjectIdToSubject` naturally (no ContextDetach fires), so `TryGetSubjectById` in the Step 2 loop succeeds directly.

### SuppressRemovalTests.cs (removed)

Tests for the removed SuppressRemoval feature.

## Why local-mutation races don't cause convergence failures

The mutation engine's structural thread can remove a subject from a collection while the value thread writes to it. The CQP correctly drops the value change (subject unregistered). But the subject is genuinely removed from the graph on ALL participants (the structural change is broadcast via the parent's property, which IS registered). Snapshots agree: subject absent from all. Convergence succeeds.

The only CQP drops that cause persistent divergence are from the applier race — where a subject is mid-move (still in the graph, just between properties). The batch scope fixes this.

## Files changed

| File | Change |
|---|---|
| `LifecycleInterceptor.cs` | Add ThreadStatic fields, `CreateBatchScope()`, modify `DetachFromProperty` isLastDetach path, add dispose logic |
| `SubjectUpdateApplier.cs` | Replace disabled SuppressRemoval with `lifecycle?.CreateBatchScope()` |
| `SubjectUpdateApplyContext.cs` | Remove PreResolveSubjects, TryResolveSubject, _preResolvedSubjects |
| `SubjectRegistry.cs` | Remove SuppressRemoval, ResumeRemoval, RemovalSuppressionScope, deferred detach branch |
| `SuppressRemovalTests.cs` | Delete |
| `BatchScopeTests.cs` | New — tests for batch scope |
| `StableIdApplyTests.cs` | Update (remove PreResolveSubjects-specific tests, keep move-during-apply test) |
| `docs/plans/fixes.md` | Fix 17 → reverted, add Fix 18 |
| `docs/design/deferred-subject-removal.md` | Rewrite for batch scope |
| `docs/plans/2026-03-25-deferred-subject-removal.md` | Delete (superseded) |

## Verification

1. All existing unit tests pass (`dotnet test --filter "Category!=Integration"`)
2. ConnectorTester WebSocket profile: target 500+ cycles with chaos, zero re-sync failures, zero registry leaks
3. Diff against master: only batch scope additions, no SuppressRemoval/PreResolveSubjects leftovers
