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

Relax any one and a root can become a pure delegator while holding a link, at which point the single write site becomes cycle-capable. A fourth relaxation already exists on the error path and is recorded at the write site: `ClearAttachContext`, the rollback `AttachToContext` runs when an attach interceptor throws, removes the edge with no reference-count check, so a subject that is already a property child, is root-attached within the same graph and whose attach then throws is left parent-only.

**Release ordering.** The link is cleared in `DetachFromProperty`'s `finally`, at reference count zero, and never before the handlers run. The descent resolves the next level's handlers through the child's own context, and a property-attached subject has no other edge, so clearing first would give grandchildren bookkeeping without handler invocation and would also lose the subject's own per-property deregistration. It also closes the window in which the subject is unowned while its graph is still mid-detach, during which another graph could claim it.

## Reference Count and Graph Ownership

The count moved off `subject.Data` and onto `InterceptorExecutor`, alongside two records: which lifecycle graph owns the subject, and which context it was root-attached through. All three are written under the context's `_mutationLock`.

The move is not only an allocation saving. On `master` the count was **global**, living on the subject, while `IsContextAttach` and `IsContextDetach` were **per-graph**, deriving from one interceptor's `_attachedSubjects`. Two graphs holding the same subject therefore disagreed about it by construction, and that mismatch is what produced #207. There, the edge released at reference count zero was the one named by whichever property-removal event happened to fire, so a subject whose edge came from somewhere else, a constructor context or a different parent, kept its edge and its reverse registration forever. The fix is to release whatever edge the subject actually holds when it leaves the graph, which is only expressible once the count and the graph refer to the same thing. One graph per subject collapses the distinction, because the two now describe the same graph and can never disagree.

Ownership is claimed against the **attaching context**, not by interceptor reference identity: an aggregated context resolves more than one `ILifecycleInterceptor` and every one of them attaches, so identity would reject the second one. A claim is refused only when the standing owner does not resolve from the context the new claim arrives through, which a genuinely disjoint graph never can. Two consequences follow, both confined to aggregated configurations that already share an interceptor: the predicate is asymmetric, since a context that resolves the standing owner may claim while one that does not may not, and the re-attach-during-detach rejection is enforced only by the owning interceptor, because that guard is gated on ownership.

Reads of the count use `Volatile.Read`, because the `ConcurrentDictionary` it replaced supplied that ordering for free and a plain field does not. The public `GetReferenceCount()` stays a snapshot.

**What two graphs holding one subject actually looks like, which no issue records.** Measured on `master`, where the shape was reachable by an ordinary parent-to-parent attach. It survives here only in the shared-tracking-context configuration under "Known Gaps", because the cross-graph rejection now refuses every other route to it. Both registries index the subject, and parent tracking records both parents, because those write to the subject's own data rather than resolving through its context. Everything that resolves through the subject's own context reaches one graph only, the one its parent link or its attach edge names, so `TryGetRegisteredSubject()` answers from that graph and the other holds a subject it can enumerate and never hears from: a write to one of the subject's properties runs the first graph's interceptors, and an observer registered on the second graph's own root context sees nothing. The half that works is exactly the half written onto the subject; the half that does not is exactly the half resolved through it.

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

## Known Gaps

Behaviours this design leaves broken on purpose. Most of them are pinned by a test in
`src/Namotion.Interceptor.Tracking.Tests/Lifecycle/KnownGapTests.cs`, one test per entry, and those
tests **assert the undesirable outcome**. A reader who finds one failing must not weaken the
assertion to make it pass. A failure means one of two things: either someone improved the behaviour,
in which case update the test and the matching entry here deliberately, or someone regressed it and
the assertion is doing its job. The two entries at the end have no test, and say why.

### Root attach racing root detach on the same subject

`AttachToContext` and `DetachFromContext` are each two steps, a record transition under
`_mutationLock` followed by an interceptor pass outside it, and they are not atomic against each
other. A detach can clear the record, an attach can then record and publish its edge, and the
detach's second step removes that edge, leaving a record with no edge: the subject reports attached
and resolves nothing. All four record and edge combinations are reachable.

One shape of it is inside the library rather than in consumer sequencing. `ClearAttachContext`, the
rollback `AttachToContext` runs when the attach throws, clears the record under `_mutationLock`,
releases the lock, and removes the edge in a second acquisition. A retry that records the same
context in between and deduplicates against the still-present edge then loses that edge to the
rollback's removal.

Not fixed because making the two steps one requires the edge removal inside `_mutationLock`, which
drags `InvalidateUsingContexts` in with it, and #400 deliberately keeps that outside. Closing it
means serialising the two root operations per subject, which is a separate change.

Pinned by `WhenAttachRacesDetachOnTheSameRoot_ThenNoInvariantOtherThanEdgeAgreementHolds`. That test
deliberately does **not** assert that the record and the edge agree, because that agreement is
exactly what the defect breaks. What it pins is the weaker property that must survive: whatever the
race leaves behind, a full attach and detach round still completes, so no round can wedge the
subject in a state no consumer call can leave.

### Adding the attach context back during the detach window

`DetachFromContext` clears the record before the interceptor loop, so an `AddFallbackContext` naming
that same context and arriving inside the window is no longer naming the recorded attach context.
`OnAddingFallbackContext` sees a lifecycle-bearing context that is not the record and throws. The
silent form of this, in which the add succeeded and left a subject resolving a graph it was leaving,
is gone. What remains is that the caller cannot complete the add at all.

Not fixed because clearing the record before the interceptor pass is what makes the detach run
exactly once and makes the edge removal unconditional in the `finally`. The window closes with the
same serialisation as the entry above.

Pinned by `WhenAddingTheAttachContextDuringTheDetachWindow_ThenItThrows`, which rendezvouses with a
custom `ILifecycleInterceptor` so that the add provably lands inside the window rather than after it.

### A multi-parent subject whose linked parent leaves the graph

The parent link is written once, at reference count one, and names whichever parent referenced the
subject first. When that parent leaves the graph while a second parent still holds the subject, the
subject's count goes to one rather than zero, so `TryClearParentContext` does not run and nothing
repoints the link. The link keeps naming a context that has itself left the graph and now resolves
nothing, so the subject stays attached and referenced while resolving no interceptors at all. Its
`GetServices<IWriteInterceptor>()` is empty. That is more severe than #410 predicts.

Not fixed because the only candidate fix is repointing the link on partial detach, which was
proposed and refuted three times. See "Rejected Alternatives".

Pinned by `WhenLinkedParentLeavesWhileAnotherHoldsTheSubject_ThenTheSubjectGoesDark`.

### A connector item whose only edge is its attach edge

The connector sites attach an item through its parent's context and then assign it into a property
of that same parent. The link gate deliberately skips a context the attach edge already names, so
the item never gets a link at all and its only edge is that attach edge. A second holder then keeps
the item's reference count above zero when the attach parent leaves, so the attach edge is never
released either, and it points into a context that no longer resolves anything. Same dark state as
the entry above, reached without a link ever existing.

Not fixed for the same reason, and this shape is what refutes the repoint most directly: there is no
link here to repoint, so the repoint's trigger never fires on the shape that has real users.

Pinned by `WhenConnectorItemsAttachParentLeaves_ThenTheItemGoesDark`. The order of the two
references matters and the test says so: the attach parent must take the first reference, because
referencing the item from the holder first would set a link to the holder and the item would survive.

### A cross-graph rejection part way through a batch write

`WriteProperty` calls `next()` before taking the lock, so by the time `AttachToProperty` rejects an
item already owned by another graph, the backing store holds the new collection and the earlier
items of the same batch are attached. The write is committed and the batch is half applied. The
rejected item itself is clean: `ClaimOwnership` runs ahead of every mutation in `AttachToProperty`,
so the item keeps exactly the references its own graph gave it rather than being counted in two.

Not fixed. This is #384's shape, and the exception is new here: change to the ownership claim made
`AttachToProperty` throw where it previously could not. An earlier version tried to prevent the
partial batch with a batch-level pre-check, which review refuted because hoisting the check
separates it from the claim and reintroduces the time-of-check race the claim exists to close.

Pinned by `WhenCrossGraphRejectionHappensMidBatch_ThenEarlierItemsStayAttached`.

### An attach handler that throws part way through a root attach

`AttachToContext`'s `catch` calls `ClearAttachContext`, which rolls back this context's own record
and edge. It cannot roll back anything the lifecycle system already did: `AttachSubjectToContext`
seeds `_lastProcessedValues` and attaches children before the root's own attach callback runs, so a
throw there leaves the children attached and counted while the root reports unattached.

Not fixed. The fix is rollback inside `AttachToProperty`, which is #384 and out of scope here.

Pinned by `WhenAttachHandlerThrowsPartWay_ThenTheLifecycleResidueRemains`.

### A fallback cycle letting a subject inherit its own descendant's subtree service

`a.Mother = b; b.Father = a;` makes each subject's context reachable from the other, so a service
registered on `b`'s context resolves from `a`. That contradicts what `ContextSubtreeServiceTests`
documents about subtree scoping.

Not fixed, and not introduced here: it predates this design and is reachable through ordinary
consumer fallback composition. Only *pure delegation* cycles raise, and the cycle argument in "The
Parent Link" is about those. Ordinary fallback cycles remain reachable and silent, and the service
walk's visited set keeps them from looping.

Pinned by `WhenFallbackCycleExists_ThenASubjectInheritsItsOwnDescendantsSubtreeService`.

### Two root contexts sharing one tracking context

Ownership is an `ILifecycleInterceptor` reference. Two root contexts that each add the same tracking
context as a fallback resolve the same `LifecycleInterceptor`, so they count as one graph while
having two registries. The cross-graph rejection therefore does not fire, and a subject referenced
from both ends up with reference count two and an entry in both registries, which is the half-working
two-graph state described under "Reference Count and Graph Ownership".

Not fixed. Distinguishing the two needs graph identity as a first-class parameter, which is the cost
listed under "Rejected Alternatives".

Pinned by `WhenTwoRootContextsShareOneTrackingContext_ThenTheCrossGraphRejectionDoesNotApply`. The
wiring order in that test is load-bearing and its comment says so: the fallback must be added before
`WithRegistry()`, because `WithService` skips only when the service type already resolves through the
chain, so registering the registry first would give each root its own `LifecycleInterceptor` and two
genuinely separate graphs, and the rejection would then fire correctly instead of demonstrating the
gap.

### The detach cascade order of a constructor-attached subtree

Not a defect, and the one entry here that pins a desirable outcome rather than an undesirable one. A
constructor-attached subject that owns a child and is then placed under a parent is the only shape
whose detach cascade order moved with this design, from top-down to the bottom-up order every other
shape already produced (see "Handler Order Depends on Resolved Position"). Only one snapshot covers
it incidentally, so it is pinned deliberately here.

Pinned by `WhenConstructorAttachedSubtreeIsRemovedFromItsParent_ThenTheCascadeIsBottomUp`.

### The residual race after both root guards, which has no test

Each root guard is atomic in itself. `TryClearAttachContext` and `TryRecordAttachContext` both read
the reference count and transition the record inside the same `_mutationLock` acquisition, and
`IncrementReferenceCount` takes that same lock, so a property attach cannot land between the check
and the transition and a rejection leaves the subject exactly as it was.

What neither covers is a property operation landing **after** the transition, on either side,
because the interceptor pass runs outside the lock:

- *Detach side.* The guard accepts at count zero, a property attach lands before the detach
  interceptors run, and the detach then removes the `_attachedSubjects` entry of a subject that has
  just become a child. The parent's later removal no-ops and the count strands at one.
- *Attach side.* The record is taken at count zero, a property attach lands before
  `AttachSubjectToContext` runs, and the re-seed of an already-attached subtree happens anyway,
  overwriting its reconciliation baseline from the backing store.

Both need a genuine overlap between a root operation and a property write on the same subject, which
is narrower than the sequential misuse the guards reject.

Closing either needs the record transition and the whole interceptor pass inside
`LifecycleInterceptor`'s monitor, which means new API on `ILifecycleInterceptor`, so it goes with the
same change that serialises the two root operations.

**No test, deliberately.** The window both residuals need is between the record transition and the
interceptor pass, and the rendezvous that would pin it sits inside `LifecycleInterceptor`'s monitor,
which no public seam reaches. A test that cannot hit the window would assert only that the ordinary
sequential path works, which is coverage in appearance and not in substance.

### The aggregated two-interceptor configuration, which has no test

Ownership is claimed and released on **graph membership**, not interceptor identity. `ClaimOwnership`
accepts when the attaching context resolves the standing owner, and `ReleaseOwnership` releases when
the detaching context does. Identity alone is wrong in both directions: an aggregated context
resolves more than one `ILifecycleInterceptor` and every one of them attaches every subject, so an
identity claim would reject the second co-resolved interceptor as a foreign graph, and an identity
release would be a permanent no-op whenever the interceptor that claimed first is not the one that
brings the count to zero, leaving the subject owned with no references, reporting attached and
unable to join any other graph.

Two consequences follow, both confined to aggregated configurations that already share an
interceptor. Rejection of genuinely distinct graphs is unaffected, because a disjoint context cannot
resolve the standing owner.

- **The predicate is asymmetric.** A context that resolves the standing owner may claim, one that
  does not may not. Since the owner is whoever claims first among co-resolved interceptors, which of
  the two outcomes a configuration gets depends on resolved interceptor order.
- **Only the owning interceptor enforces the re-attach-during-detach rejection**, because
  `ThrowIfDetachIsUnwinding` is gated on `IsOwnedBy(this)`. In a two-interceptor aggregate a
  re-attach during the non-owner's unwind passes both that guard and the claim.

**No test.** The configuration is legal but has no user in this repository, so the gap is recorded at
the call site and here rather than closed.

## Rejected Alternatives

Recorded so that a refuted idea is not proposed again.

**Repointing the parent link when one of several parents detaches.** This would close the
multi-parent dark-subject gap by moving the link to a surviving parent. Proposed and refuted three
times, once per guard:

- *Unguarded.* Two reviewers independently built pure delegation cycles from it. The repoint is a
  second write site, and the cycle argument in "The Parent Link" holds only for one.
- *Guarded by `ResolveDelegationTarget`.* The guard cannot express the question. That method returns
  the chain's **terminal**, not path membership, so it cannot answer "is this context on the
  candidate's chain", and whenever the context is a pure delegator, which is exactly the case the
  repoint addresses, it returns a false safe.
- *Guarded by a hop-limited walk.* The walk length is the candidate's depth, so a 200-level graph
  yields a 201-hop chain and any limit small enough to be a guard refuses the repoint on every deep
  graph, which is precisely where multi-parent sharing is most likely.

And none of the three fires on the connector shape, where the failure has real users, because the
link gate sets no link there and there is nothing to repoint.

The failure mode it would introduce is also worse than the one it closes. A link cycle does not
surface as a catchable resolution error: the **detach path** throws, because
`InvokeRemovedLifecycleHandlers` resolves through the child's own context and the descent one level
down uses `subject.Context`. The reference counts, the `_attachedSubjects` entries and the
`_lastProcessedValues` entries then strand permanently, with the backing store already written
because `WriteProperty` calls `next()` first. It is unrecoverable from consumer code: a
consumer-built fallback cycle is undone with `RemoveFallbackContext`, but the link is internal and
the only path that clears it is the detach that just threw. So the trade is a rare silent read-side
loss against a rare permanent leak plus a wedged subtree.

**Genuine multi-graph support**, meaning one subject legitimately belonging to two lifecycle graphs
rather than the half-working state that reaches it today. Rejected on cost, which is not local:

- Graph identity threaded through the registry, path, connector and source APIs, because
  `TryGetService` throws when two services of a type resolve and `TryGetRegisteredSubject` resolves
  the registry that way.
- A merged interceptor chain whose interleaving between the two graphs is undetermined, and where a
  short-circuiting interceptor in one graph decides for both.
- The return of delegation cycles: building graph 1 with A above Q and graph 2 with Q above A makes
  each the other's parent.
- Ownership tags on every link.
- A permanent distinction between a per-graph and a total reference count.

If it is ever wanted it should be a deliberate design with graph identity as a first-class parameter.
The cross-graph rejection exists to make that need discoverable instead of silent.

## Invariants

After all concurrent `WriteProperty` / `DetachFromProperty` / `AttachSubjectToContext` / `DetachSubjectFromContext` operations complete:

1. **Reachable → Registered**: Every subject reachable from the root via the object graph is in `_attachedSubjects`
2. **Not reachable → Not registered**: Every subject NOT reachable from the root is NOT in `_attachedSubjects`
3. **`_lastProcessedValues` matches attachment state**: For every attached subject, `_lastProcessedValues` entries exist for all structural properties that have been written or seeded
4. **No dangling entries**: No `_lastProcessedValues` entries exist for detached subjects

### Transient inconsistency

Between `next()` and lock acquisition, the backing store and `_attachedSubjects` can temporarily disagree (a new child is in the backing store but not yet attached, or an old child is detached but still in the backing store). This window is invisible through the lifecycle's API (which requires the lock) and resolves when `WriteProperty` completes its locked section.
