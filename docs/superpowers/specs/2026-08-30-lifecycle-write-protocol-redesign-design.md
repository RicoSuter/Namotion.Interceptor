# Lifecycle and Structural Write Protocol Redesign

**Status:** Proposed, pending maintainer review

**Pull request head:** `c5079c6f0cb3a06ea2bc395e2dba7b812b3fa88b`

**Local protocol branch:** `23d4a54b928c5d1c62b8b152919361ef6854da73`

**Base:** `0418410c2da2ca5aa39fb25fb9d5fda3b53f429b`

## Decision

Keep the product contract of pull request #494 and implement it with five design blocks: atomic attachment state, coordinated structural storage, one ownership graph, nonblocking operation protection, and post-commit notifications. These blocks replace the whole-chain lifecycle gate and the separate recursive attach, reconcile, release, and reachability protocols.

The field store and its graph publication have one coordinated terminal boundary. Core owns the per-subject storage lock and invokes the existing raw reader and writer delegates there. Tracking owns the short per-context topology gate and publishes an already staged graph transaction. Interceptors, ordinary getters that enumerate user values, metadata publishers, and callbacks run outside framework locks. Contract-bound raw readers and writers are the narrow exception described below. The raw reader runs under the terminal lock, and the faithful raw writer runs inside the final terminal-lock plus topology-gate commit because that assignment is the write's linearization point.

Active structural leases and ownership reservations are temporary reachability roots. Ordinary writes use an affected-component release calculation. Only a release deferred by active protectors may request an uncommon context-wide sweep. There are no pending-release closure groups, group merges, topology freezes, pending-terminal state machines, per-journal notification reference counts, or context-wide derived-transaction counters.

## Normative contract and precedence

The current PR description, `docs/design/tracking-lifecycle.md`, and the acceptance tests at the reviewed PR head define observable behavior. This redesign may change that behavior only where the changes below are required for correctness or deadlock freedom. A size reduction alone does not authorize removing a PR requirement.

The necessary changes are limited to:

- Structural normalization occurs before the coordinated terminal, including rewrites by `On<Property>Changing` and `IWriteInterceptor`. The terminal freezes that final value, and the raw structural storage operation is faithful by construction.
- A post-terminal assignment to public `NewValue` remains context-local unwind state for compatibility. It cannot alter the frozen committed value or any built-in storage, origin, lifecycle, property-change, or derived publication.
- Attached structural Dynamic and hand-written storage must supply a coordinated faithful raw reader/writer pair. Legacy or setter-only structural storage is rejected before its chain and store; scalar routing is unchanged.
- A structural write or attachment transition that races an exclusive phase may receive a prompt `LifecycleConflictException` before its terminal instead of waiting while arbitrary code can be waiting for it.
- Callbacks remain synchronous for their originating operation but execute outside framework locks and may overlap across threads. Ordering is guaranteed within one committed journal, not globally across threads.
- An older callback that overlaps a later detach uses its immutable payload for historical context. The exact context remains available throughout the detach operation's own callbacks but is not retained solely for older journals.
- Direct in-place mutation of an assigned mutable collection remains outside the intercepted-write contract. The committed immutable snapshot changes only on an intercepted assignment or lifecycle capture.

The PR's intentional removals remain removals. This redesign does not restore fallback contexts, subject-local context services, `ContextInheritanceHandler`, `ParentTrackingHandler`, public `SyncRoot`, `SetPropertyValueWithInterception`, or the executor-as-context model.

## Product behavior retained

### Ownership and attachment

- A subject is attached to exactly one context or none. Direct movement between two non-null contexts is illegal.
- Ownership is reachability from explicit and provisional anchors over committed occurrence-aware edges. Reference count is a projection, never an ownership predicate.
- `AttachToContext` creates a strict explicit anchor. Promoting inherited same-context ownership or a provisional anchor to explicit is supported; duplicate explicit attach retains the PR's rejection behavior.
- A context-taking constructor creates a provisional anchor. Independent support from an anchored ancestor may consume it; self-edges and back-edges do not. The tested PR policy for provisional cycles and competing roots remains unchanged.
- Explicit detach removes only the explicit anchor and releases only the subjects no longer reachable from another anchor or active protector.
- Constructor mirroring remains, including its documented unsupported constructor shapes and dependency-injection behavior.
- A lifecycle-free attach is root-only. Descendants remain unattached because no ownership graph exists.
- The lifecycle must be configured before any subject attaches. Late registration throws. Concurrent context configuration and attachment remains unsupported.

### Structural topology

- Every occurrence is an edge. Duplicate list and dictionary occurrences have independent reference counts.
- Edge identity is parent property plus child identity plus stable occurrence ordinal. Collection index or dictionary key is publication payload, not identity.
- Reorder and rekey update index payload without false context detach or attach transitions.
- Closed unanchored cycles release. Shared DAGs and multiple roots retain a subject while any anchored path remains.
- New support commits before obsolete support is removed, so a retained reparent target and its subtree remain continuously owned.
- A generated partial `[Derived]` property owns its backing store. A Dynamic derived property with a setter owns its store. Getter-only projections do not create ownership edges.
- Parent and reference-count reads use immutable reference-identity publications and remain coherent without taking the topology gate.

### Interception and storage

- Each interceptor executes once. The protocol never replays arbitrary interceptor side effects.
- A veto creates no terminal revision, proposal reservation, property topology delta, or write journal. Releasing its parent lease may still complete a deferred sweep caused by another already committed removal.
- `On<Property>Changing` and downstream `IWriteInterceptor` implementations may rewrite `NewValue` while the chain advances toward the terminal. Each downstream interceptor sees the value produced by its predecessor. An interceptor forwards the received context by `ref` and invokes `next` at most once; copied contexts are outside the supported contract.
- Terminal entry atomically takes a shallow `TProperty` snapshot of the final `NewValue` on the received context. Storage, origin finalization, lifecycle capture, graph publication, `GetFinalValue()`, and built-in property-change or derived publication use that frozen value. Structural occurrences are materialized separately from the shallow frozen value before framework locks are acquired.
- Assigning public `NewValue` after `next` may change what later custom interceptors observe on unwind, matching its mutable context role, but it cannot alter the internal frozen value or already committed state.
- A generated structural write publishes the exact predecessor reread under the terminal lock. Interceptor entry may have observed an earlier coherent snapshot, but unwind and property-change publication use the value actually replaced by that terminal revision.
- Property-change and derived interceptors unwind only after the corresponding topology publication has committed.
- An active write or same-context reservation continuously retains its protected subject and committed outgoing closure. A concurrent removal may commit without detaching that protected closure; final reachability is reconsidered when protectors leave.
- Scalar property reads and writes retain their existing fast path.

### Callbacks and consumers

- `SubjectAttached` occurs once on first ownership and `SubjectDetaching` once on final release.
- Attach handlers before the `LifecycleInterceptor` ordering seam observe the subtree top-down. Attach handlers after it observe the subtree bottom-up. Detach handlers on both sides observe it top-down.
- Incoming parents, reference count, attachment state, and property occurrence projections publish before the first handler for their journal.
- Direct replacement reports the actual old-edge release before the new-edge addition, although new support already protects the retained graph internally.
- Retained reparent targets emit edge and index changes without false subject detach and attach events.
- Structural writes and explicit attach or detach invoked synchronously from lifecycle callbacks remain rejected. Same-context `AddProperties` callback admission remains supported. Cross-context topology entry on a thread already in a logical topology or callback scope fails promptly.
- `AddProperties` materializes input once, rejects duplicate and foreign batches atomically, captures each qualifying structural getter once per attempt, calls the publisher exactly once only after acceptance, and invokes property callbacks in caller order.
- Registry remains a projection of committed lifecycle state. Connector-created subjects are assigned before population, preserving the PR's notification set and ordering.

## The five design blocks

### 1. Atomic attachment state

Each executor publishes one immutable attachment record containing exact context, anchor, phase, and monotonic revision. Public reads observe one complete record. The executor attachment monitor protects only short compare-and-publish transitions; no user code runs while it is held.

Built-in topology transitions occur under the context topology gate and then one executor monitor. A detaching record retains the exact context while that detach journal runs and rejects conflicting operations. The final clear is another short gate-and-monitor publication after the journal drains.

Executor-local leases and reservation groups retain exact token identity and reference counts. The simplification removes additional context-owned pending-release group or closure sets, not the exact token identities needed to reject foreign disposal and premature reuse.

### 2. Coordinated structural storage

The PR-head generated getter already passes a static raw field reader, and its setter passes a static raw writer. The setter also passes `_field` as `CurrentValue`, but that expression is evaluated before the executor acquires its terminal lock and can therefore be stale or torn under concurrent writers. The only required generator change is for a structural setter to pass the same static raw reader beside its existing raw writer. The current local branch already emits this shape.

Core uses those delegates under the executor terminal lock to reread the exact predecessor and faithfully store the final intercepted value. Generated normalization stays in `On<Property>Changing` or an interceptor. Scalar generated accessors retain the PR-head output and fast path.

`PropertyWriteContext<TProperty>` keeps its public `NewValue` setter for source and binary compatibility. Internally it keeps the mutable context value, a separate shallow frozen terminal value, and a terminal-entered flag. The first terminal freezes and returns the value before any lifecycle capture or raw store; a second terminal is rejected before another store. `GetFinalValue()`, origin finalization, property-change publication, and derived recalculation use the frozen value after terminal entry. This adds no generated API and no type-dependent scalar-versus-structural mutation rule.

`CurrentValue` keeps its public getter and gains an internal backing-field update. Entry-side interceptors see the coherent entry snapshot. Under the terminal lock, the trusted raw reader replaces that backing value with the exact predecessor before storage, so interceptor unwind and property-change publication report the value this terminal revision actually replaced. Once terminal entry occurs, `GetFinalValue()` returns the frozen value even for derived storage; it does not invoke a getter during unwind.

Framework correctness uses a separate internal terminal-committed marker. Public `IsWritten` remains source-compatible observation state, but an interceptor cannot forge a lifecycle/property publication by setting it to true before `next`, or suppress committed cleanup by setting it to false after `next`.

Dynamic properties cannot expose a native field by reference. The library-owned `DynamicSubjectFactory` dictionary reader/writer is a faithful coordinated pair. A custom `RegisteredSubject` Dynamic property or hand-written structural property is supported while attached only when it supplies both a nonblocking, non-reentrant raw getter and an exception-free setter that faithfully stores its argument. A setter-only structural property cannot provide exact predecessor or attach capture and is rejected promptly while attached. Normalization, filtering, reorder, or substitution moves to an interceptor. No post-store normalization or authoritative recapture path exists. Scalar Dynamic routing is unchanged, and structural routing uses declared metadata even when a value is boxed.

An advanced hand-written subject may use the same coordinated raw-reader and faithful raw-writer entry as generated code. These delegates must only read or assign the backing store and must not block or reenter. The general `IInterceptorExecutor.SetPropertyValue` entry remains for scalar and legacy manual use, but an attached structural write using the built-in lifecycle rejects that legacy shape before invoking its writer because it cannot provide a trusted terminal reread. This is the required migration for hand-written structural subjects.

Retain the current local concrete names `GetGeneratedPropertyValue` and `SetGeneratedPropertyValue` to avoid another generator and snapshot rename. They remain hidden from normal discovery with `EditorBrowsable(Never)`, but they are public ABI on `InterceptorExecutor` and therefore remain in public API review. Advanced hand-written subjects may use them despite the generated-oriented name. No lifecycle-specific method is emitted into consumer classes.

Coordinated structural reads take the same terminal lock. A direct backing-field alias can still observe a store in progress because it bypasses the accessor; derived validation handles that narrow observation through the active reservation described below.

### 3. One ownership graph

`OwnershipGraph` is the only committed topology engine. It accepts immutable structural snapshots, exact token state, captured revisions, and library-owned records. It never receives a raw user value or delegate.

For an ordinary property transaction it:

1. Diffs the prior and proposed occurrence snapshots.
2. Stages new outgoing support and immutable incoming publications.
3. Applies new support before obsolete support.
4. Starts release checks only from affected removed targets.
5. Determines final reachability using anchors and active protectors as roots.
6. Builds the immutable notification journal in deterministic PR order.

Ordinary writes do not scan the entire context. When removal is deferred by active protectors, the graph sets one context-level deferred-sweep flag. A protector release while the flag is set may perform a full mark-and-sweep from anchors plus the protectors that remain. This uncommon O(graph) fallback replaces exact pending closure groups and their merge protocol.

Provisional-anchor consumption remains an isolated root-selection policy over the final graph. The redesign does not add attachment ordinals or a second strongly connected component protocol unless a retained PR acceptance test proves the current policy incorrect.

The following migration helpers are deleted rather than retained beside the graph engine:

- `StructuralReconciler`
- `AttachTraversal`
- `ReleaseTraversal`
- `ReachabilityWalk`

Their mutable piecemeal edge APIs, immediate-claim adapters, seeding compatibility branches, rejected-attach rollback, stale-edge validation, and obsolete scratch pools are removed with their last caller.

### 4. Nonblocking operation protection and linearization

A structural write follows one sequence:

1. Core reads and pins the apparent attachment/context route without taking the target context gate, enters or validates the logical context scope, and rejects a same-thread second-context operation before touching that context's coordinator or admission state.
2. Core acquires a parent structural lease through the selected context's short gate-and-monitor admission and revalidates the route. If attachment changed, it releases the lease and scope and retries routing before any interceptor runs. A racing exclusive transition fails before the chain.
3. Interceptors run without framework locks. They may rewrite `NewValue` before forwarding. A veto ends the operation.
4. At the terminal boundary, Core freezes `NewValue` exactly once. Tracking captures that final value into immutable occurrence snapshots outside locks. Newly discovered detached components are captured once per attempt.
5. Tracking acquires one exact same-context reservation group for the proposed component. Capture records every participant's attachment, metadata, and structural terminal revisions.
6. Core acquires the parent terminal lock and rereads the exact predecessor through the raw reader.
7. Tracking acquires the context topology gate and revalidates the active lease identity, same context, commit-permitting attachment phase, reservations, and every captured participant revision. It does not require an unchanged anchor revision because the lease may temporarily root a parent whose explicit anchor was concurrently removed. It rebases the delta against the latest committed property snapshot; an ordinary concurrent terminal that committed first is not itself a conflict.
8. Tracking completes every fallible allocation and stages the graph publication and journal.
9. Core invokes the faithful raw writer and assigns the terminal revision while both the terminal lock and topology gate remain held.
10. Tracking performs only nonthrowing publication swaps for that revision.
11. Tracking releases the topology gate, Core releases the terminal lock, finalizes committed reservations, stores the immutable journal on the per-call context, and returns to interceptor unwind.
12. `LifecycleInterceptor` drains that journal outside locks after its downstream `next` returns, including from a `finally` path when downstream code throws after a committed terminal. The executor releases the parent lease and logical scope only after the entire interceptor chain exits. A flagged deferred sweep runs from that final short release path.

The only nested framework lock order is:

```text
subject terminal lock -> context topology gate -> one executor attachment monitor
```

Operations without a terminal lock use:

```text
context topology gate -> one executor attachment monitor
```

No topology path acquires a terminal lock. No executor monitor is retained while requesting the topology gate. Enumeration, equality, interceptor code, metadata getters and publishers, lifecycle handlers, property handlers, Registry callbacks, and events run outside all framework locks. Final origin equality and lazy timestamp resolution are completed and cached before the terminal lock because they may call user equality or the configurable timestamp provider. Only cached primitive write-state data is stamped inside the commit.

Lease and reservation admission and disposal briefly share the context gate, then the executor monitor, and revalidate attachment. If admission enters first, reachability sees the protector. If topology publication enters first, admission observes the published state. No topology-freeze or rollback phase is needed.

Explicit attach uses an exclusive reservation, captures the detached component outside locks, revalidates all capture revisions, and commits through the same graph engine. Explicit detach removes the explicit anchor and runs the same affected release. `AddProperties` materializes and captures outside locks, acquires an exclusive admission reservation, validates under the topology gate, releases the gate to invoke its existing contract-bound exception-free metadata publisher exactly once, reacquires the gate, and revalidates metadata, attachment, and reservation revisions before final graph publication. The exclusive token spans both gate sections, so no competing admission can pass between them. Detached subjects may publish metadata directly; a detaching subject may publish metadata but creates no new ownership edges.

### 5. Post-commit notifications

The graph transaction stages one immutable journal and complete immutable subject or property projections before publication. Callbacks run after all framework locks are released and may overlap on different threads.

A lightweight Core-owned thread-local logical scope retains the PR restrictions on topology-changing callback reentry and second-context structural operations. The executor establishes or validates its context after its lock-free route read and before entering that context's topology admission coordinator, so a downstream write, attach, detach, or discovery side effect cannot touch a different context's gate or admission state. Tracking marks callback depth on that same scope while permitting the documented same-context `AddProperties` admission. Same-context nested non-callback interceptor work retains the PR behavior. The scope is not a synchronization lock.

Each affected subject and property projection carries only the monotonic revision and complete immutable state required for same-entity stale suppression. A context-global publication sequence and per-edge revisions are not added. Public lifecycle payloads gain only exact context, relevant entity revision, and the complete projection required by third-party consumers; no public coordinator or transaction API is introduced.

Registry applies a complete subject or property projection only when its revision is newer than the stored revision. It does not replay a stale index-keyed delta or enumerate a live collection under Registry locks. The `IPropertyLifecycleHandler.RefreshCollectionProperty` default method remains source-compatible during this PR, but built-in Registry stops using it.

Notifier dispatch continues after a callback exception so graph cleanup and later built-in handlers are not stranded. An ordinary operation throws the single exception or an `AggregateException` after its journal drains. Core completes the parent lease explicitly after the full chain and asks the topology admission coordinator to perform any deferred sweep and drain its journal outside locks. Sweep callback failures are aggregated with the captured chain exception, preserving that exception as primary. Internal reservation and lease `Dispose` remain no-throw safety fallbacks; if one must complete a sweep outside normal operation completion, it drains the journal and reports callback failures through `Trace` without replacing an exception already unwinding.

If downstream interceptor unwind throws after a committed terminal, the field and topology stay committed and the lifecycle journal still drains. The interceptor exception remains primary; callback failures are aggregated without replacing it. The guaranteed finally-drain covers the lifecycle ownership journal and Registry projection. `PropertyChangeInterceptor`, `DerivedPropertyChangeHandler`, generated `On<Property>Changed`, and generated INPC remain ordinary later unwind/caller behavior and may be skipped by exception propagation as before. When built-in property-change or derived interceptors do run, they use the exact terminal predecessor, frozen value, and terminal revision.

## Derived-property validation

An intercepted generated or Dynamic structural read takes the terminal lock and cannot observe the faithful store before its graph publication. This removes the context-wide transaction counter, withheld list, pending-terminal registry, lost-wakeup continuations, and sticky derived topology fault.

A getter that deliberately reads the backing field through an uninstrumented alias may observe the new subject during the few instructions between the faithful store and graph publication. Both occur while the writer holds the topology gate. When derived validation encounters an otherwise unowned subject, it requires an exact same-context reservation that explains the transient value, releases user/framework locks, crosses that context's topology gate as a barrier, and reevaluates the getter and ownership outside the gate. Acquiring the gate orders the recheck after that compliant store's publication or abandonment. Back-to-back writers may expose a different reserved value immediately afterward, so validation repeats the reservation test and barrier while the newly observed orphan has an exact explaining reservation. It reports the existing lifecycle contract violation only when an orphan has no such reservation. No reservation continuation, completion registration, retry queue, or context-wide quiescence inference is required.

## Error handling

- Foreign ownership, reservation conflicts, stale captures, and legacy attached structural entry fail before the coordinated store.
- Capture or enumeration failure releases reservations and leaves field and graph unchanged.
- Topology validation and staging failure occurs before the store and leaves field and graph unchanged.
- Generated, Dynamic, and hand-written coordinated raw writers are trusted terminal primitives. If one violates its faithful, exception-free contract, the implementation is outside the lifecycle correctness contract; Core does not add a post-store recapture, rollback, pending descriptor, or sticky fault to repair arbitrary storage code.
- Assigning public `NewValue` after terminal entry may alter only later custom-interceptor context observation. It cannot alter frozen storage, origin, graph, or built-in publication.
- An exception after `next` from an interceptor observes an already committed field and topology, matching normal interceptor semantics.
- Callback failure never rolls back committed state and never strands a detaching phase.
- No general pending-terminal or sticky topology-fault state is introduced.

## Public and assembly boundaries

- Keep `IInterceptorExecutor`, interceptor delegate signatures, explicit attach and detach extensions, `SubjectPropertyRegistration`, and both `AddProperties` entry points source-compatible.
- Keep the two current local coordinated concrete executor methods as the one advanced hand-written and generated storage seam. Public API snapshots must acknowledge them.
- Add one internal Core terminal-coordinator contract implemented by Tracking through the existing friend-assembly relationship. Do not add a public lifecycle coordinator.
- Remove the PR-only `ILifecycleInterceptor.EnterStructuralWriteGate` and `ExitStructuralWriteGate` methods. They are absent on master.
- Keep raw `TryUpdateAttachment` for alternative lifecycle implementations. The built-in lifecycle guards its own transitions with leases, reservations, and phases; alternative lifecycle implementations remain responsible for their synchronization.
- Keep lifecycle payload additions minimal and limited to unlocked callback correctness.

## Simplification requirements

Production code means C# under `src`, excluding tests, benchmarks, samples, snapshots, and E2E projects.

At local head `23d4a54b`, the five-project lifecycle scope is 444 production lines above PR head and 738 lines above the final `+2,800` ceiling relative to master. The revised implementation must remove at least 738 net production lines from this local head before completion.

The final implementation must satisfy all of these gates:

- Net production lines are materially negative relative to PR head `c5079c6f`, not negative by a token amount.
- Core plus Tracking are at most `+2,300` production lines relative to master.
- Core, Tracking, Generator, Registry, and Dynamic together are at most `+2,800` production lines relative to master.
- The same five-project scope is at most `+12` net production files relative to master.
- There is one snapshot builder, one ownership graph, one structural terminal protocol, and one notification path.
- A compatibility implementation may not remain beside its replacement.
- Focused types retain one responsibility even when the file-count ceiling discourages unnecessary new files.

Expected deletions include the four recursive topology helpers, the whole-chain gate, old reconcile preparation, immediate-claim adapters after cutover, seeded and releasing compatibility branches, rejected-attach rollback, context-wide transaction deferral, raw collection refresh callers, and obsolete scratch pools. The callback guard is simplified into the logical notification scope rather than deleted as a behavior change.

## Verification requirements

- Terminal tests prove ordered downstream rewrites, the frozen final value, noncommitting mutation after `next`, internal commit authority despite `IsWritten` forgery or suppression, veto behavior, exact predecessor, after-`next` exception semantics, and rejection of unsafe legacy or setter-only attached structural entries.
- Generated, Dynamic, and hand-written storage tests prove faithful storage, normalization before terminal, structural read serialization, scalar fast-path preservation, and public API shape.
- Concurrency tests prove no worker-wait deadlock, no coordinated field/topology gap, no claim gap, stable attachment publication, callback topology rejection, and same-thread cross-context rejection.
- Reachability tests cover cycles, shared DAGs, duplicate occurrences, reparenting, reorder, rekey, deterministic release order, active parent and descendant writes, overlapping protected closures, and new support before the final protector leaves.
- Capture tests cover stale detached-component attachment, metadata, and terminal revisions.
- Derived tests cover coordinated reads, an uninstrumented-alias gate recheck, and a genuine orphan verdict without retry or lost-completion state.
- Callback tests cover required handler order, parents and reference counts before handlers, invocation outside framework locks, cross-thread overlap, exception draining, same-entity stale suppression, and detach-context availability for the detach operation's own callbacks.
- Admission tests cover one materialization, atomic duplicate and foreign rejection, one getter capture per attempt, publisher-once semantics, caller ordering, same-context callback admission, and detach behavior.
- Focused Core, Tracking, Generator, Dynamic, Registry, Connector, and Hosting suites pass before the full non-integration solution build, test, pack, public API, and repeated concurrency runs.
- Production callout and lock-order audits find no user code under framework locks. Independent correctness and deletion reviews approve the final diff.
- Benchmarks run only after the user reviews the local branch and approves the performance follow-up.

## Evidence

The reviewed PR and local deterministic tests establish the motivating failures: the whole-chain gate deadlocks with downstream worker waits; raw baselines re-enumerate mutable user values; remove-before-add creates a reparent claim gap; uncoordinated attach capture can publish stale topology; and unlocked callbacks require same-entity stale suppression. The relevant acceptance areas are `CrossContextGateDeadlockTests`, `TerminalStoreContractTests`, `NormalizingSetterDerivedRaceTests`, `ReconcileRetentionWindowTests`, `DownstreamWriteInterceptorReleaseTests`, `StructuralWriteLockOrderTests`, `OwnershipReservationProtocolTests`, `CallbackContractTests`, and `AddPropertiesLifecycleTests`.

The synchronization assumptions follow the platform contracts for [`System.Threading.Lock`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.lock), [`Monitor.Enter`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.monitor.enter), [`Volatile`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.volatile), [`Interlocked.CompareExchange`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.interlocked.compareexchange), and immutable collections. Framework locks protect only library-owned nonblocking work, and every reader-visible publication is immutable.
