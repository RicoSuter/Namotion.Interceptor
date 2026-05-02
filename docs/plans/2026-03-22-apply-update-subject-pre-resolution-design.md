# ApplyUpdate Subject Pre-Resolution

## Problem

`SubjectUpdateApplier.ApplyUpdate` looks up subjects by ID via `ISubjectIdRegistry.TryGetSubjectById` during processing. Subjects can be removed from the registry mid-apply, causing lookups to fail and property updates to be permanently lost.

The cause is **same-thread sequencing within the apply**: the apply processes a structural change (collection/dictionary/object-ref replacement) that detaches a subject from the registry. Later in the same apply, Step 2 tries to find that subject by ID — fails. This is a deterministic sequencing issue, not a concurrency race.

Cross-thread concurrent mutations (e.g., a local mutation engine detaching a subject on another thread) are NOT a problem here. If a subject is already gone from the registry before the apply starts, it is genuinely removed from the graph (or being moved by the concurrent mutation). In either case, not applying property updates is correct — permanent removals don't need updates, and moves propagate their own structural changes via CQP.

### Why this only affects Step 2

The Step 2 loop in `ApplyUpdate` iterates `update.Subjects` and applies property value updates to subjects found by ID. When a subject can't be found, its property updates are permanently lost — there is no self-healing path.

Structural lookups (object-ref, collection, dictionary item resolution) also call `TryGetSubjectById`, but failures there are handled by the `CompleteSubjectIds` protocol (Fix 12): unknown references are skipped, and the concurrent mutation that caused the detach propagates its own structural change via CQP, converging the system naturally.

## Solution: Pre-Resolve Subject References

Before processing any updates, resolve all subject IDs in `update.Subjects.Keys` to their `IInterceptorSubject` instances and cache the references in `SubjectUpdateApplyContext`. Step 2 uses this cache instead of the live registry.

### Why this works

Pre-resolution runs before any structural changes are applied. Subjects that will be detached by the apply's own structural processing are captured while still in the registry. This is a deterministic fix — no race window, no timing dependency.

**Applying to subsequently-detached subjects is safe**: Property setters fire through the interceptor chain, but CQP filters them out (change source matches the connector). If the subject was permanently removed by the apply's structural changes, the property updates are harmless noops on a soon-to-be-GC'd object. If the subject is still reachable via other parents, the updates land correctly.

### Why only `update.Subjects.Keys`

Structural lookups reference IDs via `propertyUpdate.Id` and `itemUpdate.Items[].Id`. These don't need pre-resolution because:

- `CompleteSubjectIds` (Fix 12) correctly distinguishes "create new" from "reference existing"
- Structural failures are self-healing: the concurrent mutation that caused the detach is itself a structural change that propagates via CQP
- Property value failures (Step 2) are NOT self-healing — the sender won't re-send values unless they change again

### Scope of changes

| Component | Change |
|-----------|--------|
| `SubjectUpdateApplyContext` | Add `Dictionary<string, IInterceptorSubject>` for pre-resolved subjects. Add `TryResolveSubject(string id, out IInterceptorSubject subject)` that checks cache first, then falls back to live registry. |
| `SubjectUpdateApplier.ApplyUpdate` | Before processing, iterate `update.Subjects.Keys` and resolve each via `idRegistry.TryGetSubjectById`, storing results in the context. |
| `SubjectUpdateApplier.ApplyUpdate` (Step 2) | Use `context.TryResolveSubject` instead of `idRegistry.TryGetSubjectById`. |
| `SubjectRegistry` | Revert deferred `_pendingIdCleanup` queue — restore eager removal of `_subjectIdToSubject` entries on `IsContextDetach`. |
| `SubjectIdTests` | Revert test that expected deferred cleanup behavior — restore expectation of immediate removal on detach. |

### What is NOT changed

- Structural lookup call sites (`ApplyObjectUpdate`, `ResolveOrCreateSubject`) continue using the live registry directly. `CompleteSubjectIds` handles failures.
- `SubjectRegistry` behavior returns to simple eager cleanup — no deferred queues.
- No new registry API surface needed — pre-resolution uses existing `TryGetSubjectById`.

## Replaces: Deferred `_subjectIdToSubject` Cleanup

The previous approach (deferred cleanup queue in `SubjectRegistry`) kept ID mappings alive past detach until the next lifecycle event. This was a global behavioral change to the registry for what is an `ApplyUpdate`-scoped problem. Pre-resolution is scoped to the apply operation with no side effects on the registry.
