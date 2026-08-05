# Lifecycle Interceptor: Internal Design

This document describes the internal concurrency model and data structures of `LifecycleInterceptor`. For user-facing documentation, see the [Tracking](../tracking.md) documentation.

## Overview

`LifecycleInterceptor` tracks which subjects are part of the object graph. When a structural property (ObjectRef, Collection, Dictionary) is written, the interceptor diffs the old and new values to determine which child subjects were added or removed, then fires attach/detach lifecycle events. These events drive downstream systems like the `SubjectRegistry` (which maintains a flat index of all subjects) and change tracking.

The fundamental challenge is **concurrency**: multiple threads can write structural properties simultaneously, and the interceptor must maintain consistent state without losing track of subjects (memory leak) or double-attaching them.

## Data Structures

```
_attachedSubjects: Dictionary<IInterceptorSubject, HashSet<PropertyReference>>
```

Tracks which subjects are currently in the graph and via which property references they are attached. A subject can be referenced by multiple parents (e.g., the same child in two collections). The `HashSet<PropertyReference>` tracks all references; when the last reference is removed (`isLastDetach`), the subject's children are recursively detached.

```
_lastProcessedValues: Dictionary<PropertyReference, object?>
```

The lifecycle's **private ledger** of what it has actually processed for each structural property. This is the key data structure that enables correct concurrent behavior. It is decoupled from the backing store, which can be mutated at any time by concurrent `next()` calls outside the lock.

Both dictionaries are accessed exclusively under `lock (_attachedSubjects)`.

## The Concurrency Model

### The constraint

`WriteProperty` implements the `IWriteInterceptor` interface. It must call `next(ref context)` to propagate the write through the interceptor chain to the backing store. This call **cannot** be inside the lock because downstream interceptors may perform arbitrary work (I/O, notifications, other locks). The lock is acquired only after `next()` returns.

This creates a race window:

```
Thread A: next() writes to backing store ──────── [WINDOW] ──────── acquires lock
Thread B:           next() writes to backing store ── acquires lock ── releases lock
```

During this window, Thread B can complete an entire `WriteProperty` cycle, changing what's in `_attachedSubjects` and `_lastProcessedValues`. Thread A must handle this gracefully.

### Why the backing store is unreliable as a baseline

After `next()` writes value X to the backing store and before the lock is acquired, another thread can call `next()` and overwrite X with Y. When Thread A reads the backing store inside the lock, it sees Y (not X). If the lifecycle used the backing store as its "old value" baseline, it would diff the wrong pair of values, potentially missing detach operations or double-attaching subjects.

### The solution: `_lastProcessedValues`

`_lastProcessedValues` records what the lifecycle **last processed** for each structural property. It is only updated inside the lock, so it always reflects the lifecycle's actual state. `WriteProperty` uses it as the diff baseline:

- **Old value** = `_lastProcessedValues[property]`, what we last processed. Stable, under our control.
- **New value** = re-read from the backing store, what is actually there now. May reflect another thread's write.

This asymmetry is the key insight: the old value comes from our private ledger, the new value comes from the shared backing store.

## Entry Lifecycle of `_lastProcessedValues`

### 1. Seeded on attach

When a subject enters the graph via `AttachSubjectToContext`, `FindSubjectsInProperties` runs with `LastProcessedValuesMode.Seed`. It reads each structural property's current backing store value and stores it:

```
_lastProcessedValues[(subject, "Collection")] = current collection reference
_lastProcessedValues[(subject, "ObjectRef")]   = current child subject
```

This establishes the initial baseline. Without seeding, the first `WriteProperty` would fall back to `null` (meaning "nothing was ever processed"), which triggers a full diff against the backing store: correct, but slightly more work than diffing against a known baseline.

### 2. Updated on every structural write

Inside `WriteProperty`, after diffing and performing attach/detach operations:

```csharp
_lastProcessedValues[context.Property] = newValue;
```

This records: "I just processed this value. Next time, diff against this."

### 3. Read as the diff baseline

On the next `WriteProperty` for the same property:

```csharp
if (!_lastProcessedValues.TryGetValue(context.Property, out var lastProcessed))
    lastProcessed = null;  // nothing was ever processed for this property
```

The fallback to `null` handles the rare case where no entry exists (e.g., a write to a property whose entry was concurrently removed by a parent detach). Using `null` rather than `context.CurrentValue` is important: `context.CurrentValue` reflects what is *in the backing store*, not what the lifecycle *actually processed*. If the property already contains children that were never attached, `context.CurrentValue` would make them look "already processed", causing `ReferenceEquals` to skip re-discovery. `null` honestly represents "nothing was processed" and ensures the diff discovers all children in the backing store.

### 4. Read during detach (instead of backing store)

When detaching a subject, we need to find its children to recursively detach them. `DetachSubjectFromContext` and `DetachFromProperty` read from `_lastProcessedValues` instead of the backing store:

```csharp
// DetachFromProperty (isLastDetach path)
if (_lastProcessedValues.TryGetValue(subjectProperty, out var lastProcessed) && lastProcessed is not null)
{
    FindSubjectsInProperty(subjectProperty, lastProcessed, ...);
}
```

This is critical because a concurrent `next()` may have written an unattached child to the backing store. `_lastProcessedValues` tells us what was *actually attached*, which is exactly what we need to *detach*.

### 5. Removed when the property leaves the graph

The rule is an invariant rather than a list of call sites: **no entry outlives the attachment of its
property, and an entry exists once that property has been written or seeded.** It is created when the
seeding pass runs over the owning subject's own properties, which happens on `AttachSubjectToContext`,
or on the first structural write to the property; it is replaced on every later structural write; and
it is removed when the subject leaves the graph, whether it leaves through its last property reference
(`DetachFromProperty` at `isLastDetach`) or as a root (`DetachRootSubject`). The direction the two
rules below rely on is the exact one: nothing survives detachment. The converse is weaker, since a
property that has been neither seeded nor written has no entry, which is why invariant 3 at the end of
this document is phrased as "written or seeded". The parent-dead check in `WriteProperty` is the same
invariant applied to an attach that turned out never to have happened: it removes the single entry it
just wrote.

Stating it this way rather than as a table of locations is what makes the two follow-on rules
checkable. Nothing may leave an entry behind for a detached subject, and nothing may make the
removal conditional on resolving a handler through the subject's own context, because a subject that
has gone dark resolves nothing and would keep its entries, and therefore itself, for the
interceptor's lifetime. A future property-removal API has to remove the entry itself for the same
reason (see #210, which is not reachable today because `IInterceptorSubject.Properties` has no
removal counterpart).

## The Parent-Dead Check

After `WriteProperty` attaches new children and stores the `_lastProcessedValues` entry, it checks whether the parent is still in the graph:

```csharp
if (!_attachedSubjects.ContainsKey(context.Property.Subject))
{
    _lastProcessedValues.Remove(context.Property);
    // detach children we just attached
}
```

This catches the following race:

1. Thread A: `DetachFromProperty` removes parent from `_attachedSubjects`
2. Thread B: `next()` already wrote a new child to backing store (before Thread A's lock)
3. Thread A: reads `_lastProcessedValues` (the old child), detaches it, releases lock
4. Thread B: acquires lock, diffs, attaches new child, writes `_lastProcessedValues`
5. Thread B: **parent-dead check** finds the parent absent from `_attachedSubjects` → undo

Without this check, the child would be attached to a dead parent and never cleaned up, which is a memory leak.

## Concurrency Scenarios

### Two threads write the same property

1. Thread X: `next()` writes X to backing store
2. Thread Y: `next()` writes Y (overwrites X)
3. Thread X acquires lock: old = `_lastProcessedValues`, new = re-read backing store = Y
   - X effectively processes Y's write. `_lastProcessedValues = Y`
4. Thread Y acquires lock: old = Y, new = Y → `ReferenceEquals` → early return (no-op)

Thread X processes Thread Y's write; Thread Y becomes a no-op. Correct and efficient.

### Write races with parent detach

1. Thread A: detaching parent → `_attachedSubjects.Remove(parent)`
2. Thread B: `next()` wrote new child to backing store (before lock)
3. Thread A: reads `_lastProcessedValues` → detaches old children → removes entries → releases lock
4. Thread B: acquires lock → no `_lastProcessedValues` entry → falls back to `null`
   - Diffs `null` vs backing store, attaches new child, writes `_lastProcessedValues`
   - Parent-dead check fires → undo (removes entry, detaches child)

No leak.

### DetachSubjectFromContext races with child property write

1. Thread A: `DetachSubjectFromContext` → `FindSubjectsInProperties` with `LastProcessedValuesMode.Use`
   - Reads `_lastProcessedValues` (the actually-attached children), detaches them
2. Thread B: waiting for lock (already ran `next()` on a child's property)
3. Thread A: finishes, releases lock
4. Thread B: acquires lock → parent-dead check fires → undo

No leak.

## Lock Ordering

Two locks exist inside the lifecycle/registry system itself:

1. `_attachedSubjects` in `LifecycleInterceptor`
2. `_knownSubjects` in `SubjectRegistry`

Acquisition order is always: `_attachedSubjects` → `_knownSubjects`. The `SubjectRegistry` never calls back into `LifecycleInterceptor` while holding `_knownSubjects`. No deadlock is possible.

A second chain reaches into the context: `_attachedSubjects` → `InterceptorSubjectContext._mutationLock` → a `_usedByContexts` set lock. Every attach and detach enters `_mutationLock`, because the ownership claim, the reference count and the parent link all live behind it. Only the operations that actually publish or remove an edge or the parent link go on to the set lock, since that is where the reverse entry is registered or unregistered; an ordinary reference-count increment or ownership claim stops at `_mutationLock`. The set lock is a leaf: it touches only that set and calls into no other context.

Taken together the two chains are acyclic in normal operation, and there is exactly one edge that would close them: `_mutationLock` → user code → `_attachedSubjects`. Only a `TryAddService` factory or `exists` predicate can produce it, by attaching a subject from inside the mutation critical section. The contract forbids that for this reason. This is issue #404, it predates the parent link and the redesign does not make it worse, but the redesign does put `_mutationLock` on the attach path of every subject, so the edge is worth naming here rather than leaving it in the context's own comments.

The `_attachedSubjects` lock is re-entrant (C# `Monitor`). `WriteProperty` may trigger lifecycle handlers that write to *other* properties, re-entering the lock. Each property has its own `_lastProcessedValues` entry, so there is no interference. Handlers must NOT write to the *same* property being reconciled, which is a documented contract requirement.

## The Parent Link

A subject that is attached through a property does not get its parent's context as a fallback edge. It gets an internal `Parent` field on the context's immutable state, published by `ContextInheritanceHandler` through `TrySetParentContext` and cleared by `LifecycleInterceptor.DetachFromProperty` through `TryClearParentContext`. Both are internal, both run no callbacks, and consumer code can neither add nor remove the link. Service resolution visits own services, then fallback contexts in registration order, then the parent, so explicit composition beats inheritance.

**Who publishes it and when.** The handler, not `LifecycleInterceptor`, and only at reference count one:

- The handler owns inheritance, and it is registered by `WithContextInheritance()` while the interceptor is registered by `WithLifecycle()`. Publishing from the interceptor would give a subject its parent's context even where inheritance was never configured, which collapses the distinction between the two extension methods.
- Publishing from the handler also keeps what an earlier-ordered handler observes unchanged: the link does not exist until the inheritance handler runs, so handlers ahead of it still see a child that resolves nothing.
- `count == 1` is the **single write site**. Any second one needs its own cycle argument.

**Two guards on that gate**, both of which are load-bearing rather than defensive:

- *Self-context.* `a.Mother = a` reaches the handler with the parent being the subject itself. A self-link would make the context its own delegation target, so every access on that subject would throw.
- *Attach context.* When the parent's context is already the subject's recorded attach context, no link is published. This is the connector pattern: an item is attached through its parent's context, populated while registry-visible, then assigned into a property of that same parent. A link there would be a second edge to a context the attach edge already names, and the pair cannot be kept consistent: releasing the attach edge instead leaves the record describing an edge that no longer exists, and a re-attach then re-runs the seeding pass over an already-attached subtree.

**Why one write site keeps the link acyclic.** The last link written in any reference cycle targets a subject whose count went from zero to one at that instant. That subject must already be a link *source*, which required it to have driven an intercepted write while unreferenced, which requires an attach edge. So it has two outgoing edges and is not a pure delegator, and the pure delegation cycle exception is unreachable through inheritance. The argument does not depend on the cycle's length; a mutual pair is one shape of it, not the requirement.

That argument holds only while three guards hold together:

1. the attach edge survives for as long as the subject is referenced,
2. `RemoveFallbackContext` rejects the attach edge,
3. `DetachFromContext` rejects a non-zero reference count.

Relax any one and a root can become a pure delegator while holding a link, at which point the single write site becomes cycle-capable. One relaxation already exists on the error path and is recorded at the write site: the attach rollback removes the edge with no reference-count check, so a subject that is already a property child, is root-attached within the same graph and whose attach then throws is left parent-only.

**Release ordering.** The link is cleared in `DetachFromProperty`'s `finally`, at reference count zero, and never before the handlers run. The descent resolves the next level's handlers through the child's own context, and a property-attached subject has no other edge, so clearing first would give grandchildren bookkeeping without handler invocation and would also lose the subject's own per-property deregistration. It also closes the window in which the subject is unowned while its graph is still mid-detach, during which another graph could claim it.

## Reference Count and Graph Ownership

The count moved off `subject.Data` and onto `InterceptorExecutor`, alongside two records: which lifecycle graph owns the subject, and which context it was root-attached through. All three are written under the context's `_mutationLock`.

The move is not only an allocation saving. On `master` the count was **global**, living on the subject, while `IsContextAttach` and `IsContextDetach` were **per-graph**, deriving from one interceptor's `_attachedSubjects`. Two graphs holding the same subject therefore disagreed about it by construction, and that mismatch is what produced #207. There, the edge released at reference count zero was the one named by whichever property-removal event happened to fire, so a subject whose edge came from somewhere else, a constructor context or a different parent, kept its edge and its reverse registration forever. The fix is to release whatever edge the subject actually holds when it leaves the graph, which is only expressible once the count and the graph refer to the same thing. One graph per subject collapses the distinction, because the two now describe the same graph and can never disagree.

Ownership is claimed against the **attaching context**, not by interceptor reference identity: an aggregated context resolves more than one `ILifecycleInterceptor` and every one of them attaches, so identity would reject the second one. A claim is refused only when the standing owner does not resolve from the context the new claim arrives through, which a genuinely disjoint graph never can. Two consequences follow, both confined to aggregated configurations that already share an interceptor: the predicate is asymmetric, since a context that resolves the standing owner may claim while one that does not may not, and the re-attach-during-detach rejection is enforced only by the owning interceptor, because that guard is gated on ownership.

Reads of the count use `Volatile.Read`, because the `ConcurrentDictionary` it replaced supplied that ordering for free and a plain field does not. The public `GetReferenceCount()` stays a snapshot.

## Handler Order Depends on Resolved Position

A handler's observed position is its **resolved** service position, not its registration position, and the two differ. `ServiceOrderResolver.OrderByDependencies` is Kahn's algorithm with a lowest-index tie-break behind a `[RunsFirst]`/`[RunsLast]` partitioning fast path. `ParentTrackingHandler` carries `[RunsBefore(typeof(ContextInheritanceHandler))]`, so it is always ahead of the inheritance handler. `SubjectRegistry` carries no ordering attribute at all, so where it lands is purely its registration index: ahead when `WithRegistry()` is called first, behind when it is called last.

For a three-level graph the difference is visible in the attach order a handler sees:

```
handler resolved BEFORE inheritance:  M2, M3, M1     (top-down)
handler resolved AFTER  inheritance:  M3, M2, M1     (bottom-up)
```

No issue records this. The parent-link design preserves it deliberately, which is the reason the link is published by the handler rather than by `LifecycleInterceptor`, and characterization tests cover both registration orders so that a future change to the resolver has to move a snapshot rather than move this quietly.

**One detach order did move, and in the direction of consistency.** The detach descent used to run only when the inherited edge was present, so a subject whose edge was absent, such as one attached in its own constructor and then placed under a parent, fell through to the explicit child recursion that runs after the whole handler chain. That produced a top-down cascade for exactly that shape while every other shape cascaded bottom-up. The descent now runs unconditionally at reference count zero, so every shape whose parent context carries `ContextInheritanceHandler` cascades bottom-up.

The explicit child recursion at the end of `DetachFromProperty` still runs. Where the descent already detached a child, that child no longer has a ledger entry and the recursion returns immediately, so it costs a lookup and changes nothing. Where inheritance is not registered it is the only cascade there is, and it produces the top-down order, because a subject's own handlers run before its children are recursed into.

## Invariants

After all concurrent `WriteProperty` / `DetachFromProperty` / `AttachSubjectToContext` / `DetachSubjectFromContext` operations complete:

1. **Reachable → Registered**: Every subject reachable from the root via the object graph is in `_attachedSubjects`
2. **Not reachable → Not registered**: Every subject NOT reachable from the root is NOT in `_attachedSubjects`
3. **`_lastProcessedValues` matches attachment state**: For every attached subject, `_lastProcessedValues` entries exist for all structural properties that have been written or seeded
4. **No dangling entries**: No `_lastProcessedValues` entries exist for detached subjects

### Transient inconsistency

Between `next()` and lock acquisition, the backing store and `_attachedSubjects` can temporarily disagree (a new child is in the backing store but not yet attached, or an old child is detached but still in the backing store). This window is invisible through the lifecycle's API (which requires the lock) and resolves when `WriteProperty` completes its locked section.
