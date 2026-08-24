# Lifecycle Interceptor: Internal Design

This document describes the internal ownership model, concurrency model and data structures of `LifecycleInterceptor` and its collaborators. For user-facing documentation, see [Tracking](../tracking.md).

## Overview

The lifecycle owns structural graph membership for one context: which subjects the context holds, through which occurrence-aware edges, and when a subject that lost its last support leaves. When a structural property (a subject reference, collection or dictionary) is written, the lifecycle diffs the old and new values, claims newly reachable subjects for the context, releases subjects that became unreachable, and fires attach and detach events that drive downstream systems such as `SubjectRegistry`.

A subject is attached to exactly one context, read lock-free through `subject.TryGetContext()`. It is held either by a root anchor or by a path of committed structural edges from an anchored root. There is no reference counting as an ownership predicate: ownership is reachability.

## Anchors

An anchor marks a subject as a root of its context. There are two kinds:

- **Explicit**: created by `AttachToContext(subject, context)`. Never cleared automatically; `DetachFromContext` is how a caller gives it up.
- **Provisional**: created by a context-taking constructor, `new Person(context)`. Consumed by the first inherited edge that provides *independent support*, meaning the edge's parent has an anchored ancestor other than the subject itself.

The independent-support rule is what makes ordinary graph building safe. Clearing on the first edge of any kind is unsound: `child.Parent = root` would consume the root's own anchor and the next removal anywhere would release the whole graph. In a mutually referencing pair the first-constructed subject keeps its anchor; that is the same outcome as any root the caller constructed and never removed.

The anchor lives on the executor (`IInterceptorExecutor.Anchor`), never mirrored into graph state, so it cannot drift from the attachment it belongs to.

## Decomposition

| Class | Owns |
|---|---|
| `LifecycleInterceptor` | the write protocol shell, the topology gate, attach and detach entry points, events, handler fan-out |
| `OwnershipGraph` | the owned-subject map, property baselines, claim and release primitives |
| `SubjectOwnership` | one subject's record: occurrence-aware incoming edges with inline-to-list promotion, the published parent snapshot |
| `StructuralReconciler` | the write-time diff and reconcile for scalar, collection and dictionary properties |
| `StructuralValueScanner` | the one interpretation of "which subjects does this value hold", serving reconcile, seeding, release and edge validation |
| `ReachabilityWalk` | the single `IsAnchorReachable(start, excluded)` backward search |
| `AttachTraversal` | edge recording and attach publication, including provisional-anchor consumption |
| `ReleaseTraversal` | deterministic first-visit release from a removed edge, cycle drain |
| `PropertyAdmission` | atomic admission of dynamically added properties |
| `LifecycleNotifier` | the notification surface handed to collaborators, and callback depth marking |
| `LifecycleScratch` | pooled scratch buffers for discovery, release and reconcile |

## Data Structures

**The owned-subject map** (`OwnershipGraph._owned`): a `ConcurrentDictionary<IInterceptorSubject, SubjectOwnership>` keyed by reference equality. Concurrent so that gate-free readers (`GetParents()`, `GetReferenceCount()`) can find a record without the topology gate; only the gate holder mutates it.

**Property baselines are the committed outgoing edges.** The last reconciled value of every structural property is the outgoing truth: a subject commits an edge to a child exactly when the baseline of one of its structural properties still contains that child. There is no second outgoing representation, which removes the whole class of bugs where two representations disagree, and it is what makes the release descent and the reachability walk read the same relation.

**Incoming edges** live on `SubjectOwnership`, occurrence-aware: each edge carries the property and the occurrence index or dictionary key, so `[a, a, b]` records two distinct edges for `a` and `GetReferenceCount()` answers 2. A single incoming edge is stored inline; a list is allocated only from the second edge.

**Parent snapshots activate lazily.** The first `GetParents()` on a subject takes that subject's `SubjectOwnership` monitor to set the activation bit and materialize the initial snapshot; from then on the subject publishes an immutable snapshot eagerly on every edge change and every later read is a lock-free snapshot read. A consumer that never asks pays nothing.

## The Reconcile Order Invariant

`Reconcile` commits outgoing edges (baselines) before updating incoming records. Incoming and outgoing state therefore legitimately disagree in three windows: reconcile commit, attach-time seeding, and cycle drain. **Any algorithm reading incoming edges must validate candidates against committed outgoing edges** (`OwnershipGraph.CommitsEdgeTo`). This is the single most load-bearing invariant in the area; the reachability walk validates every candidate parent against it and does not mark a rejected parent as visited.

## Reachability and Release

Release does not scan the context and does not maintain a forward mark. When an edge or an anchor is removed, `IsAnchorReachable(start, excluded)` walks **backward** from the questioned subject up its committed incoming edges to the nearest anchored ancestor. Release passes no exclusion; provisional-anchor consumption passes `(parent, excluded: subject)` so the subject's own anchor does not count as support for itself.

Release order is observable and deterministic: the traversal starts at the removed edge, collects committed children before dropping baselines, visits only subjects that are no longer anchor-reachable, in first-visit order, and drains closed cycles. Detach callbacks therefore arrive top-down. The owned-subject map is never enumerated for release.

An independently written forward-mark oracle lives in the test assembly (`OwnershipOracleTests`), cross-checking reachability, reference counts and occurrence-aware parents over seeded random mutation sequences. It found four real defects during the rewrite that the example-based suite did not.

## The Write Protocol

Structural writes are routed at generation time: the generator emits `SetStructuralPropertyValue` only for properties whose declared type can contain subjects, classified fail-closed, so scalar setters carry no runtime check of any kind. Dynamic proxy setters classify at proxy construction.

An attached structural write follows this sequence in `InterceptorExecutor.SetStructuralPropertyValue`:

1. Read the exact attached context, pin its context state with one volatile read, and resolve the lifecycle from that pinned state.
2. Enter the lifecycle gate (`ILifecycleInterceptor.EnterStructuralWriteGate`).
3. Enter the subject's attachment monitor.
4. Revalidate the attached context under both locks; release and retry if it moved.
5. Resolve the ordinary cached write chain from the routing's pinned state, not a fresh read, and execute it through the terminal. A chain resolved from a fresh read could contain a lifecycle the routing did not see, whose `WriteProperty` would take the gate inside the attachment monitor and invert the lock order; a lifecycle registered after the pin is invisible to this write as a whole and is seen by the next write.

A transient race with attach or detach therefore **orders** rather than throws; only a persistent conflict (the subject genuinely owned by another context) throws, before the backing field is written. An unattached subject enters only the attachment monitor, rechecks it is still unattached, and writes directly.

When the attachment moves between the routing read and the lock acquisitions, the structural write retries rather than orders. The loop is livelock-free: every retry is caused by another thread completing an attachment transition, and each transition requires the lifecycle gate, so a retry is evidence of progress elsewhere rather than of mutual blocking. It is not starvation-free, and no attempt bound is enforced: sustained attach and detach churn on one subject, concurrent with structural writes to that same subject, could in principle starve a writer. That is a pathological workload rather than a plausible one, and the lock-order tests drive the loop at 3,000 iterations without observing it. If it is ever observed, the mitigation is to order rather than retry, which is a rework of the write protocol and not a tuning change.

Inside the chain, `LifecycleInterceptor.WriteProperty` validates and claims the proposed new component before calling `next`, calls `next` exactly once, rereads the authoritative getter (which also serves normalizing setters), then reconciles committed edges: removals publish before additions, old occurrences in reverse order, new ones forward. `ReleaseUnusedClaims` compensates for a suppressed or throwing terminal and for normalizing setters that store a different graph than the validated one.

## Lock Ordering

The total order is **lifecycle gate → attachment monitor → SyncRoot**, with the per-subject `SubjectOwnership` monitor as a leaf below all three.

- The **lifecycle gate** is one private reentrant monitor per lifecycle, the outermost lock. Every topology change holds it. Reentrancy is required because same-lifecycle `TryAddProperties` re-enters from inside callbacks.
- The **attachment monitor** is the executor's private lock guarding the attachment triple (context, anchor, revision). Transitions are leaf acquisitions or taken under the gate. Attachment reads are lock-free (volatile fields, revision published last with release semantics), because consumers read them from inside their own locks and a locking read deadlocks against a held commit.
- **SyncRoot** is the executor's internal per-subject terminal lock, pairing the backing write with revision increment and write-state publication. The zero-interceptor read chain takes no lock; removing SyncRoot from the write terminal would permit torn reads of value types wider than 64 bits.
- The **`SubjectOwnership` monitor** is a per-subject leaf lock guarding the incoming-edge record and the parent snapshot. The lifecycle takes it under the gate for every edge mutation; the first `GetParents()` on a subject takes it without the gate, from any thread, to activate parent publication. Nothing foreign runs while it is held and the type never leaves the assembly, so it cannot participate in an ordering cycle.

Taking the attachment monitor before the gate deadlocks: a structural write holding a child's monitor would wait for the gate while a parent removal holding the gate reaches `ReleaseClaim(child)` and waits for that monitor. The lock-order tests inject exactly this inversion and fail with a bounded join rather than hanging.

`GetParents()` and `GetReferenceCount()` never take the lifecycle gate: `GetReferenceCount()` is a plain volatile read, and `GetParents()` is a lock-free snapshot read except for the first call on a subject, which takes that subject's `SubjectOwnership` monitor to activate publication. `SourceMonitor` holds its own lock across a graph walk that calls `GetParents()` and is also called from inside the gate, so a gate-taking read would deadlock.

## Callback Contract

Lifecycle callbacks are synchronous and exception-free by contract; violations propagate with no rollback. A callback may evaluate anything, including user getters, and may change no graph topology: no structural property write, no explicit attach or detach, and no cross-context `AddProperties`. A structural write and an explicit attach or detach throw `LifecycleContractViolationException` in every build, uniformly at every graph depth, because the silent failure modes are graph corruption and a deadlock between two lifecycle gates; a cross-context `AddProperties` is rejected with a plain `InvalidOperationException` before it enumerates input or blocks on the foreign topology gate. Property lifecycle callbacks (`IPropertyLifecycleHandler.AttachProperty`/`DetachProperty`) are not exempt: the derived-property handler evaluates user getters from its attach callback, and evaluation is what the contract permits.

`DerivedPropertyChangeHandler` absorbs exceptions from derived getters, keeping the last known value and recomputing on the next dependency write, and filters `LifecycleContractViolationException` out of that absorption so a contract breach cannot hide behind a derived value that silently never initializes. A derived property whose declared type can contain subjects also throws when it returns a subject this context does not own, because derived properties establish no ownership edges and such a subject would never be tracked.

The contract binds callbacks, not the rest of the write chain: a third-party `IWriteInterceptor` registered after `WithLifecycle` runs during `next` at callback depth zero, on the writing thread, holding the topology gate reentrantly, and can release the writing parent through a nested structural write or an explicit detach before the reconcile runs; a hand-written terminal setter and a dynamic subject's authoritative getter reread sit in the same window. An ownership check at `Reconcile` entry closes that shape: a released parent commits no baseline and enters no loop, and `ReleaseUnusedClaims` hands the proposed subjects' claims back. The released-parent early exits inside the reconcile loops remain load-bearing for the residual shape the entry check cannot see: side-effecting user code the loops themselves invoke at depth zero (a dictionary-key `Equals`, a user collection or dictionary implementation) can run the write protocol reentrantly and release the parent mid-flight. The inexact same-property fallback in `SubjectOwnership.RemoveIncoming` stays on its own justification: a reconcile commits the property's new value before retained edges adopt their new indices, so a release descent inside that window presents indices the incoming records have not adopted yet, and only the per-property occurrence count is authoritative there.

## Handler Order

`LifecycleInterceptor` implements `ILifecycleHandler` and occupies the former context-inheritance descent slot, so it is the public ordering seam: `[RunsBefore(typeof(LifecycleInterceptor))]` places a handler ahead of the descent, `[RunsAfter]` behind it. `SubjectRegistry` runs before it (every ancestor is registry-visible during attach); `SourceMonitor` and `HostedServiceHandler` run after it. Ordering attributes bind only between services that both implement the interface being resolved, and `ServiceOrderResolver` keys on the exact runtime type, which is one reason `LifecycleInterceptor` is sealed.

Observed orders on a three-level chain: attach ahead of the descent `top, mid, leaf`; attach behind `leaf, mid, top`; detach `top, mid, leaf` in both positions. Authoritative parent state is visible before the first handler runs.

## Invariants

- A subject is unattached or owned by exactly one exact context, with at most one anchor.
- Ownership is anchor-reachability over committed occurrence-aware edges; reference count is a projection, never a predicate.
- Property baselines and committed outgoing edges are one representation.
- Algorithms reading incoming edges validate against committed outgoing edges.
- Release is deterministic first-visit from the removed edge; the owned map is never enumerated.
- Reference-count reads are lock-free; parent reads are lock-free snapshot reads once the first call activates publication under the subject's leaf monitor; the lifecycle is the sole writer of parent state.
- The lock order is gate, then attachment monitor, then SyncRoot, with no path acquiring in any other order and the `SubjectOwnership` monitor as a leaf below all three.
