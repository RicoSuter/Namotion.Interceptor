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

The anchor lives on the executor (`IInterceptorExecutor.AttachmentAnchor`), never mirrored into graph state, so it cannot drift from the attachment it belongs to.

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

**Which properties carry edges** (`OwnershipGraph.IsStructural`): intercepted, not derived, and of a declared type that can contain subjects. The two exclusions answer different questions. Interception is generated only for partial properties, so every computed shape (an expression-bodied getter, an interface default) is already excluded before the derived test runs; what the derived test excludes is a store, a partial property with a backing field. `[Derived]` declares the value to be a function of other state, which makes the property a cache rather than the store of record, whether or not a field holds the result. A subject reachable only through a derived property is therefore never tracked, and `DerivedPropertyChangeHandler` rejects it with `LifecycleContractViolationException` rather than letting it go silently unowned. That rejection needs `WithDerivedPropertyChangeDetection()`, because nothing else ever evaluates a derived getter.

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

Structural writes are routed at runtime, inside the one `SetPropertyValue` entry: the routing flag lives next to the per-type chain index as a static readonly field per `TProperty` instantiation, so the scalar route pays one predictable branch and no classifier call, and both fields are read off one static base and threaded down into the chain lookup. The routing follows `TProperty`: a boxed `object` fails closed to the structural side, while explicitly narrowing `TProperty` below the declared property type routes scalar. Narrowing does not opt out of ownership: the lifecycle classifies on the declared type (a flag precomputed on `SubjectPropertyMetadata`), so a narrowed write on an attached subject runs the full structural section inside the chain, and the scalar route's unattached arm consults the declared type so a narrowed write still answers the terminal's commit predicate and cannot slip past an attach's seeding. Hand-written setters therefore get the full protocol from the same call generated ones make, instead of silently skipping it by picking the wrong accessor. Callers whose values travel boxed but who know the declared type at registration time, the registry's dynamic property setters and the dynamic proxy, build their setter once per property through a cached typed delegate that instantiates this same entry with the declared type as `TProperty`, so scalar dynamic writes stay off the structural route and the chain carries the unboxed value; a write the declared type cannot represent, a null into a non-nullable value type say, falls back to an object-typed write that carries the boxed values to the stored setter unchanged.

The protocol separates the two jobs its locks do, and holds no lock while user code runs. Topology atomicity (claim before write, reconcile after write, no topology change interleaving with either) belongs to the lifecycle's gate, taken inside the chain: the chain compiler partitions every compiled write chain so all `ILifecycleInterceptor` instances run last, so no registered interceptor executes inside the gate, and `next()` under the gate reaches the terminal and nothing else. Commit-versus-transition atomicity (a write commits only against the world its chain was resolved for) belongs to a commit predicate the terminal evaluates under the subject's attachment monitor, immediately around the commit.

A structural write in `InterceptorExecutor` is a bounded loop that holds nothing:

1. Volatile-read the attached context, pin its `ContextState`, and resolve the chain from the pinned state. An unattached subject resolves the zero-interceptor chain instead, so its commit still stamps the revision and the write state.
2. Construct the per-attempt write context carrying the terminal protocol fields: the structural-route flag, the expected attached context (null on the unattached arm), and the pinned chain state.
3. Execute the chain. If the attempt aborted with a moved attachment, loop against the fresh attachment (threading the consumed change origin through); otherwise return.

At the terminal, iff the write is structural-routed, the commit predicate runs under the attachment monitor: an unattached subject commits (the null rule; the monitor orders the commit against every future claim, so a later attach's seeding sees it); an attachment that differs from the expected one aborts the attempt, and the executor re-routes the whole write; a chain compiled without a lifecycle also aborts when the context's current state now resolves one (the currency check, deliberately scoped so a plain service registration never disturbs an in-flight write). Commit means the ordinary terminal body under `SyncRoot`.

Inside the chain, `LifecycleInterceptor.WriteProperty` classifies on the declared type, then branches on the subject's attachment: a subject of this context enters the gate, re-checks under it, validates and claims the proposed new component before calling `next` (which is the terminal), and, only when the terminal committed, rereads the authoritative getter (which also serves normalizing setters) and reconciles committed edges: removals publish before additions, old occurrences in reverse order, new ones forward. A subject that left for another context aborts the attempt without calling `next`. An unattached subject takes the write-through arm: the expected attached context is overwritten to null (so a concurrent re-attach, even to the same context, fails the predicate instead of landing a value the re-attach seeding already read past) and `next()` commits under the null rule, with no claims and no reconcile but with change notification intact on the original chain. `ReleaseUnusedClaims` compensates for a suppressed or throwing terminal, for aborted attempts, and for normalizing setters that store a different graph than the validated one.

A transient race with attach or detach therefore **orders** rather than throws: lifecycle-mediated transitions of an attached subject wait on the gate the write section holds, a release lets the write commit unattached under the null rule, and a claim or move re-routes the write through the new owner's chain and protocol. Only a persistent conflict (the subject genuinely owned by another context at claim time) throws, before the backing field is written. The retry loop is bounded at `InterceptorExecutor.MaxWriteRouteAttempts` (100, matching the derived handler's stabilization bound): a re-route needs a genuine attachment transition of the subject or the once-per-context lifecycle registration, so exhausting the bound means user code transitions the subject on every attempt, and the executor answers with a diagnostic `InvalidOperationException` instead of the silent unvalidated write-through that shape used to get. The loop is not starvation-free under adversarial churn, which is accepted.

Because discovery reads the getters of unattached subjects with no synchronization, the claim step verifies what it claimed: `TryClaimDiscovered` rereads each newly claimed subject's structural getters once (a worklist, since a reread can reveal further unattached subjects written in the discovery window), claims what discovery missed, and rejects the whole operation with the caller's own message when the reread finds a foreign subject, releasing every claim it made. Together with the commit predicate on unattached writes, this closes the window where a value committed into a component between discovery and claim would be owned never or half.

## Lock Ordering

The total order is **lifecycle gate → attachment monitor → SyncRoot**, with the per-subject `SubjectOwnership` monitor as a leaf below all three. The order binds the library's own acquisitions on the structural write protocol; no lock is held across the interceptor chain, so registered interceptors and subscriber dispatch run outside all of them.

- The **lifecycle gate** is one private reentrant monitor per lifecycle, the outermost lock. Every topology change holds it. Only the lifecycle enters it, for a structural write from inside the chain, where the chain partition makes it the last interceptor. Reentrancy is required because same-lifecycle `TryAddProperties` re-enters from inside callbacks.
- The **attachment monitor** is the executor's private lock guarding the attachment triple (context, anchor, revision). Transitions are leaf acquisitions or taken under the gate; the write terminal takes it for the commit predicate and the commit only, releases it before any re-route, and never acquires a gate inside it. Attachment reads are lock-free (volatile fields, revision published last with release semantics), because consumers read them from inside their own locks and a locking read deadlocks against a held commit.
- **SyncRoot** is the executor's internal per-subject terminal lock, pairing the backing write with revision increment and write-state publication, taken inside the monitor on structural commits. The zero-interceptor read chain takes no lock; removing SyncRoot from the write terminal would permit torn reads of value types wider than 64 bits.
- The **`SubjectOwnership` monitor** is a per-subject leaf lock guarding the incoming-edge record and the parent snapshot. The lifecycle takes it under the gate for every edge mutation; the first `GetParents()` on a subject takes it without the gate, from any thread, to activate parent publication. Nothing foreign runs while it is held and the type never leaves the assembly, so it cannot participate in an ordering cycle.

Taking the attachment monitor before the gate deadlocks: a claim inside the gate section hands executors around through that same monitor, so a path that acquired the monitor first and then wanted the gate would oppose it. The terminal's user-facing delegates are the one exposure outside the library's claim: the stored setter of a registry dynamic property executes at the terminal under monitor and SyncRoot (and the gate for a write on a lifecycle context), so a stored setter that acquires a lifecycle gate inverts the order; that constraint is documented on `RegisteredSubject.AddProperty`. The lock-order tests drive writes against removals and against explicit attach and detach concurrently under bounded joins, so an inversion reaching production fails a join instead of hanging the suite.

`GetParents()` and `GetReferenceCount()` never take the lifecycle gate: `GetReferenceCount()` is a plain volatile read, and `GetParents()` is a lock-free snapshot read except for the first call on a subject, which takes that subject's `SubjectOwnership` monitor to activate publication. `SourceMonitor` holds its own lock across a graph walk that calls `GetParents()` and is also called from inside the gate, so a gate-taking read would deadlock.

## Callback Contract

Lifecycle callbacks are synchronous and exception-free by contract; violations propagate with no rollback. A callback may evaluate anything, including user getters, and may change no graph topology: no structural property write, no explicit attach or detach, and no cross-context `AddProperties`. A structural write and an explicit attach or detach throw `LifecycleContractViolationException` in every build, uniformly at every graph depth, because the silent failure modes are graph corruption and a deadlock between two lifecycle gates; a cross-context `AddProperties` is rejected with a plain `InvalidOperationException` before it enumerates input or blocks on the foreign topology gate. Introducing a subject from a callback is not forbidden as such: what a callback cannot do is write an existing structural property, because there is no protocol for claiming the new component while the descent that is publishing it is still running. Adding a dynamic property whose value is a subject does have one, so `RegisteredSubject.AddProperty` from a same-context callback is supported and property admission claims the component. A default child assigned by a callback therefore has no direct replacement and belongs at construction time; a third-party `IWriteInterceptor` is not a substitute when the parent assigns the child in its own constructor, because that write predates context publication and is never intercepted. Property lifecycle callbacks (`IPropertyLifecycleHandler.AttachProperty`/`DetachProperty`) are not exempt: the derived-property handler evaluates user getters from its attach callback, and evaluation is what the contract permits.

`DerivedPropertyChangeHandler` absorbs exceptions from derived getters, keeping the last known value and recomputing on the next dependency write, and filters `LifecycleContractViolationException` out of that absorption so a contract breach cannot hide behind a derived value that silently never initializes. A derived property whose declared type can contain subjects also throws when it returns a subject this context does not own, because derived properties establish no ownership edges and such a subject would never be tracked.

The contract binds callbacks, not the rest of the write chain: a third-party `IWriteInterceptor` registered after `WithLifecycle` runs upstream of the lifecycle (the chain partition compiles every lifecycle last), holding no lock, and can release the writing parent through a nested structural write or an explicit detach before the lifecycle runs; the write then flows through the lifecycle's write-through arm, which makes no claims and runs no reconcile while the terminal's null rule still commits and notifies. A hand-written terminal setter and a dynamic subject's authoritative getter reread still execute inside the gate section, and the ownership check at `Reconcile` entry covers a parent they released: a released parent commits no baseline and enters no loop, and `ReleaseUnusedClaims` hands the proposed subjects' claims back. The released-parent early exits inside the reconcile loops remain load-bearing for the residual shape the entry check cannot see: side-effecting user code the loops themselves invoke at depth zero (a dictionary-key `Equals`, a user collection or dictionary implementation) can run the write protocol reentrantly and release the parent mid-flight. The inexact same-property fallback in `SubjectOwnership.RemoveIncoming` stays on its own justification: a reconcile commits the property's new value before retained edges adopt their new indices, so a release descent inside that window presents indices the incoming records have not adopted yet, and only the per-property occurrence count is authoritative there.

## Handler Order

`LifecycleInterceptor` implements `ILifecycleHandler` and occupies the former context-inheritance descent slot, so it is the public ordering seam: `[RunsBefore(typeof(LifecycleInterceptor))]` places a handler ahead of the descent, `[RunsAfter]` behind it. `SubjectRegistry` runs before it (every ancestor is registry-visible during attach); `SourceMonitor` and `HostedServiceHandler` run after it. Ordering attributes bind only between services that both implement the interface being resolved, and `ServiceOrderResolver` keys on the exact runtime type, which is one reason `LifecycleInterceptor` is sealed.

Observed orders on a three-level chain: attach ahead of the descent `top, mid, leaf`; attach behind `leaf, mid, top`; detach `top, mid, leaf` in both positions. Authoritative parent state is visible before the first handler runs.

## Invariants

- A subject is unattached or owned by exactly one exact context, with at most one anchor.
- Ownership is anchor-reachability over committed occurrence-aware edges; reference count is a projection, never a predicate.
- Property baselines and committed outgoing edges are one representation.
- A derived property never carries an edge: `[Derived]` declares a cache, not the store of record, whether or not a backing field holds the result.
- Algorithms reading incoming edges validate against committed outgoing edges.
- Release is deterministic first-visit from the removed edge; the owned map is never enumerated.
- Reference-count reads are lock-free; parent reads are lock-free snapshot reads once the first call activates publication under the subject's leaf monitor; the lifecycle is the sole writer of parent state.
- The lock order is gate, then attachment monitor, then SyncRoot, with no library path acquiring in any other order, no lock held across the interceptor chain, and the `SubjectOwnership` monitor as a leaf below all three.
