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

### Per-subject attachments

A subject can be attached to several lifecycle interceptors at once, one per graph. `SubjectLifecycleAttachments` keeps, in a state object in the subject's data that also holds the subject's reference count, the set of lifecycle interceptors the subject is attached to, as an immutable value (none, or an array of the interceptors, where a subject in one graph holds that interceptor's own one-element array) that every change replaces by compare-and-swap, compared by reference, so neither reads nor changes take a lock. The state object is created at the subject's first attach and never removed, so an attach or detach finds it with one lock-free lookup. An interceptor adds itself before it attaches the subject's properties (when the subject is attached as a root, or on its first reference), and removes itself before it detaches them (when the subject is detached as a root, or loses its last reference).

Derived properties use it to stay attached while their subject is in any graph. While an interceptor detaches a subject's properties, the property detaches it issues do not detach derived properties. Afterwards, also when a lifecycle handler threw, the interceptor checks whether the subject left its last graph, and if so detaches every derived property of the subject, whether or not its own context has derived change detection. A subject of a type without any derived property data (`DerivedPropertyTypeGate`) has nothing to detach and skips both steps. Each of those detaches checks again under the derived property's lock, because another interceptor may attach the subject in between. See [Detach and Attachments](tracking-derived-properties.md#detach-and-attachments).

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

- **Old value** = `_lastProcessedValues[property]` (what we last processed, stable and under our control)
- **New value** = re-read from backing store (what is actually there now, which may reflect another thread's write)

This asymmetry is the key insight: the old value comes from our private ledger, the new value comes from the shared backing store.

## Entry Lifecycle of `_lastProcessedValues`

### 1. Seeded on attach

When a subject enters the graph via `AttachSubjectToContext`, `FindSubjectsInProperties` runs with `LastProcessedValuesMode.Seed`. It reads each structural property's current backing store value and stores it:

```
_lastProcessedValues[(subject, "Collection")] = current collection reference
_lastProcessedValues[(subject, "ObjectRef")]   = current child subject
```

This establishes the initial baseline. Without seeding, the first `WriteProperty` would fall back to `null` (meaning "nothing was ever processed"), which triggers a full diff against the backing store. That is correct but slightly more work than diffing against a known baseline.

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

### 5. Removed on detach

Entries are cleaned up in three places:

| Location | When |
|----------|------|
| `DetachFromProperty` (isLastDetach) | Last reference to subject removed: all structural property entries cleaned |
| `DetachFromContext` | Root subject removed: all structural property entries cleaned |
| Parent-dead check in `WriteProperty` | Undo after attaching to a dead parent: single entry cleaned |

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
5. Thread B: **parent-dead check** finds the parent is not in `_attachedSubjects` → undo

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

Two locks serialize the lifecycle and the registry:

1. `_attachedSubjects` in `LifecycleInterceptor`
2. `_knownSubjects` in `SubjectRegistry`

Acquisition order is always: `_attachedSubjects` → `_knownSubjects`. The `SubjectRegistry` never calls back into `LifecycleInterceptor` while holding `_knownSubjects`. No deadlock is possible.

Below those two sit the registry's per-object locks: `_children` on each `RegisteredSubjectProperty` and `_lock` on each `RegisteredSubject`. Every change to children and parent entries takes them inside `_knownSubjects`, on attach and detach and when a write moved a kept subject (`IChildIndexHandler`, implemented by `SubjectRegistry`). A `_lock` may be taken while `_children` is held, never the reverse. Readers take each of these locks on its own and never nest them.

The `_attachedSubjects` lock is re-entrant (C# `Monitor`). `WriteProperty` may trigger lifecycle handlers that write to *other* properties, re-entering the lock. Each property has its own `_lastProcessedValues` entry, so there is no interference. Handlers must NOT write to the *same* property being reconciled. This is a documented contract requirement.

Under `_attachedSubjects` the lifecycle also takes a leaf-level lock: the per-property lock of a derived property, which its attach and detach take briefly. Nothing that runs under a derived property's lock takes `_attachedSubjects`, because derived getters and publications never run under it (see [Lock ordering](tracking-derived-properties.md#lock-ordering-and-deadlock-freedom)).

## Derived Properties Under the Lock

Derived property tracking hooks into the lifecycle at two points: the lifecycle attaches and detaches derived properties, and a derived getter can write a property that the lifecycle reconciles.

**Recording is suspended while the lifecycle reconciles.** A getter's write of a subject-typed property is reconciled on the evaluating thread: `WriteProperty` reconciles it, and attaching a subject runs its handlers. The reads of that reconciliation and of the handlers it calls (registry lookups, parent tracking) are not dependencies of the getter. `AttachSubjectToContext`, `DetachSubjectFromContext` and the reconciliation in `WriteProperty` therefore suspend the derived recording of the thread while they hold the lock, by pushing an inert recorder frame when a recording runs. `TryRunWhileAttached` does not, since its callback reads nothing on behalf of a getter. A derived property attached during the reconciliation pushes its own frame, so its first evaluation records its own reads normally.

**What runs under the lock.** The first evaluation of a derived property at attach runs under `_attachedSubjects`, because consumers read the value while the subject attaches. A recalculation requested under the lock also runs immediately, on the same thread and still under the lock, and publishes its change there: for example one requested by a write that a lifecycle handler makes, or by a getter's side-effect write during an attach. A handler's writes are announced through the write chain under the lock as well, so they are announced before the structural write that caused them. The dependents of a structural write made outside the lock are recalculated after its chain completed, when the lock is already released. Getters and observers that run under the lock must therefore not block or take locks, see [Handler Requirements](../tracking.md#handler-requirements).

## Handler Order Around the Descent

`ContextInheritanceHandler` is the handler that walks down into a newly attached subtree. Where a handler sits relative to it decides both the order it sees subjects in and what it can look up.

Take a subtree built first and attached in one assignment:

```csharp
var child = new Node();
var middle = new Node { Child = child };
var top = new Node { Child = middle };

root.Child = top;   // attaches top, middle and child in one go
```

Measured callback order for that one assignment, which depends on the handler's position:

| Handler position | Order it sees |
|---|---|
| Ahead of the descent (`[RunsBefore(typeof(ContextInheritanceHandler))]`) | `top`, `middle`, `child` |
| Behind the descent | `child`, `middle`, `top` |
| The subject's own `ILifecycleHandler` implementation | `child`, `middle`, `top` |

A handler ahead of the descent runs for a subject before that subject's children are visited, so it goes top down. Everything else runs only once the subtree underneath has finished attaching, so it goes deepest first.

`SubjectRegistry` sits ahead of the descent, so it has registered a subject before the descent reaches that subject's children. That holds at every level, which gives a guarantee independent of how the context was composed: **by the time any handler at or behind the registry runs for a subject, every one of its ancestors is already registered.** A handler ordered ahead of the registry is outside that guarantee, for the obvious reason that the registry has not run yet.

```csharp
public void HandleLifecycleChange(SubjectLifecycleChange change)
{
    if (!change.IsContextAttach) return;

    // Called for "child": middle, top and root all resolve here.
    foreach (var parent in change.Subject.GetParents())
    {
        var registered = parent.Property.Subject.TryGetRegisteredSubject();
    }
}
```

(`GetParents()` needs `WithParents()` on the context. Without it the same walk goes over `RegisteredSubject.Parents`, the registry's own parent edges.)

Were the registry behind the descent, `child` would see only part of that chain and `TryGetRegisteredSubject()` would return null for the rest, so a walk would compute a wrong answer rather than fail. Derived getters are exposed to this too, because `DerivedPropertyChangeHandler` evaluates them while the property attaches.

A handler that records something other handlers read during attach therefore has to say so with `[RunsBefore(typeof(ContextInheritanceHandler))]`. Registration order alone is not enough, because an unrelated handler's ordering constraint can move it. `[RunsAfter(typeof(SubjectRegistry))]` is not a substitute: it constrains the handler only against the registry, leaving its position relative to the descent dependent on where it was registered.

`SubjectRegistry` and `ParentTrackingHandler` both carry it, and the registry is additionally ordered ahead of parent tracking. Neither reads the other, so that direction only fixes what a handler placed between them sees; without it, which of the two had already run would depend on how the context was composed. The pre-descent segment therefore resolves the same way for every composition: registry, then parent tracking, then the descent. A handler asking for the slot between the two, with `[RunsAfter(typeof(ParentTrackingHandler))]` and `[RunsBefore(typeof(SubjectRegistry))]` together, now closes a cycle and is rejected when the chain resolves.

There is a second boundary inside attach that the handler ordering cannot cross. A subject's `ILifecycleHandler` chain runs to completion first, and only then do its properties attach, which is where `SubjectRegistry` invokes every `ISubjectPropertyInitializer`. So a lifecycle handler never sees properties an initializer adds to the same subject, at any ordering position. A handler that needs initializer output has to observe `IPropertyLifecycleHandler` instead, ordered with `[RunsAfter(typeof(SubjectRegistry))]` so it resolves behind the registry.

Detach is not the mirror of attach. Below the root of the detached subtree, each ancestor is deregistered further up the descent before the callback reaches a descendant, so the walk stops at the first one and nothing above it is resolvable through the registry either, including ancestors outside the subtree that are still registered. `GetParents()` is not a substitute, because it yields at most the immediate parent there, and only for the subject's own handler or one ordered ahead of `ParentTrackingHandler`. A handler that needs ancestor state on detach has to capture it at attach.

Pinned by `RegistryHandlerOrderTests`, and for the derived-getter path by `RegistryAncestorResolutionTests`. The initializer phase boundary above is measured but not pinned by a test.

## Invariants

After all concurrent `WriteProperty` / `DetachFromProperty` / `AttachSubjectToContext` / `DetachSubjectFromContext` operations complete:

1. **Reachable → Registered**: Every subject reachable from the root through intercepted properties is in `_attachedSubjects`. Properties that are not intercepted, such as non-partial `[Derived]` getters, never attach the subjects they return, so they do not make a subject reachable.
2. **Not reachable → Not registered**: Every subject NOT reachable from the root through intercepted properties is NOT in `_attachedSubjects`
3. **`_lastProcessedValues` matches attachment state**: For every attached subject, `_lastProcessedValues` entries exist for all structural properties that have been written or seeded
4. **No dangling entries**: No `_lastProcessedValues` entries exist for detached subjects

### Transient inconsistency

Between `next()` and lock acquisition, the backing store and `_attachedSubjects` can temporarily disagree (a new child is in the backing store but not yet attached, or an old child is detached but still in the backing store). This window is invisible through the lifecycle's API (which requires the lock) and resolves when `WriteProperty` completes its locked section.
