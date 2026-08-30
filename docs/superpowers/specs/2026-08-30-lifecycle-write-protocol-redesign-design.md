# Lifecycle and Structural Write Protocol Redesign

**Status:** Proposed for pull request #494

**Reviewed head:** `331b701931cb9d92832523bb014c222bca072cc6`

**Base:** `0418410c2da2ca5aa39fb25fb9d5fda3b53f429b`

## Decision

Keep the product model introduced by pull request #494, including exact one-context ownership, anchor reachability, provisional constructor anchors, occurrence-aware parent edges, constructor mirroring, lifecycle-aware `AddProperties`, and assign-before-populate connector behavior. Replace the current lifecycle gate and raw-value reconciliation protocol with four smaller mechanisms:

1. A terminal-boundary coordinator that sees the final `PropertyWriteContext.NewValue` immediately before the backing store and the assigned terminal revision immediately after it.
2. Immutable structural snapshots that are captured from user values once and become the only committed outgoing-edge representation.
3. Nonblocking, context-specific attachment and ownership reservations that prevent cross-context claims and reparent gaps without waiting while arbitrary code runs.
4. A short per-context topology commit that invokes no interceptor, getter, enumerator, key equality, lifecycle handler, event handler, property callback, or public metadata delegate.

The current branch should not be patched by adding more conditions to `StructuralReconciler` or by moving the existing gate. The four remaining defects have one cause: user-controlled work and graph publication are interleaved while one context-wide reentrant lock is treated as a transaction. The replacement separates capture, terminal store, pure commit, and notification.

## Goals

- Preserve the intended PR #494 lifecycle model and consumer behavior unless it conflicts with correctness or deadlock freedom.
- Guarantee data-race-free per-subject attachment state and immutable lock-free parent snapshots.
- Guarantee quiescent consistency for nonfaulted properties: after structural writes and deferred releases settle, backing fields, committed outgoing snapshots, incoming parents, reference counts, anchors, registry membership, and exact contexts agree.
- Never expose a retained subject as unattached or claimable while one committed edge is being replaced by another edge in the same context.
- Never hold a framework lock while calling arbitrary interceptors, getters, enumerators, key equality, lifecycle callbacks, events, property callbacks, or metadata publishers. A generator-emitted raw field read and the raw terminal are the narrow exceptions and have the stricter trusted-access contract stated below.
- Preserve normalizing terminals that reorder, drop, or substitute proposed subjects, including the current uncontended hand-written substitution cases.
- Preserve scalar fast paths and measure structural overhead before finalizing.
- Delete obsolete phases and synchronization plumbing instead of layering a second protocol over the first.

## Non-goals and contract boundaries

- Direct in-place mutation of a mutable collection without assigning the property again is not an intercepted structural write. Such mutation remains unsupported. The framework snapshots what an intercepted assignment or attach observes.
- A structural getter or enumerable that mutates related topology indefinitely is a contract violation. Bounded capture retry detects instability and throws rather than looping forever.
- The raw terminal delegate passed by a manual subject must be an exception-free, nonblocking, non-reentrant store. It may normalize the assigned value, but it may not start or wait for another operation, invoke lifecycle APIs, or call the executor recursively. A generator-emitted raw reader and terminal satisfy this contract. Framework deadlock freedom for a manual subject is conditional on its terminal contract because the terminal runs under the per-subject `SyncRoot` that prevents torn backing-field access. The existing manual API also remains responsible for supplying a coherent `currentValue`; the generated entry captures and re-linearizes that value under `SyncRoot`.
- A normalizing terminal may store a subject that was not present in its final proposed component. Because that subject cannot be reserved before an opaque terminal reveals it, the framework captures the authoritative value and attempts its reservation after the store. An uncontended unattached or same-context substitute remains supported. If a foreign reservation wins first, the property enters sticky `Faulted` topology state; the framework cannot safely restore a backing field controlled by the opaque terminal.
- Concurrent context service configuration and attachment remains unsupported. Lifecycle and registry services must be configured before subjects attach.
- A property in `Faulted` topology state is deliberately not quiescent. Structural reads and graph-sensitive operations throw until a later successful terminal supersedes the fault or explicit detach clears it. When authoritative capture succeeded, the fault retains only same-context reservations required by the actual stored snapshot and releases proposal reservations for subjects known to have been dropped. If authoritative capture itself failed, it conservatively retains the proposal reservations because it cannot prove they are absent. A foreign subject introduced by a normalizer remains owned by its foreign context and keeps the property faulted.
- Dynamic metadata becomes executor-authoritative because the current public continuation cannot run under the topology gate. `SubjectPropertyRegistration` and its public signatures remain compatible; its publisher remains subject to its existing contract: it must be exception-free and assign the supplied lookup exactly once. Generated and `DynamicSubject` publishers satisfy that contract. The framework cannot repair an implementation-owned `IInterceptorSubject.Properties` projection if a third-party publisher violates it, although executor metadata and topology remain authoritative and the originating operation still reports the exception after draining its journal. Other public cleanup remains a separate decision after correctness is established.

## Evidence at the reviewed commit

The design is based on source inspection, deterministic tests, the live PR discussion, and three throwaway implementation spikes. The spikes were made in isolated clones and are not part of the PR.

| Evidence | Result | Design consequence |
| --- | --- | --- |
| [`InterceptorExecutor.SetStructuralPropertyValue`](https://github.com/RicoSuter/Namotion.Interceptor/blob/331b701931cb9d92832523bb014c222bca072cc6/src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs#L228-L325) enters the lifecycle gate before the write chain | A downstream interceptor can start a same-context worker write and wait while the worker blocks on the gate | No context gate may surround the interceptor chain |
| Terminal-boundary spike at `331b7019` | The deterministic worker-wait test failed after 20 seconds before the spike and passed in 21 ms with terminal-only coordination; 15 of 16 selected protocol tests passed | The terminal hook is viable, but its prototype must not keep the gate across the authoritative getter or reconciliation |
| [`ReentrantStructuralWriteTests.WhenAUserEnumerableWritesTheRootWhileTheAttachSeedsIt_ThenTheWritePassesThroughAndTheAttachCompletes`](https://github.com/RicoSuter/Namotion.Interceptor/blob/331b701931cb9d92832523bb014c222bca072cc6/src/Namotion.Interceptor.Tracking.Tests/Lifecycle/ReentrantStructuralWriteTests.cs#L147-L193) | The test accepts a completed attach whose field contains `lateChild` while the graph owns `seededChild` | Claimed-but-unpublished writes may not pass through; attachment capture must be versioned or the conflicting operation must fail before its terminal |
| [`OwnershipGraph`](https://github.com/RicoSuter/Namotion.Interceptor/blob/331b701931cb9d92832523bb014c222bca072cc6/src/Namotion.Interceptor.Tracking/Lifecycle/OwnershipGraph.cs#L28-L185) stores raw values as baselines | Release and reachability re-enumerate mutable user values after the assignment | Committed topology must store immutable occurrences, never raw values |
| Immutable-edge spike at `331b7019` | Tracking passed 669 of 669 and Registry passed 181 of 181 after replacing raw baselines with occurrence snapshots; the prototype deleted 139 net lines | Immutable snapshots are both viable and simpler than the current reconcile and release phases |
| [`StructuralReconciler`](https://github.com/RicoSuter/Namotion.Interceptor/blob/331b701931cb9d92832523bb014c222bca072cc6/src/Namotion.Interceptor.Tracking/Lifecycle/StructuralReconciler.cs#L104-L151) removes before it adds | Reparenting can detach and reattach a retained subtree; a competing context can claim it in the gap | Commit new support before removing old support and decide release from final reachability |
| [`SubjectOwnership.RemoveIncoming`](https://github.com/RicoSuter/Namotion.Interceptor/blob/331b701931cb9d92832523bb014c222bca072cc6/src/Namotion.Interceptor.Tracking/Lifecycle/SubjectOwnership.cs) compares occurrence keys while mutating ownership | User `Equals` can reenter and can create lock-order cycles | Edge identity must not depend on an index or dictionary key |
| Reservation spike at `331b7019` | Seven deterministic models passed for same-context sharing, foreign competition, stale attach capture, exclusive transitions, provisional promotion, detached structural setter coordination, and unlocked getter callout | Reservations are viable when reservation context is separate from committed attachment and no participant waits |
| [Grouped lifecycle/write-protocol review comment](https://github.com/RicoSuter/Namotion.Interceptor/pull/494#discussion_r3884461954) | Four previously separate findings identify one protocol boundary problem | Implement and review this as one redesign, not four local fixes |

The synchronization assumptions also follow the platform contract. `System.Threading.Lock` and `Monitor` are blocking and reentrant, so opposite acquisition order can deadlock. `Volatile` only constrains ordering around the volatile access and does not turn a mutable object graph into one atomic value. `Interlocked.CompareExchange` provides the atomic reference transition needed for executor state and reservation publication. Immutable collections are thread-safe after publication, while ordinary mutable collection enumeration is not safe against concurrent mutation. See [Lock](https://learn.microsoft.com/en-us/dotnet/api/system.threading.lock), [Monitor.Enter](https://learn.microsoft.com/en-us/dotnet/api/system.threading.monitor.enter), [Volatile](https://learn.microsoft.com/en-us/dotnet/api/system.threading.volatile), [Interlocked.CompareExchange](https://learn.microsoft.com/en-us/dotnet/api/system.threading.interlocked.compareexchange), [ImmutableArray](https://learn.microsoft.com/en-us/dotnet/api/system.collections.immutable.immutablearray), and [Dictionary thread safety](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2#thread-safety).

## Product semantics to preserve

### Exact ownership and anchors

- A subject is unattached or attached to exactly one built-in context.
- Direct movement from one non-null context to another is illegal.
- Explicit anchors survive until explicit detach.
- Context constructors create provisional anchors.
- A provisional anchor is consumed only by independent support from an anchored ancestor. Self-edges and back-edges do not consume it.
- Detach clears the explicit anchor and releases only the subjects no longer reachable from any anchor.
- An attached root can have reference count zero. Reference count is never an attachment predicate.
- The exact context remains available during detach callbacks.

### Structural topology

- Every occurrence is one incoming edge. Duplicate list entries and dictionary entries have independent reference counts.
- Retention matches child identity and occurrence ordinal, not the current collection index or dictionary key.
- Reorder and rekey refresh the published index without false detach and attach transitions.
- Closed cycles release when no anchor reaches them. Shared DAGs survive while any anchored path remains.
- Getter-only derived projections do not own their result. Generated or dynamic derived backing stores with a real setter can own their stored result.

### Interception and terminal behavior

- Every interceptor sees the chain once. The protocol never replays arbitrary interceptor side effects.
- Vetoed writes perform no terminal or topology work.
- `On<Property>Changing` still runs before the executor entry and generated terminals remain faithful backing-field stores.
- A generated structural write captures `CurrentValue` through its trusted raw reader under `SyncRoot` before the chain and re-linearizes it immediately before the terminal store. With overlapping writes, interceptor code before `next` can observe the earlier entry snapshot while unwind and property-change publication observe the exact value replaced by that terminal revision.
- A normalizing terminal may reorder or drop subjects from its final proposed component.
- A hand-written normalizing terminal may substitute an unattached subject. Its authoritative post-terminal snapshot is reserved and committed when uncontended; an introduced foreign subject produces the documented sticky topology fault.
- Property change and derived interceptors unwind only after the topology for that terminal revision is committed or a lifecycle fault is recorded.

### Lifecycle callbacks and consumers

- Attach handlers before the `LifecycleInterceptor` ordering seam observe a subtree top-down. Handlers after the seam observe it bottom-up.
- Detach handlers on both sides observe a subtree top-down.
- Incoming parents and reference count are published before handlers observe an edge change.
- `SubjectAttached` occurs once on first ownership and `SubjectDetaching` occurs once on final release.
- Attach property callbacks receive the property set captured for that attach. Callback-added properties are admitted by their own call and are not attached twice.
- `AddProperties` materializes input once, rejects duplicate names atomically, captures each structural getter once per attempt, publishes metadata once, and invokes callbacks in caller order.
- Registry remains a projection of lifecycle truth. Connectors continue to assign structural state before populating surviving children.

## Necessary behavior changes

The following observations cannot be preserved together with deadlock freedom and continuous ownership. They are intentional changes, not incidental test updates.

| Area | PR #494 at `331b7019` | Revised contract | Why |
| --- | --- | --- | --- |
| Ordinary downstream interceptor writes into another context | Rejected because the outer chain already holds one topology gate | Allowed when ownership reservations do not conflict | With no gate around arbitrary interceptors there is no lock-order reason to reject the operation |
| Structural write racing explicit attach or detach on the same subject | Waits and orders through the gate and attachment monitor | Never waits; one operation obtains an exclusive transition or shared structural lease, and the loser receives a prompt retryable conflict before its terminal | Waiting while arbitrary code can wait for the competing operation recreates self-deadlock |
| Parent structural lease racing final topology publication | Orders implicitly under the whole-chain gate | Linearizes before the topology freeze and protects the attachment, or receives a prompt retryable conflict before its interceptor chain | Publication cannot validate zero protectors and then clear an attachment after a lease has returned |
| Proposed-child reservation racing final topology publication | Orders implicitly under the whole-chain gate | Linearizes before the topology freeze and protects the child, or receives a prompt retryable conflict before the terminal store; downstream interceptors may already have run once | Reservation happens at the final proposal boundary, and the chain is never replayed |
| `CurrentValue` during overlapping generated writes | The whole-chain gate serializes capture and terminal | Entry-side interceptors can observe the coherent entry snapshot; the terminal re-linearizes `CurrentValue` under `SyncRoot`, so unwind and published change payloads contain the actual predecessor | Replaying entry-side interceptors to update their earlier observation would duplicate arbitrary side effects |
| Write during attach seeding | May pass through and leave the graph seeded from a stale value | Cannot pass through untracked; it either commits through the structural lease before capture or fails before its terminal while attach owns the exclusive transition | A completed stale seed violates quiescent consistency |
| Reparent of an already owned target | Can emit detach and attach for the retained target and descendants because removal precedes addition | Retained targets and descendants remain continuously attached; only actual edge and index changes are reported | Synthetic detach and attach events would assert a state that never safely exists |
| Lifecycle callbacks under concurrent commits | Serialized because the topology gate is held while callbacks run | Synchronous for their originating operation, invoked outside framework locks, and allowed to overlap across threads; ordering is guaranteed only inside one topology revision | Serializing arbitrary callbacks makes worker-wait deadlocks unavoidable |
| Callback live-state view | Exact intermediate state produced by recursive mutation | Each individual attachment and parent read is immutable and coherent, but a sequence of reads can span revisions; the payload describes the exact committed delta and property projection | A per-subject publication cannot make a whole multi-subject graph atomic without a context-wide copy |
| Callback exception | Propagates while the gate and partial recursive phase unwind | An ordinary journal aggregates and propagates after commit; a deferred-release journal records and reports a callback fault without throwing from lease disposal | Graph correctness and exception unwinding cannot depend on callback success |
| Mutable collection after assignment | Raw baseline is re-enumerated later | Only the immutable occurrence snapshot captured by the intercepted assignment is authoritative | Direct collection mutation is not an intercepted write and mutable enumeration is not thread-safe |
| Cross-thread lifecycle event order | Implicitly serialized by the gate | Changes carry context, subject, property, and stable-edge revisions as applicable; built-in consumers replace per-property projections and reject stale work only for the same entity | A context-global watermark would incorrectly discard older changes for unrelated subjects |
| Topology work from a lifecycle callback | Structural writes and explicit attach or detach are rejected on the callback thread | Allowed after the outer commit, subject to the same nonblocking lease and reservation conflicts as any other operation | Once callbacks hold no framework lock, the thread-static ban is no longer required for correctness |
| Detach deferred by an active descendant write | The removing operation blocks behind the context gate or creates an unsafe release window | The removing operation commits `ReleasePending` and returns; final lease disposal starts a new revision that owns the detach journal | The removing operation may not wait for arbitrary code that can be waiting for it |

## Compatibility summary

Compared with master, this redesign deliberately keeps the PR's product changes: one exact context instead of plural fallback contexts, executor and context separation, anchor-based ownership, occurrence-aware edges, cycle release, constructor mirroring, lifecycle-owned parent projection, and lifecycle admission for dynamic properties. It does not attempt to restore master's subject-local services, fallback composition, split `ContextInheritanceHandler` and `ParentTrackingHandler`, collapsed repeated occurrences, or recursive last-reference release. Those are the feature decisions of PR #494, not consequences of this synchronization redesign.

Compared with PR head `331b7019`, the intended ownership and consumer model remains. The behavior not preserved is limited to the rows above: operations that previously waited or were rejected because a whole-chain gate happened to be held now use explicit nonblocking conflicts, callbacks may overlap, stale attach seeding is forbidden, direct mutable collection edits do not rewrite committed topology, and retained reparent targets no longer emit false context transitions. Uncontended authoritative normalizers still support reorder, drop, and unattached substitution. A terminal that reveals a foreign or concurrently lost substitute after its irreversible store produces sticky fault state rather than a silently inconsistent graph.

## Rejected alternatives

### Patch the existing gate protocol

Adding stale-baseline checks, more claim bookkeeping, or another reentrancy guard does not remove the gate from arbitrary interceptors, getters, enumerators, equality, and callbacks. Every new callback path can recreate the same worker-wait or cross-context lock cycle. This also leaves raw-value re-enumeration and removal-before-add publication intact.

### Move the gate to the terminal but keep reconciliation inside it

The terminal spike proved that this removes the downstream-interceptor deadlock, but an authoritative getter, custom enumerable, key comparison, or lifecycle callback can still start a worker that needs the same gate and wait for it. The terminal hook is retained, while its lock scope is not.

### Serialize all lifecycle work on an actor or callback dispatcher

A dispatcher would avoid shared-state mutation but would change every synchronous setter and callback into a potentially asynchronous operation. A callback that waits for a worker whose notification is queued behind it also needs special helping rules. The proposed short commit plus revisioned synchronous callbacks is smaller and preserves uncontended synchronous behavior.

### Narrow support to generated faithful terminals

This is the smallest protocol because the pre-terminal snapshot is automatically authoritative. It would remove support for hand-written and dynamic normalizing stores, which PR #494 intentionally tests. The recommended design uses a fast faithful-terminal path and retains an authoritative post-terminal capture for other terminals.

### Publish one immutable snapshot of the entire context graph

One atomic graph root would give the cleanest reader model, but copying a context-wide dictionary for each property write scales with the full graph and allocates heavily. Per-property and per-subject immutable snapshots give data-race freedom and quiescent consistency without whole-graph copying. Internal operations that require a stable multi-subject view use a context publication sequence and retry when a writer was active; public one-subject reads remain lock-free and do not promise a context-wide snapshot.

## Detailed design

### 1. Executor attachment state and nonblocking leases

`InterceptorExecutor` continues to publish one immutable attachment-state object. Extend it with a transition phase and counters that are modified only under the executor's private attachment monitor:

```csharp
internal sealed class AttachmentState
{
    internal InterceptorSubjectContext? Context;
    internal SubjectAttachmentAnchorKind Anchor;
    internal long AttachmentRevision;
    internal long AttachmentOrdinal;
    internal AttachmentPhase Phase;
    internal int StructuralLeaseCount;
    internal OwnershipReservation? Reservation;
    internal long TopologyFreezeRevision;
    internal long? PendingReleaseGroupId;
    internal bool DetachCompleted;
}
```

The published object remains immutable. The sketch shows fields conceptually, not mutable implementation fields. A transition always publishes a new state reference.

Structural setters acquire a shared structural lease before resolving and executing the interceptor chain. The lease pins the attachment context and chain state without holding a lock. Concurrent structural writes can share the lease. Explicit attach, detach, and anchor promotion require an exclusive transition. An exclusive transition never waits for a structural lease, and a structural lease never waits for an exclusive transition. The operation that loses returns a specific retryable lifecycle-conflict exception before its terminal executes.

Each lease has an executor-local identity and idempotent disposal. Executors retain active identities in private state under their attachment monitors while the published attachment state needs only the count. A lease already active when a pending-release group forms becomes one of its protectors. A later same-context lease on a `ReleasePending` subject uses a slow path in topology-gate-then-executor-monitor order, revalidates the pending group, and joins it before returning. It never increments under the monitor and then requests the gate. If finalization wins first, acquisition retries from the newly published attachment state before any interceptor runs. A foreign or exclusive transition still fails promptly. If new support cancels pending release, existing protectors become ordinary attachment leases and their later disposal cannot reopen the completed group.

This changes concurrent attach and detach from blocking order to prompt conflict. It avoids replaying interceptor side effects and makes it impossible for an attachment epoch to change between structural routing and terminal finalization.

Generated structural getters and setters must not use `_executor is null` as a direct-access fast path. Both create or reuse the executor, so even the first detached structural access takes `SyncRoot`, setters consume a terminal revision, and attach capture cannot tear a wide value. Generated scalar getters and setters retain the current direct fast path.

### 2. Context-specific ownership reservations

An ownership reservation is separate from committed `AttachedContext`. Reservation is not ownership and `TryGetContext()` must not report it.

Each operation owns a token. A subject has at most one reservation group:

```text
context identity + reservation mode + participant count + generation
```

- Write and property-admission reservations are shareable by operations for the same exact context.
- Each participant releases only its token. One participant cannot clear another participant's reservation.
- A foreign-context reservation fails immediately before the terminal.
- Explicit attach uses an exclusive reservation for its prospective component. Another operation fails promptly instead of waiting.
- Committing ownership converts the reservation into ordinary attachment. Remaining same-context tokens release as no-ops against the committed context.
- Releasing the last unused token triggers a short reachability check over committed snapshots and hands back subjects that have no committed support.

The terminal preparation reserves every proposed occurrence and every newly discovered descendant. Reserving already-owned same-context subjects prevents another concurrent commit from handing them to no context before this transaction adds its support. This is the concurrency form of add-before-remove.

A transaction that would release a closure protected by an active structural lease or same-context ownership reservation does not wait and does not clear any member's context. It removes obsolete incoming support, creates or joins the context-owned pending-release group described below, retains the whole currently unreachable outgoing closure's ownership snapshots and same-context reservations, and omits those members' detach journals. A descendant that remains reachable from another anchor is excluded. Final protector disposal runs a short final-reachability transaction that releases members still unreachable or cancels release for members with new support. This permits the removing operation to finish while preserving the pinned attachment epoch, every reserved future support, and every committed descendant edge below it.

### 3. Immutable structural snapshots

`OwnershipGraph` stores `StructuralSnapshot`, never the raw assigned object:

```csharp
internal readonly record struct StructuralOccurrence(
    IInterceptorSubject Subject,
    int SubjectOrdinal,
    object? Index);

internal sealed record StructuralSnapshot(
    long SourceRevision,
    ImmutableArray<StructuralOccurrence> Occurrences);
```

`SubjectOrdinal` is the zero-based occurrence number of that same child identity within one property snapshot. The stable edge identity is:

```text
parent subject identity + property name + child subject identity + subject ordinal
```

`Index` is publication payload only. It can be a collection position or dictionary key, and it is never used for edge identity, dictionary lookup, or equality while graph state is locked. Rekey and reorder retain the edge and update only its payload.

`StructuralSnapshotBuilder` is the only component that interprets values. It captures direct subjects, collection ordinals, generic and non-generic dictionaries, and supported read-only dictionary shapes. It invokes all user enumeration outside framework locks and produces an immutable array reused by commit, reachability, release, parent publication, and index refresh.

The same data is published to projections as an immutable per-property snapshot containing property revision, child identity, child ordinal, and index payload. Registry atomically replaces a property's projection from this snapshot. It never re-enumerates the live value and never compares a user dictionary key while holding its lock. The raw-value `RefreshCollectionProperty(PropertyReference, object?)` callback is removed.

Attach and property admission capture each structural getter once per attempt, record the property's terminal revision before accepting the capture, and retry only the capture when a coordinated setter changes that revision. They never rerun the caller's interceptor chain or re-enumerate the `AddProperties` input sequence.

### 4. Terminal-boundary coordination

Core defines an internal terminal coordinator interface available to Tracking through `InternalsVisibleTo`. `LifecycleInterceptor` remains an `IWriteInterceptor` so ordering attributes keep their meaning, but its write method installs the coordinator on the by-ref `PropertyWriteContext` and forwards once. The raw terminal invokes the coordinator at the only point where every downstream interceptor has finalized `context.NewValue`.

The structural write sequence is:

1. Acquire the parent executor's shared structural lease and pin the exact context and context state.
2. Run ordinary interceptors without a lifecycle gate or attachment monitor.
3. Immediately before the raw terminal, capture the final proposed component and acquire same-context ownership reservation tokens. Failure occurs before the backing store.
4. Allocate a `PendingStructuralWrite` in `Preparing` state. Every proposal reservation participant links to that descriptor. The generated faithful path needs no context-wide entry because every value it can expose already has an exact reservation link. Before an untrusted manual terminal runs, publish its descriptor in the context's immutable pending-terminal registry to cover a substitute that cannot be known or reserved yet. Do not yet replace the property's current descriptor slot.
5. Under the parent `SyncRoot`, assign the next per-subject terminal revision, publish this descriptor as the property's current slot, and advance it to `Storing`. For the generated entry, invoke the trusted raw reader and update `PropertyWriteContext.CurrentValue` to the exact value this terminal will replace. Invoke the trusted raw terminal once, update origin, timestamp, and write state, then advance the descriptor to `Pending`. The slot publication and revision assignment are the same linearization point, so a descriptor prepared earlier but stored later correctly supersedes one that prepared later but stored first.
6. Release `SyncRoot`.
7. For a generator-marked faithful terminal, reuse the pre-terminal snapshot. For a manual normalizing terminal, capture the authoritative getter output outside every framework lock.
8. Reuse proposal reservations and acquire post-terminal same-context reservations for actual subjects introduced by a normalizer. Reorder, subset, and an uncontended unattached or same-context substitute are legal. If an actual subject is foreign or wins a competing foreign reservation, record a sticky property fault rather than publishing inconsistent topology.
9. Enter the short context topology gate, verify the parent lease, terminal revision, property pending descriptor, capture revisions, metadata generation, and reservation generations, then commit pure topology or sticky fault state. As the last pure publication steps, mark the descriptor terminal, remove its optional untrusted-terminal registry entry, and detach its registered continuation list without invoking it.
10. Release the topology gate, release reservation tokens and the parent structural lease, append any journal produced by final-protector deferred release after the current journal, invoke the journals outside framework locks in their local topology-revision order, and run the detached descriptor continuations exactly once.
11. Resume interceptor unwinding. Derived and property-change interceptors now observe committed topology or the fault.

The per-property pending descriptor prevents stale completion:

- If write B reaches the terminal after write A, B's larger terminal revision replaces A as pending.
- A discovers that it is stale and releases its reservations without publishing topology.
- B is responsible for committing the actual latest snapshot.
- Reentrant writes from a getter follow the same rule. The outer capture cannot overwrite the newer topology.
- A latest terminal finalization that cannot complete records a sticky lifecycle fault on the property. Structural getters, parent and reference-count queries, Registry access, `AddProperties`, and explicit attach surface that fault. A structural setter is the recovery entry: it may execute one new terminal, and a successful topology commit supersedes and clears the fault. The protocol never silently treats an old snapshot as current.

No retry repeats the interceptor chain. Bounded retry is limited to capture and version validation around the value that the terminal already stored.

The faithful marker is an explicit generator-emitted argument on a generated-code executor entry. Metadata classification never infers it. The existing public manual `SetPropertyValue` entry remains source and binary compatible and always selects the untrusted authoritative-capture path. The generator-only entry is public solely because generated code compiles in consumer assemblies; it is hidden from normal discovery and does not let existing manual calls silently opt into a stronger contract.

`PendingStructuralWrite` has the state machine `Preparing -> Storing -> Pending -> Committed | Superseded | Faulted`, with `Storing -> Superseded | Faulted` on terminal failure. Its `RegisterOrRun` completion operation prevents a derived retry from being lost between a state check and registration. Transitioning to a terminal state atomically detaches the already-registered continuation list; lifecycle code invokes that list only after releasing the topology gate. A registration that observes a terminal state runs on its registering thread, which holds no lifecycle lock. A derived orphan observation first reads exact pending descriptors from the observed subject's same-context reservation participants. Only when no exact participant explains the subject does it snapshot the context's immutable untrusted-terminal registry and register an all-completed continuation against that exact descriptor set. The registry is not an in-flight count and not a request to wait for context quiescence. An untrusted descriptor is registered there before its terminal may expose an unknown substitute, so an operation that starts later cannot explain the value already observed. The generated faithful path pays no context-registry publication cost. If a manual terminal violates its exception-free contract, Core retains the original exception, releases `SyncRoot`, and asks the coordinator to terminalize the descriptor before rethrowing. A descriptor already replaced by a later terminal becomes `Superseded`; otherwise lifecycle performs best-effort authoritative capture outside locks and publishes sticky `Faulted` state, retaining proposals only when capture cannot prove what was stored. Both paths unlink every reservation participant, remove the optional registry entry, release unneeded reservations, and dispatch continuations outside locks. A later successful terminal may replace `Faulted`; after its topology commit it clears the fault and releases the fault's reservations. Explicit detach also clears the fault and reservations. Structural getters, parent projection, reference count, registry access, and explicit attach surface the fault until one of those recovery paths completes.

### 5. Pure topology transaction

The context topology gate protects only in-memory graph staging and publication. Code under it may allocate or manipulate library collections, but may not call through any delegate or virtual user surface. Every allocation and fallible validation required for publication completes before the context publication sequence becomes odd. Installing a freeze retains the exact prior attachment-state reference. Every abort path restores those references in `finally`; every success path has already allocated its unfrozen final states. The odd section therefore contains only validated nonthrowing state swaps, and an ordinary exception leaves the old even publication intact rather than blessing a partial graph or stranding a freeze.

For one property revision, `TopologyTransaction`:

1. Replaces the property's committed outgoing snapshot in staged state.
2. Matches retained edge identities by child reference and subject ordinal.
3. Adds every new incoming edge and attaches newly reachable reserved subjects.
4. Resolves provisional anchors from the final staged graph by strongly connected components.
5. Computes forward reachability from the remaining explicit and provisional anchors.
6. Removes old incoming edges.
7. Finds unreachable components without protectors and the full unreachable closures protected by active structural leases or ownership reservations.
8. Installs a transaction-specific topology freeze on every executor whose attachment state may change, one executor monitor at a time in topology-gate-then-monitor order, then revalidates all lease identities, reservation identities, attachment revisions, and graph revisions used by staging. An acquisition that encounters a freeze fails promptly before returning a token. Parent lease acquisition occurs before the interceptor chain; proposed-child reservation occurs after downstream interceptors but before the terminal store. Validation failure clears every installed freeze and retries staging without replaying the interceptor chain.
9. Fully stages pending-release group merges, immutable parent snapshots, reference counts, attachment states, notification holds, entity revisions, and the journal.
10. Publishes the staged state inside one context publication sequence and clears every freeze before releasing the topology gate.

New support is staged before old support is removed. Release is a final-reachability decision, not a recursive side effect of an individual edge removal. This preserves cycles, shared DAGs, already-owned reparent targets, leased descendants, and all descendants that the final graph still reaches.

`ReleasePending` is represented by a context-owned pending-release group, not a boolean on only the leased root. The group contains the entire currently unreachable outgoing closure, retains each member's context, snapshots, and same-context reservations, and contains the exact structural-lease and ownership-reservation token identities that protect any member. If two pending closures overlap, the transaction merges their groups and protector sets. Token disposal removes only its own protector after dropping the executor monitor, and the group is finalized only after its last protector exits. The final-protector transaction recomputes reachability, releases members that are still unreachable and unprotected, preserves independently reachable members, and clears the group. A new same-context lease or reservation on a pending member joins the group through the same revalidated topology slow path. New support may therefore cancel all or part of a pending release without a foreign-claim window.

Every attachment epoch receives a context-wide unique `AttachmentOrdinal` from one monotonic counter when the context is first published on that subject. Concurrent admissions linearize at that allocation; reattachment receives a new ordinal. Provisional-anchor resolution first consumes every provisional subject reachable from an explicit anchor. For the remainder, build reachability between provisional subjects through the final graph, condense it into strongly connected components, and retain the subject with the lowest `AttachmentOrdinal` from each source component. Consume every other provisional anchor. Source components are the minimal independent roots of the remaining provisional graph, so `B -> A` retains B regardless of local generation values, a provisional cycle retains exactly one representative, and self or back edges cannot consume the last root. The result depends only on the final graph and the stable total order of current attachment epochs, not recursive traversal or callback order.

Writers bracket sequential per-subject publication with an odd/even context publication sequence. Parent and reference-count readers load one immutable per-subject snapshot and never take the topology gate. Internal multi-subject graph walks read the even sequence before and after their walk and retry if it changed or was odd. The public API guarantees per-subject coherence and quiescent whole-graph agreement, not an atomic context-wide snapshot during a concurrent commit.

The only nested framework-lock order is context topology gate before an executor attachment monitor during freeze, validation, publication, or notification-hold completion. Lease and reservation acquisition on ordinary attached state uses only the executor monitor. Acquisition on frozen state fails promptly; acquisition on `ReleasePending` uses the revalidated topology slow path. Token disposal uses the monitor-only fast path only when the subject is neither frozen nor pending release. Otherwise it leaves state unchanged, drops the monitor, and enters the topology-gate-then-monitor slow path; last-protector disposal may initiate deferred release there. Notification-hold completion also enters the topology gate before its executor monitor and performs only the pure decrement and optional final context clear, so it cannot overwrite or be overwritten by a staged attachment publication. No path waits for the topology gate while retaining an executor monitor, and no topology transaction holds more than one executor monitor at a time.

### 6. Notifications and callback concurrency

The notification journal preserves ordering inside one topology revision:

- Actual releases are reported before actual additions for a direct replacement, preserving `old child detached` before `new child attached`.
- A retained reparent target produces edge changes and index refresh only. It does not produce context detach or attach events for itself or descendants.
- Attach traversal remains top-down before the lifecycle ordering seam and bottom-up after it.
- Detach traversal remains top-down on both sides.
- Parents, reference count, ownership, and the topology revision are committed before the first callback.

Callbacks run synchronously for their originating operation but outside all framework locks. Callbacks from different threads can overlap. Change payloads carry the context topology revision and the subject, property, or stable-edge revision required to order changes to the same entity. Built-in Registry and connector projections apply revision checks under their existing synchronization. Registry replaces a property projection from the immutable property snapshot instead of replaying index-keyed deltas.

A callback receives an exact immutable delta and projection for its journal revision. Each live attachment or parent read is individually coherent, but multiple live reads can span the journal revision and later commits. A callback that needs the exact transition uses its payload. An internal consumer that needs a stable multi-subject walk uses the context publication-sequence retry.

The executor retains the exact context through detach callback completion by publishing a detaching phase and an active-notification count. Every hold required by a newly committed journal is included in the same staged attachment-state publication as that journal, before the topology gate is released. There is no post-commit gap in which a later detach can clear context before an earlier journal obtains its hold. The detach journal's final entry publishes a `DetachCompleted` marker for the exact attachment epoch when it releases its hold. Any later hold completion, including an older attach or property journal that happens to finish last, clears context when it observes that marker and reduces the epoch's count to zero. Hold completion takes the short topology gate before the subject monitor, preventing its immutable attachment-state decrement from racing a transaction's staged replacement. Cross-context attachment cannot claim a detaching subject. No writer waits for the count.

Topology work from callbacks is allowed after the outer commit and goes through ordinary leases and reservations. A nested operation can fail promptly on an exclusive transition conflict, but no thread-static callback rule rejects it merely because it is nested. `CallbackReentrancyGuard` is deleted. Unbounded callback recursion remains application code and is not hidden by the framework.

Every journal entry owns a subject notification hold installed by its topology publication and releases it in `finally`. The notifier continues the remaining handlers and journal entries after a callback exception so no subject is stranded in `Detaching`. An ordinary journal collects exceptions and throws the single exception or an `AggregateException` to its originating operation after the journal completes. A deferred-release journal is a distinct operation with its own topology revision, initiated by the final protecting token's disposal. It collects callback failures, releases every notification hold, and only then traces each failure with the context, revision, subject, and exception. Trace-listener failures are caught and cannot escape token disposal or replace an exception already unwinding. Topology is already committed and is not rolled back. Lifecycle handlers remain contractually exception-free and thread-safe. This is the one callback-exception case with no safe originating caller to receive the exception, and it does not justify a new public diagnostic API.

### 7. Explicit attach and detach

Attach performs all user work before the topology gate:

1. Acquire exclusive reservations for the root and its captured unattached component.
2. Capture all structural property snapshots outside framework locks.
3. Validate attachment and property revisions.
4. Under the short topology gate, publish the anchor, ownership, outgoing snapshots, incoming edges, parent snapshots, and journal as one pure transaction.
5. Invoke attach callbacks after the gate.

If a coordinated structural write or another explicit transition already owns a conflicting lease, attach fails promptly before publishing. Capture staleness retries capture only. No attach may complete from a stale seed.

Detach acquires the subject's exclusive transition, clears the explicit anchor under the topology gate, computes final reachability from committed snapshots, publishes releases and a detaching phase, then invokes callbacks outside the gate. The detach journal publishes `DetachCompleted` after its callbacks finish, and exact context clears only when every notification hold for that attachment epoch has finished. A subject retained by another anchored path remains attached and produces no context-detach transition.

### 8. `AddProperties`

`AddProperties` uses the same prepare, reserve, commit, notify structure:

1. Materialize and duplicate-check the input exactly once outside the topology gate.
2. Capture each qualifying structural getter once per attempt outside the gate.
3. Reserve captured components.
4. Under the short gate, revalidate attachment, metadata generation, names, capture revisions, and reservations; publish executor-owned immutable metadata state and initial immutable property snapshots.
5. Produce and invoke property and lifecycle journals outside the gate in caller input order.

Dynamic metadata gains immutable executor-owned authoritative state while the generated and `DynamicSubject` fields remain compatibility projections. Before the first admission, the executor captures the subject's existing static or constructor-supplied metadata outside framework locks. Concurrent batches revalidate and merge against the executor snapshot and metadata generation under the short gate, never against a stale projection field. Generated `IInterceptorSubject.Properties` and `DynamicSubject` keep their existing seed and callback-published overlay behavior, so this redesign does not require a new public executor accessor.

`SubjectPropertyRegistration` and the existing `IInterceptorExecutor.AddProperties` and `ILifecycleInterceptor.AddProperties` signatures remain. After the topology gate publishes executor metadata and initial snapshots, the admission journal first invokes the registration's legacy publisher outside every framework lock, then continues property and lifecycle notifications in caller order. A publisher exception cannot roll back authoritative metadata or strand notification holds: the notifier records it, continues the journal, and includes it in the originating operation's final aggregate. Generated and `DynamicSubject` publishers satisfy the exception-free projection contract, so their compatibility fields may lag executor-authoritative metadata only during that post-commit call. A third-party publisher that violates the contract can leave its implementation-owned `IInterceptorSubject.Properties` projection stale; the operation throws after draining the journal, and the strong projection guarantee does not apply to that invalid implementation. A detached subject publishes metadata only. A detaching subject may admit metadata only, matching the current detach-callback behavior, and may not publish new ownership edges.

### 9. Derived-property validation

The terminal coordinator narrows the field-visible but topology-pending window to the work between raw terminal and pure commit. A pending topology descriptor is published atomically with the terminal revision, so a concurrent derived evaluation can distinguish `temporarily pending` from `unowned`.

A derived evaluation that encounters a subject reserved or pending for its context calls the descriptor's atomic `RegisterOrRun` operation. Completion runs each registered retry exactly once after topology commit and before outer derived and property-change interceptors publish. A genuine orphan becomes a sticky derived lifecycle fault and is surfaced to a later caller instead of being trace-only. The current context-wide `_transactionsInFlight` gate count and best-effort withheld list are removed.

### 10. API and code reduction

Required API changes are limited to the protocol:

- Remove `EnterStructuralWriteGate` and `ExitStructuralWriteGate` from `ILifecycleInterceptor`.
- Add an internal Core terminal-coordinator seam and internal executor lease and reservation operations.
- Add a generated-code executor entry for the explicit faithful-terminal marker while preserving the existing manual `SetPropertyValue` signature and behavior.
- Add context, subject, property, and stable-edge revisions plus immutable property projections to lifecycle change payloads where applicable.
- Add a specific retryable lifecycle-conflict exception or error code for exclusive transition conflicts.
- Move authoritative dynamic metadata state into `InterceptorExecutor` while preserving the generated and dynamic projection fields, `SubjectPropertyRegistration`, and both existing admission signatures with post-commit publisher semantics.

Keep public explicit attach, detach, lifecycle events, and handler interfaces during the correctness rewrite. Do not internalize other APIs unless the protocol requires it.

The target decomposition is:

- `StructuralSnapshotBuilder`: user value to immutable occurrences, no graph mutation.
- `OwnershipGraph`: committed snapshots, anchors, immutable parent publications, no user value interpretation.
- `TopologyTransaction`: pure staged graph delta and final reachability under the short gate.
- `OwnershipReservation`: executor-local nonblocking claims and tokens.
- `LifecycleNotifier`: journal construction and callbacks after commit.
- `PropertyAdmission`: `AddProperties` preparation around the same transaction primitive.
- `LifecycleInterceptor`: routing and orchestration only.

`AttachTraversal`, `ReleaseTraversal`, `ReachabilityWalk`, `StructuralReconciler`, and `CallbackReentrancyGuard` are deleted once their tests pass through the replacement protocol. `LifecycleScratch` is reduced to capture and transaction builders that still demonstrate an allocation benefit. `SubjectPropertyRegistration` remains as the compatibility request envelope, but no longer owns authoritative metadata or permits a publisher call under the topology gate.

The immutable-edge spike reduced its touched production and test diff by 139 net lines while passing the complete Tracking and Registry projects. The full redesign has additional terminal and reservation code, so the acceptance gate is not an arbitrary total line target. The relevant simplification test is whether every remaining phase owns one invariant and whether the old raw-baseline, recursive-release, and whole-chain-gate paths are completely deleted.

## Correctness invariants

The implementation is not complete until each invariant has a deterministic test.

1. **Exact attachment:** one subject reports zero or one exact context from one atomically published attachment state.
2. **No torn structural access:** every generated structural getter and setter, including the first detached access, serializes raw backing-field access through its executor terminal lock. Each successful generated terminal revision publishes the exact predecessor value it replaced, even when entry-side interceptors observed an earlier coherent snapshot.
3. **No arbitrary code under framework locks:** interceptors, getters, enumerators, equality, callbacks, events, property handlers, and metadata publishers run without the attachment monitor, topology gate, or terminal lock held by lifecycle code. Only a trusted nonblocking and non-reentrant raw store runs under `SyncRoot`.
4. **Pre-terminal foreign rejection:** every subject in the final proposed component is reserved for the target context before the raw terminal.
5. **Latest terminal wins:** only the current pending terminal revision can publish a property snapshot.
6. **Snapshot authority:** every committed outgoing edge comes from one immutable snapshot; release and reachability never touch the raw value.
7. **Continuous same-context ownership:** a same-context reservation, active structural lease, pending release, or final reachable edge prevents a subject from becoming unattached or foreign-claimable. Topology freeze closes acquisition before final validation, so an acquisition cannot return across publication based on stale attachment state.
8. **Occurrence identity:** duplicate count is exact, and index or key changes never invoke user equality to identify an edge.
9. **Final reachability:** after structural leases, reservations, and pending releases settle, the owned set equals forward reachability from anchors through committed snapshots. During `ReleasePending`, every temporarily unreachable owned subject belongs to a fully retained and reserved pending-release group; overlapping closures share or merge lease and reservation protector sets and cannot be released until every protector exits.
10. **Publication consistency:** each per-subject snapshot is immutable; internal multi-subject walks validate the context publication sequence; nonfaulted state converges to one final graph.
11. **Callback state:** callback payloads carry entity revisions and immutable property projections; every notification hold is published atomically with its journal; exact context remains available throughout overlapping callbacks; callback exceptions cannot strand notification holds.
12. **No silent dirty property:** a terminal revision is committed, superseded by a newer revision, or represented by a recoverable sticky lifecycle fault with its subjects still reserved.
13. **No chain replay:** attachment or capture conflicts never execute arbitrary interceptors twice.

## Verification strategy

The implementation plan starts by converting characterization tests that currently bless stale or intermediate behavior into failing correctness tests. It then lands the terminal seam, immutable snapshots, reservations, pure topology commit, notification revisions, attach and detach, `AddProperties`, and derived handling in independently reviewable commits.

Every concurrency test uses `ManualResetEventSlim`, `Barrier`, `CountdownEvent`, or `AsyncTestHelpers.WaitUntilAsync`. No committed test uses `Thread.Sleep` or `Task.Delay`. Bounded joins are assertions that report a probable deadlock instead of hanging the suite.

The final gates are:

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
dotnet pack src/Namotion.Interceptor.slnx
```

Public API snapshots must be reviewed, not blindly accepted. The benchmark comparison is agreed before it is run and follows `docs/benchmarking.md`, with allocation results evaluated before CPU time.
