# LifecycleInterceptor: `_lastProcessedValues` Analysis

## What it is

`_lastProcessedValues` is a `Dictionary<PropertyReference, object?>` in `LifecycleInterceptor` that tracks the last known value of each structural property (Collection, Dictionary, ObjectRef) after the lifecycle processed it. It serves as the **diff baseline** for `WriteProperty`: when a property is written, the lifecycle compares `_lastProcessedValues[property]` (old) vs the re-read backing store value (new) to determine which child subjects to attach/detach.

## Why it exists

The lifecycle's `WriteProperty` uses a **re-read-under-lock** pattern:

1. `next()` writes to the backing store (no lock held)
2. Lock acquired on `_attachedSubjects`
3. Backing store re-read to get the current value (handles concurrent overwrites)
4. Diff old vs new → attach/detach children
5. `_lastProcessedValues[property] = newValue`

Without `_lastProcessedValues`, two threads writing to the same property would both diff against `context.CurrentValue` (the pre-write snapshot captured before `next()`). This causes double-attaches or missed detaches because `context.CurrentValue` doesn't account for intermediate writes processed by other threads.

## All mutation points

| Location | Operation | Context |
|----------|-----------|---------|
| `WriteProperty` L430 | Read (fallback) | Baseline for diff. Falls back when entry doesn't exist |
| `WriteProperty` L482 | Write | Updated to re-read value after processing |
| `WriteProperty` L488 | Remove | Parent-dead check cleanup |
| `AttachSubjectToContext` L159 | Write (Seed) | Root subject initialization — reads backing store |
| `DetachFromProperty` L377-385 | Read + Remove | Cascade: finds children to recursively detach |
| `DetachFromContext` L321-323 | Remove | Root subject teardown |
| `EndBatchScope` L93-99 | Read + Remove | Deferred detach cascade |
| `FindSubjectsInProperties` L577-579 | Write (Seed mode) | Called by `AttachSubjectToContext` |

## Bug found: wrong fallback baseline

### The problem

When `_lastProcessedValues` has no entry for a property, `WriteProperty` fell back to `context.CurrentValue`:

```csharp
if (!_lastProcessedValues.TryGetValue(context.Property, out var lastProcessed))
    lastProcessed = context.CurrentValue; // ← BUG
```

`context.CurrentValue` is the backing store value **before** `next()` was called. For subjects whose structural properties were populated **before** the subject had a context (e.g., the applier creates new subjects, applies properties, then adds to the graph via `SetValue`), `context.CurrentValue` already includes the pre-context children. The lifecycle diffs these children against the re-read value and sees them as "already processed" — they are never attached.

### The scenario (applier for new subjects)

1. Applier creates subject X (no context)
2. Applier writes `X.Items = {"child": Z}` → direct to backing store (no interceptor chain)
3. Applier writes `parent.Collection = [..., X]` → interceptor chain → lifecycle attaches X
4. X gets context via `ContextInheritanceHandler`
5. Later, someone writes to `X.Items` (server broadcast, mutation engine, etc.)
6. `WriteProperty` for `X.Items`: no `_lastProcessedValues` entry → fallback to `context.CurrentValue`
7. `context.CurrentValue` = `{"child": Z}` (set in step 2, before context)
8. Re-read = `{"child": Z}` (same reference or equivalent)
9. Diff: same → **Z is never attached**. Z stays in backing store but not in `_attachedSubjects` or registry

### Impact

- Subject Z is reachable from the root (in the backing store) but not registered
- The applier can't find Z by ID → value updates for Z are dropped
- `CreateCompleteUpdate` (snapshots) may or may not include Z depending on timing
- Re-sync via `ApplySubjectUpdate` can't fix it (Z's ID not in registry → creates new instance but the old orphan stays)
- Observed in the connector tester as value divergence between server and clients

### The fix

```csharp
if (!_lastProcessedValues.TryGetValue(context.Property, out var lastProcessed))
    lastProcessed = null; // Force discovery of all children
```

Using `null` as baseline ensures that the first `WriteProperty` after attachment diffs `null` vs backing store → discovers and attaches all pre-context children.

### Fix limitations

The fix is **lazy**: children are only discovered when someone writes to the structural property after attachment. In practice this always happens (the applier processes subsequent updates, the server broadcasts, mutation engines write). But theoretically, if a subject enters the graph with pre-populated children and nobody ever writes to that structural property again, the children stay unattached.

### Why eager discovery doesn't work

The alternative fix — eagerly discovering and attaching children in `AttachToProperty` on first-attach (same pattern as `AttachSubjectToContext` for root subjects) — was attempted but fails under concurrent writers.

**The race**: when `AttachToProperty` eagerly attaches grandchild G and seeds `_lastProcessedValues[child.Mother] = G`, a concurrent thread can overwrite `_lastProcessedValues[child.Mother]` to `null` (via its own `WriteProperty` processing) before the detach cascade runs. The cascade then finds `null` → nothing to detach → G stays orphaned with refCount=1.

Specifically:
1. Thread 1 (lock): attach child → eager fix: read `child.Mother` → G → attach G, seed `_lastProcessedValues = G`
2. Thread 2 (pending): had written `child.Mother = null` via `next()`
3. Thread 2 (lock): `WriteProperty` → `lastProcessed = G`, re-read = `null` → detach G ✓, `_lastProcessedValues = null`
4. Thread 1 (lock): detach child → cascade → `_lastProcessedValues[child.Mother] = null` → nothing to detach
5. Thread 1 (lock): re-attach child → eager fix attaches G2 → seeds `_lastProcessedValues = G2`
6. Repeat step 3-4 → G2 orphaned

The eager approach creates an attach that the normal `WriteProperty` flow doesn't know about. The concurrent `WriteProperty` correctly transitions `_lastProcessedValues` through G → null, but the detach cascade always arrives too late (after `_lastProcessedValues` was already overwritten to null).

This is not fixable within `AttachToProperty` without fundamentally changing the concurrency model (e.g., holding the lock during `next()`, which would serialize all structural writes).

## Other `_lastProcessedValues` concerns reviewed

### DetachFromContext doesn't cascade children

`DetachFromContext` (line 312) removes `_lastProcessedValues` entries but does NOT recursively detach children via `FindSubjectsInProperty`. However, `DetachSubjectFromContext` (line 178) calls `FindSubjectsInProperties` and detaches children explicitly before calling `DetachFromContext`. So the cascade happens at the caller level. **Not a bug.**

### EndBatchScope uses root context for service resolution

`EndBatchScope` (line 126) uses `s_batchScopeRootContext` for service resolution during deferred detach. If the root context's services change during the batch scope, handlers might not fire correctly. **Low risk** — services don't change at runtime.

### Backing store reads outside lock

`FindSubjectsInProperties` with `Seed` mode reads the backing store inside the lock but concurrent `next()` calls (outside the lock) can modify the backing store between reads. The read is a reference assignment (atomic in .NET), so no torn reads. The value might be stale, but the next `WriteProperty` will correct it. **Not a bug.**

### ReferenceEquals early-exit

`WriteProperty` line 441: `if (ReferenceEquals(lastProcessed, newValue)) return;` — if the re-read returns the same reference as the baseline, no processing happens. This is correct for value-type properties but could miss changes within mutable collections. However, the library uses immutable array/dict replacements (not in-place mutation), so reference equality correctly detects "no change". **Not a bug.**

## Seeding paths comparison

| Path | Seeded with | Children attached | Used for |
|------|-------------|-------------------|----------|
| `AttachSubjectToContext` | Backing store values | Yes (explicit loop) | Root subjects |
| `AttachToProperty` (before fix) | Not seeded | No | Child subjects |
| `AttachToProperty` (after fix) | Not seeded (null fallback in WriteProperty) | No (lazy, on next write) | Child subjects |

The asymmetry between root and child paths is the source of the bug. Root subjects get full seeding + child discovery. Child subjects rely on subsequent `WriteProperty` calls to discover their children.

## Test coverage

- `ConcurrentStructuralWriteLeakTests` (10 tests): verify no orphaned subjects after concurrent structural writes. All pass with the null-fallback fix.
- Connector tester (WebSocket): 43+ cycles with chaos injection (disconnects, kills) without structural bugs. Previously failed at cycles 9-13.
