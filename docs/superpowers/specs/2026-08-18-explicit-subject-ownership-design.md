# Explicit Subject Ownership and Lifecycle Design

**Date:** 2026-08-18

**Status:** Revised approved design for PR #419, pending independent documentation review

**Roadmap:** `2026-08-17-single-effective-context-stack-roadmap.md`

## Purpose

PR #419 replaces fallback-driven lifecycle attachment with strict explicit roots, one configured-context ownership domain, one effective ownership route, prospective structural admission, and one permanent Core membership ledger. It merges recursive inheritance into the exact single lifecycle coordinator resolved for the ownership domain and makes public fallback mutation service composition only.

This design is governed by the roadmap's [stack-wide acceptance criterion](2026-08-17-single-effective-context-stack-roadmap.md#stack-wide-acceptance-criterion). That criterion is stated only in the roadmap. Every wait, restart, persistent failure, and unsupported liveness condition below is interpreted through it.

## Terms and Invariants

- **Explicit anchor:** one root relationship created by `AttachToContext` and removed only by exact `DetachFromContext`.
- **Parent membership:** one committed structural `PropertyReference` that currently reaches a child. Repeated occurrences in one collection or dictionary property are one Core membership; Tracking retains occurrence indices for projections.
- **Ownership domain:** the exact plain configured `InterceptorSubjectContext` supplied to a root, compared by reference.
- **Effective route:** PR #474's exact immutable descriptor targeting the selected parent executor or explicit configured context.
- **Lifecycle coordinator:** the zero-or-one exact `ILifecycleInterceptor` instance captured for an active ownership domain. Any exact single implementation is canonical and receives the advanced protocol. `WithLifecycle()` is the standard built-in registration, not a concrete-type gate.
- **Topology turn:** the process-wide reentrant `OwnershipTopologyGate` interval serializing potentially structural and topology mutation.
- **Tentative batch:** an all-or-nothing `Preparing` set of proposed anchor, parent, domain, generation, route, and baseline changes.
- **Committed operation:** the exact immutable generation delta exposed to Tracking only after Core publication.
- **Private property/state monitor:** the one monitor owned by a subject's `InterceptorExecutor`. It replaces public `IInterceptorSubject.SyncRoot` and is never exposed as a synchronization contract.

The permanent invariants are:

1. A subject has zero or one explicit anchor, one ownership domain, one selected route, and one monotonically increasing ownership generation.
2. Core parent membership is keyed by exact `PropertyReference`; repeated collection occurrences do not multiply the Core reference count.
3. An explicit anchor wins route selection. In a lifecycle-coordinated domain, the earliest surviving compatible acyclic parent membership otherwise wins. A coordinator-free domain never creates parent membership.
4. Public fallbacks compose services only. They do not create an anchor, membership, lifecycle callback, or ownership route.
5. Every active effective context resolves zero or one exact lifecycle coordinator identity. Repeated paths to the same instance are valid; two distinct instances are permanently incompatible.
6. In a lifecycle-coordinated domain, backing structural values, Core membership, Tracking baseline, route, Registry/parent projection, and callbacks agree after a successful operation settles. In a coordinator-free domain, only an explicit root route is owned and structural setters are ordinary intercepted writes.
7. Cycles, shared DAGs, compatible multiple parents, repeated paths, and final component release remain supported.
8. Core alone enters the topology turn, reserves, commits, cancels, restarts, and finalizes. Tracking traverses, reserves through the facade, selects parents, pins callback arrays, reconciles committed state, and chooses the exact callback phase at which Core publishes the already committed selected route.

## Advanced Public Lifecycle Facade

Core and Tracking are separate packages. No new `InternalsVisibleTo` entry is added. Their performance-critical boundary is a normally visible public advanced contract. Advanced extension implementations are supported when they obey every strict lifetime, concurrency, no-retain, and no-throw obligation in this section.

The capability-minimal surface is:

```csharp
public interface ILifecycleInterceptor : IWriteInterceptor
{
    void PrepareSubjectAttachment(
        IInterceptorSubject subject,
        IInterceptorSubjectContext context,
        ref SubjectOwnershipOperation operation);

    void PrepareSubjectDetachment(
        IInterceptorSubject subject,
        IInterceptorSubjectContext context,
        ref SubjectOwnershipOperation operation);

    void PrepareSubjectPropertyWrite<TProperty>(
        ref SubjectOwnershipWriteContext<TProperty> context,
        ref SubjectOwnershipOperation operation);

    void PrepareSubjectMetadataAdditionBatch(
        ref SubjectMetadataAdditionBatchContext context,
        ref SubjectOwnershipOperation operation);

    void ReconcileSubjectOwnership(ref SubjectOwnershipOperation operation);

    void ReleaseSubjectOwnership(ref SubjectOwnershipOperation operation);
}

public readonly ref struct SubjectOwnershipWriteContext<TProperty>
{
    public PropertyReference Property { get; }
    public TProperty ProposedValue { get; }
}

public readonly ref struct SubjectMetadataAddition
{
    public SubjectPropertyMetadata Metadata { get; }
    public object? CurrentValue { get; }
}

public readonly ref struct SubjectMetadataAdditionBatchContext
{
    public IInterceptorSubject Subject { get; }
    public int Count { get; }
    public SubjectMetadataAddition GetAddition(int index);
}

public readonly ref struct SubjectOwnershipView
{
    public IInterceptorSubject Subject { get; }
    public IInterceptorSubjectContext? ExplicitAttachContext { get; }
    public IInterceptorSubjectContext? OwnershipDomain { get; }
    public PropertyReference? SelectedParent { get; }
    public long Generation { get; }
    public int ParentMembershipCount { get; }
    public PropertyReference GetParentMembership(int index);
}

public readonly ref struct SubjectOwnershipOperation
{
    public IInterceptorSubjectContext OwnershipDomain { get; }
    public SubjectOwnershipView GetView(IInterceptorSubject subject);
    public void ReserveParentAddition(PropertyReference property, IInterceptorSubject child);
    public void ReserveParentRemoval(PropertyReference property, IInterceptorSubject child);
    public void ReserveFinalRelease(IInterceptorSubject subject);
    public void SelectActiveParent(IInterceptorSubject subject, PropertyReference? parent);
    public ImmutableArray<TService> GetCurrentServices<TService>(IInterceptorSubject subject);
    public ImmutableArray<TService> GetProspectiveServices<TService>(IInterceptorSubject subject);
    public bool TryPublishCommittedRoute(in SubjectOwnershipView committedView);
}

public readonly ref struct SubjectMetadataCommitRegistration
{
    public static SubjectMetadataCommitRegistration Register(
        IInterceptorSubject subject,
        Action onCommitted);

    public void Dispose();
}
```

The batch context is a count/index view over Core's once-materialized metadata array and parallel current-value slots. It adds no enumerable, array copy, or provider-frame allocation. `CurrentValue` may box a value-type structural candidate because `AddProperties` is cold; the no-boxing requirement applies to warmed generic structural writes and their provider state.

The two generic service methods return exact immutable-array references. `GetCurrentServices` pins the array from the exact currently published executor snapshot. `GetProspectiveServices` resolves once during preparation against that snapshot plus the proposed route target/domain, without creating or publishing a routed `ContextState`; Tracking retains that prospective array only in its pooled frame. `GetParentMembership(index)` is a zero-allocation index API over Core's inline-first membership storage.

`TryPublishCommittedRoute` is deliberately acknowledged as one narrowly constrained commit-phase capability. Core creates the committed view only after ledger commit. The method accepts only that exact subject, current generation, and expected old descriptor, only while Core is invoking the matching coordinator reconciliation phase, and at most one call can publish. A provider call in any other phase or with a fabricated/mismatched view throws a persistent `InvalidOperationException`; an exact older generation/descriptor returns `false` as a stale no-op. Core still owns validation, ledger commit, retry, cancellation, generation, and final cleanup. Reflection tests inspect these capabilities and guards rather than method names.

`SubjectMetadataCommitRegistration` is the smallest separate cross-assembly commit-identity seam. `Register` pushes one exact thread-local token for the next `AddProperties` entry on that subject. Core claims that token into the outer operation immediately on entry, before input enumeration or any enumerable reentrancy, invokes its callback exactly once immediately after the metadata dictionary and ownership ledger commit and before reconciliation/user callbacks, and disposes or restores the previous token in `finally`. A precommit rejection never invokes it. A nested Registry call pushes and claims a distinct token; a nested unregistered Core call cannot steal the already claimed outer token. Equal or same-name metadata never participates in identity. The callback is an advanced synchronous no-retain/no-reentry/no-throw hook; Registry uses it only to promote the already allocated exact wrapper under the topology turn.

Tracking keeps its operation state in a thread-static pooled LIFO frame. Each public attach, detach, lifecycle-coordinator write invocation, or metadata batch prepare pushes exactly one frame. Repeated structural terminals downstream of one coordinator revise that frame's final committed delta; repeated calls upstream invoke the coordinator separately and therefore use separate frames. `ReconcileSubjectOwnership` consumes the final coalesced contents once per coordinator invocation. Core calls `ReleaseSubjectOwnership` exactly once for success, cancellation, restart, precommit exception, reconciliation exception, and finalization exception; Tracking pops, clears every reference, and returns buffers. Nested synchronous coordinator invocations push another frame.

No boxed enumerator, catch-all `object` provider payload, operation enum/runtime switch, or visually minimized method set is accepted when it adds allocations or broadens capability. Implementers must not retain any facade, view, write context, metadata context, membership value, callback array, or registration callback beyond the synchronous scope. Prepare and release methods must be concurrency-safe across threads and exception-free after input validation. There is no `EditorBrowsable(Never)` annotation. This is an advanced supported extension contract, not an ordinary application callback API.

## Global Topology Turn and Lock Order

`SubjectOwnershipCoordinator` owns one static process-wide monitor:

```csharp
internal static object OwnershipTopologyGate { get; }
```

Every potentially structural write enters it before context-state/action selection. Strict attach/detach, lifecycle preparation/reconciliation/finalization, Core ledger mutation, every PR #474 ownership-route mutation, context service/fallback mutation, and `AddProperties` enter the same monitor. `InterceptorSubjectContext.TryChangeOwnershipRoute` enters the gate itself and then `_mutationLock`; it never relies on caller ownership. The topology gate remains held through reverse registration, immutable state publication, conditional old reverse unregistration, and the complete reverse invalidation walk. `_mutationLock` is released before invalidation, preserving PR #474's leaf-lock discipline while preventing another structural operation from pinning an upstream stale chain.

The ordinary lock orders are:

```text
OwnershipTopologyGate
  -> at most one InterceptorSubjectContext._mutationLock
       -> at most one _usedByContexts leaf-set lock
  -> or at most one InterceptorExecutor private property/state monitor
  -> or at most one Tracking/Registry projection leaf lock
```

Synchronous `TryAddService` predicate/factory reentrancy is the one lower-lock exception. It may produce `OwnershipTopologyGate -> source context lock -> zero or more same-thread nested target context locks`, and nested ownership work may temporarily add one executor or feature projection leaf below that stack. All nested context locks unwind in LIFO order. No competing thread can own a target context lock before the topology gate, and no library code waits for the topology gate while holding a lower lock. Route mutation takes topology gate, one context lock, and one reverse leaf. Core ledger publication takes topology gate and one executor monitor at a time. Feature reconciliation takes one Registry or Tracking projection leaf at a time and invokes no user code under it. Reads, invokes, scalar writes, and `GetServices` never take the topology gate. A zero-interceptor read remains the current direct lock-free delegate call. Only an intercepted read terminal and scalar write terminal take the private executor monitor.

The global monitor is reentrant. Synchronous cross-domain A to B work runs inline. An external B to A caller waits before it acquires B's context or executor monitor, then rereads committed state after admission. Disconnected domains intentionally lose parallel structural and configuration throughput. They retain independent reads, scalar work, invokes, and service resolution. `Monitor` supplies exclusion and reentrancy but no FIFO or starvation guarantee. Completion claims assume finite contention and scheduler progress; sustained adversarial contention is an unsupported liveness condition and no allocation-heavy fairness queue is added.

User interceptors, lifecycle preparation/traversal, direct ownership readers, lifecycle handlers, events, property handlers, context predicate/factory callbacks, and direct backing writers run while the topology turn is owned. They do not run under a context lock or executor monitor, except:

- `TryAddService` invokes its predicate and factory under the topology gate plus the source `_mutationLock`, preserving current atomicity and state reread;
- the synchronous direct-store backing writer runs under the topology gate plus the initiating executor monitor;
- current `TryAddService` same-thread callbacks may reentrantly enter another built-in context lock because no competing thread can own that target lock before the topology gate.

Callbacks, predicates, factories, ownership readers, and custom writers must not synchronously wait for another thread whose operation needs the topology gate. Generated and built-in backing writers are direct stores. A custom writer is also required to be a synchronous direct store and must not invoke ownership, context, topology, or metadata APIs while the executor monitor is held. `IInterceptorExecutor.GetPropertyValue` XML likewise requires its delegate to be a synchronous, nonblocking direct storage read with no context, ownership, topology, or metadata call; generated and Dynamic reads comply. This is a contract-only restriction. Core adds no thread-local read guard or runtime diagnostic to a scalar/intercepted-read hot path, and deadlock avoidance is not guaranteed for an advanced delegate that violates the contract. A custom delegate that violates either reader or writer rules, including writer mutation followed by throw, is advanced caller misuse outside the library-controlled transient-failure criterion; the library does not promise rollback for that application code.

## Context Mutation and Array Pinning

`AddService`, `TryAddService`, `AddFallbackContext`, and `RemoveFallbackContext` enter the topology gate before `_mutationLock`. Each operation materializes or invokes its application input exactly once. `TryAddService` keeps current behavior: it checks services in order, invokes `exists` once per examined service until a match, calls `factory` at most once, rereads source state after callback reentrancy, and publishes one replacement state. Internal restart never replays either delegate.

A predicate or factory may mutate the same or another built-in context synchronously. Nested context work reenters the topology gate and takes only its target `_mutationLock`. A simultaneous reverse external caller waits outside all context locks. Custom `IInterceptorSubjectContext` implementations remain responsible for their own synchronization.

Before immutable publication Core substitutes the prospective state into the reverse dependency walk and computes the exact lifecycle coordinator identity set of every affected active ownership domain. Publication is legal only when each set remains the same zero-or-one reference identity. A change from none to one, one to none, one instance to another, or one to two on an active domain throws `InvalidOperationException` before context state and reverse edges publish. This is permanent semantic incompatibility. Nonunique service additions and fallback composition remain legal.

One structural operation pins:

- the exact cached `WriteInterceptorChain<TProperty>` and its immutable interceptor array after topology-gate entry and before the prefix;
- for every affected subject, one exact prospective `ImmutableArray<ILifecycleHandler>` resolved during preparation against the then-current executor snapshot plus proposed route overlay, and the exact logical coordinator insertion index dividing its pre-coordinator and post-coordinator segments;
- for every affected subject and property phase, one exact cached `ImmutableArray<IPropertyLifecycleHandler>`;
- the exact current route descriptor, expected old descriptor, subject generation, proposed route target, and ownership-domain identity used by those snapshots.

For attach, the prospective lifecycle and property arrays are resolved without prebuilding a routed `ContextState`. For detach/final release, current arrays come from the exact current old immutable state. The same pinned lifecycle array supplies both segments around the coordinator, preserving the current ordered service set. The coordinator insertion index is computed from the coordinator's rank in the complete ordered gathered service set; it does not require an advanced coordinator also to implement Tracking's `ILifecycleHandler`. The built-in `LifecycleInterceptor` uses the exact former `ContextInheritanceHandler` rank. Every descendant is pinned during preparation, not lazily during callback descent. Pooled `LifecycleReconciliationState` entries retain immutable array structs and the coordinator insertion index without copying arrays.

A legal handler callback may add a nonunique handler or identity-preserving fallback, including on the same executor before route publication. The current operation continues its already pinned arrays for every later subject and property phase. A synchronously nested new operation pins the newly published arrays and may observe the addition. On return, the outer operation resumes its old arrays.

At the exact coordinator route-publication phase, `TryPublishCommittedRoute` rereads the then-current immutable executor state, materializes the fresh `ContextOwnershipRoute`, and merges it into a fresh routed `ContextState` preserving all legal earlier service/fallback mutations. Semantic compatibility and required mutable buffer capacity were validated before backing commit, so this phase cannot introduce a semantic rejection. An out-of-memory failure is outside recoverable semantic guarantees. If a nested operation has already committed a newer subject generation or descriptor, the older publication returns `false` without constructing the replacement and the stale callback tail stops. Route objects are therefore constructed only after the final exact stale check. Prospective service-walk/handler arrays are the unavoidable earlier exception: they must be pinned before any user callback to preserve callback order, so a callback-created later generation can make an outer route publication stale after those arrays were already materialized. The route-changing allocation rows count that case separately.

An active initiating structural interceptor prefix that synchronously attaches or detaches its own subject cannot splice the already selected structural chain and cannot replay the prefix, whether that subject is route-free or already owned. A route-free structural prefix likewise cannot change the coordinator identity selecting its own chain. Core detects that exact structural-operation self-modification and throws ordinary `InvalidOperationException` before publication. The same structural prefix, arguments, and committed state fail identically on immediate retry. External topology callers wait for the structural prefix/topology turn to finish and then succeed according to the resulting committed state. Coordinator-preserving nonunique mutation remains legal and affects future operations. Scalar prefixes remain outside the topology gate and carry no active-prefix marker: they may synchronously attach or detach their own subject, their already selected scalar chain completes, and future operations observe the new route. External scalar/ownership interleaving retains current serializable visibility.

## Ownership State Machine

Configured-context activation is:

```text
Absent -> Preparing -> Active -> Releasing -> Absent
          |                         |
          +---- failure cleanup ----+
```

The activation stores domain identity, exact coordinator identity, generation, lease count, and reverse affected-domain membership. It is state, not a monitor. Failed first activation clears every binding and reference before returning to `Absent`.

Each ledger-bearing operation batch is one of `ExplicitAttach`, `ExplicitDetach`, `StructuralCoordinatorInvocation`, or `MetadataAddition` and follows:

```text
Preparing -> Committed -> Reconciling -> Finalized
Preparing -> Cancelled -> Finalized
```

There is no `Committing` public or internal phase and no second publication monitor. The topology gate covers validation, direct store, ledger publication, reconciliation, and finalization. A pooled `StructuralPrefixScope` is a non-ledger carrier for exact active-prefix identity and batch nesting; it is pushed before the structural chain and returned in `finally`, while scalar chains allocate/push nothing. Each coordinator invocation pushes a ledger-bearing child accumulator. The accumulator remains `Preparing` while downstream executes; each terminal has an inline transient attempt status whose pending markers are cleared after its successful backing/ledger commit. When the coordinator regains control, zero successful stores finalize without a committed operation, while one or more transition the accumulator once to `Committed` with the coalesced final delta. `Cancelled` never returns to `Preparing`; Core clears and returns that attempt, rents or reuses a fresh empty batch, and restarts the terminal boundary.

A nested structural or metadata operation that finds any target subject reserved by an outer `Preparing` terminal attempt cancels every tentative reservation in that attempt before selecting its own action. It then executes independently from current committed or route-free ownership. With a downstream repeater, an earlier successful terminal commit in the same coordinator accumulator is no longer tentative and remains the accumulator's fallback final delta; cancelling a later preparing attempt never discards it. After each reentrant application call, preparation observes the cancelled attempt status and returns normally to the outer Core terminal boundary, which repeats discovery from final values. It never replays the interceptor prefix, a backing writer that already ran, lifecycle callback, `TryAddService` predicate/factory, or `AddProperties` input enumeration.

Restart is unbounded for library-controlled cancellation, revision change, or stale snapshot. There is no cancellation counter, finite retry exception, or public retry result. Application structural traversal that mutates finitely many times and stabilizes must succeed regardless of whether stabilization occurs before or after any former numeric threshold. Traversal that changes relevant application state on every pass may prevent the synchronous call from returning; that is an unsupported application liveness/programming-contract condition, not a library-thrown failure.

A nested operation during `Committed` or `Reconciling` creates a later generation and is the valid serial winner. The older callback tail compares exact generation and descriptor before each remaining action. It never clears or reports the later state.

## Structural Write Protocol

Task 1's retained `CanContainSubjects` classification selects the path. Scalar properties use the existing public four-argument executor API and never enter the topology gate. A zero-interceptor read remains lock-free; an intercepted read terminal and scalar write terminal acquire the private executor monitor. Structural properties use:

```csharp
bool SetPropertyValue<TProperty>(
    string propertyName,
    TProperty newValue,
    TProperty currentValue,
    Action<IInterceptorSubject, TProperty> writeValue,
    bool canContainSubjects);
```

If the subject has no active exact lifecycle coordinator, the structural overload preserves ordinary write-interceptor behavior but creates no ownership batch, parent membership, baseline, child route, or lifecycle callback. Explicit ownership of the root remains intact. This applies both to a prepopulated child at explicit attach and a child assigned later. If exactly one `ILifecycleInterceptor` implementation is resolved, whether installed by `WithLifecycle()` or advanced registration, that exact identity is canonical and receives the protocol; two distinct identities are incompatible.

With the exact lifecycle coordinator, continuation behavior remains position-dependent exactly as in the current chain. A repeating interceptor upstream of the coordinator invokes the coordinator independently for each `next`; every coordinator invocation owns one accumulator, reconciles once, and releases before the upstream repeater can call `next` again. A repeating interceptor downstream of the coordinator executes zero, one, or several terminal attempts inside one coordinator invocation. Zero successful stores produce no reconciliation. One or several successful stores produce one reconciliation after downstream returns, from Tracking's prior committed baseline to the final successfully stored value. The prefix before the repeater is never replayed.

The coordinator-invocation accumulator records ordered terminal attempt/revision metadata and the most recent successfully committed Core generation/delta. It is not a callback queue. Each terminal attempt independently rereads committed state, prepares and validates its proposed value before its direct backing store, commits its Core ledger generation after a successful store, and then replaces the accumulator's final delta while clearing superseded tentative buffers. Intermediate downstream stores do not emit lifecycle callbacks or route publication. If nested work independently reconciles a later generation and no later terminal in this accumulator commits, the accumulator's exact generation check makes its older final delta a stale no-op. If another terminal then commits, it becomes the later winner and rebases the coalesced delta from the then-current committed Tracking baseline. If a later terminal attempt is permanently incompatible, that attempt throws before its store; an earlier successful store remains the accumulator's final committed value and is reconciled once during unwind unless a newer independently reconciled generation already superseded it. If downstream throws after one or more successful stores, the same one final reconciliation runs and the original downstream exception wins. This preserves current `LifecycleInterceptor` placement semantics without imposing an at-most-once continuation rule or allocating an ordered queue.

Each lifecycle-coordinated terminal invocation follows:

1. Enter `OwnershipTopologyGate` before reading ownership state or selecting an action.
2. Pin the exact context snapshot, compiled chain, and interceptor array.
3. Execute the interceptor prefix once. When the exact coordinator node is entered, push one empty coordinator-invocation accumulator and Tracking frame. Interceptors may transform `NewValue` and call continuations zero, one, or several times.
4. At each terminal, build `SubjectOwnershipWriteContext<TProperty>` from that attempt's final transformed value. The lifecycle coordinator traverses only direct ownership values, reserves every subject once for that attempt, and pins all callback arrays required for the accumulator's prospective final delta.
5. If nested work cancels the batch, release the provider frame and all reservations, create a fresh attempt, and repeat terminal discovery from final values. Do not replay the prefix.
6. Validate exact domain/coordinator identity, subject generations, route descriptors, and initiating property revision. Permanent incompatibility cancels and throws before the backing writer.
7. Enter the initiating executor monitor, recheck the property revision, and if stale release it and restart terminal discovery without invoking the writer. Otherwise call the synchronous direct-store writer once, set `IsWritten`, stamp origin/write state, and increment revision.
8. While retaining the topology gate, publish that attempt's preallocated Core ledger entries under one affected executor monitor at a time, stamp its generation, and replace the coordinator accumulator's final committed delta. Clear superseded attempt-only reservations without publishing callbacks.
9. When the exact lifecycle coordinator chain node regains control, expose no operation if no terminal store succeeded. Otherwise expose only the accumulator's final coalesced committed operation. Tracking records the final baseline first, reconciles callbacks once with pinned arrays, and invokes `TryPublishCommittedRoute` at the exact coordinator slot. An upstream repeater gets this complete sequence once per coordinator invocation; a downstream repeater gets it once for all its successful terminal stores.
10. Core finalizes route descriptors, accumulator state, TLS and pooled storage in `finally`.

The topology gate prevents any concurrent structural/context mutation between validation and commit except synchronous legal reentrancy on the same thread. Scalar writers can change the initiating revision before step 7; the under-monitor recheck restarts without a public failure. Core prevalidates semantic compatibility and reserves mutable operation/reverse capacity before the backing writer. It does not prebuild an immutable route or routed state. After commit, the exact coordinator publication phase performs a final generation/descriptor check, then creates the route from the then-current immutable executor state. An ordinary stale or losing attempt therefore allocates no route object. PR #474 reverse snapshots, route/service-cache construction, prospective service-walk arrays, and invalidation generations remain measured route-change costs rather than hidden warmed-stable costs.

### Ownership discovery reader contract

`SubjectPropertyMetadata` adds the distinct optional delegate:

```csharp
public Func<IInterceptorSubject, string, Type, object?>? GetOwnershipValue { get; }
```

Both public constructors add `Func<IInterceptorSubject, string, Type, object?>? getOwnershipValue = null` as their final parameter. Canonical lifecycle traversal invokes it with the exact metadata name and declared type and never invokes ordinary `GetValue`. The source generator supplies a noncapturing direct backing-field delegate for an intercepted partial structural property with a getter. It supplies `null` for computed/nonintercepted properties. Dynamic and advanced metadata may opt in only with an equivalent synchronous topology-free direct storage reader whose storage changes flow through that property's gated setter or this `AddProperties` batch. Missing reader means no automatically discoverable initial edge; a later gated structural setter still uses its final generic value and is tracked normally.

Dynamic keeps its existing per-proxy `DynamicSubjectInterceptor._propertyValues` and regular property-read path. `DynamicSubjectFactory` creates the interceptor explicitly, creates the proxy with that instance, and immediately inserts that exact interceptor into the proxy subject's existing `Data` under one private static reserved tuple key containing a package-qualified GUID string. This association occurs before metadata lookup or `AddProperties`; collision is a persistent factory invariant failure. The cached Dynamic metadata array retains no subject and uses one static noncapturing `GetOwnershipValue(subject, propertyName, propertyType)` helper. The helper resolves the subject-owned association, then calls the interceptor's direct `ReadProperty` method without proxy reflection, a public getter, or the read-interceptor chain. Missing values use the same `GetOrAdd` default semantics as the ordinary Dynamic read: `null` for reference types and one memoized `Activator.CreateInstance(propertyType)` box for value types. The existing interceptor object and one `Data` node are the only per-proxy association costs; no direct ownership read allocates after first default materialization, and no static table retains subjects. Proxy lifetime owns subject, `Data`, interceptor, and storage as one collectible component.

An ownership reader may not run read interceptors, derive its result from scalar/external mutable state, invoke context/ownership/topology/metadata APIs, or wait for another thread. Nonintercepted/computed structural properties that cannot meet the direct-storage contract are not valid automatic ownership edges. This mechanically prevents an unversioned scalar/external dependency from becoming a silently stale ownership claim. Generator snapshots, Core Public API, Dynamic, manual metadata, XML, and normal-path source tests explicitly cover the new delegate and compatibility loss.

Direct collection mutation outside an intercepted setter remains outside prospective admission. Supported collection wrappers must turn changes into structural writes. Unobserved external raw mutation has no transactional ownership guarantee.

## Explicit Attach and Detach

The strict APIs are:

```csharp
public static void AttachToContext(
    this IInterceptorSubject subject,
    IInterceptorSubjectContext context);

public static void DetachFromContext(
    this IInterceptorSubject subject,
    IInterceptorSubjectContext context);

public static bool TryGetAttachContext(
    this IInterceptorSubject subject,
    out IInterceptorSubjectContext? context);
```

Attach validates null and exact supported plain-context shape before resolving subject services, enters the topology gate, prepares activation, and reserves the explicit anchor. When exactly one lifecycle coordinator identity is present, Tracking also discovers and reserves the complete direct-readable reachable graph before Core commits. A custom exact single `ILifecycleInterceptor` receives the same public advanced protocol as the standard provider installed by `WithLifecycle()`. A duplicate anchor or incompatible domain/coordinator throws before callbacks or state change. A coordinator-free domain commits only the root anchor and direct route: Core does not discover, adopt, release, or route prepopulated or later property descendants, and no recursive lifecycle callback runs.

Detach validates the exact current anchor under the topology gate. Missing or wrong context fails persistently. Core removes only the anchor, chooses a surviving inherited route or reserves final component release, commits, and reconciles. Explicit-to-inherited transfer emits no detach/attach pair. Several roots in one exact configured context share activation/coordinator identity while retaining independent anchors.

Generated and Dynamic constructors taking a context call `AttachToContext` and are strict explicit roots. Parameterless subjects remain route-free until a committed parent edge in a lifecycle-coordinated domain adopts them. A context-constructed child remains explicitly owned when a parent edge disappears.

## Dynamic `AddProperties` and Registry Handshake

`IInterceptorSubject.AddProperties` remains callable before, during, and after activation. Generated and Dynamic implementations delegate to the existing public advanced package-infrastructure interface `IInterceptorExecutor`. The method is public because generated subjects compile in consumer assemblies, but it is not an application-level mutation handle: the writer must synchronously publish the supplied complete dictionary into the same subject and must not retain either argument.

```csharp
public void AddProperties(
    IEnumerable<SubjectPropertyMetadata> properties,
    Action<IInterceptorSubject, IReadOnlyDictionary<string, SubjectPropertyMetadata>> writeProperties);
```

Generated and Dynamic code pass a noncapturing static writer that casts the supplied subject and assigns its private metadata field. Core materializes the input once and invokes that writer once only after admission succeeds.

The executor treats one public call as one atomic batch. Its first action on method entry is to claim the exact matching thread-local `SubjectMetadataCommitRegistration` token into a local operation slot, before argument enumeration, topology-gate waiting, metadata lookup, or any other application code. From that point a nested call cannot observe or claim the token. One `finally` either invokes it at the exact commit point or discards it on every precommit exit. The remaining sequence is:

1. Enter the topology gate, then enumerate the input exactly once into the existing materialized array. Reentrant public operations triggered by the enumerable run inline. After enumeration, rebuild the outer prospective dictionary against the then-current committed dictionary and preserve current duplicate-name behavior.
2. Classify every new metadata entry through Task 1's retained flag. For every structural entry with `GetOwnershipValue`, invoke that direct reader exactly once into a parallel cold-path value slot. Ordinary `GetValue` is never invoked for ownership.
3. If enumeration, duplicate validation, any later reader, or permanent ownership admission fails, publish neither any metadata entry nor any ownership/projection change. Earlier values in the batch remain private and are cleared.
4. Create one `SubjectMetadataAdditionBatchContext` over all materialized additions and values. When an exact lifecycle coordinator owns the subject, call `PrepareSubjectMetadataAdditionBatch` once, using one Tracking frame to validate all structural additions together. In a coordinator-free domain, no lifecycle preparation occurs.
5. Under the executor monitor, invoke the metadata writer once with the complete prospective dictionary and publish every entry atomically. Publish the batch's Core memberships/generation in the same commit and invoke the exact claimed metadata commit token. Reconcile once and release once.

Scalar/nonstructural entries in a mixed batch perform no lifecycle work. Structural metadata without `GetOwnershipValue` publishes but contributes no current edge; a future gated structural setter final value is tracked. Two structural entries, a scalar plus structural entry, and a failure in the second ownership reader all therefore have one all-or-nothing metadata publication point. Cold metadata current values may box value-type structural candidates. Derived `base(context)` followed by generated `AddProperties` and Dynamic batch additions use this same transaction.

Registry's `RegisteredSubject.AddProperty` uses exact commit identity rather than name or value inference:

1. Allocate the exact `RegisteredSubjectProperty` and its linked commit node, then call `SubjectMetadataCommitRegistration.Register(Subject, commitCallback)`. The token is bound by reference to this wrapper. Public `Properties`, `TryGetProperty`, and snapshots do not consult it.
2. Call `Subject.AddProperties`. The first instruction in the Core executor entry claims that exact token into this operation before input enumeration or any application callback. An unregistered nested same-subject `AddProperties` from outer enumeration cannot steal it. At the single Core metadata/ledger commit, before any lifecycle/property/user callback, Core invokes the token once and Registry links that exact wrapper into its committed list and invalidates its frozen snapshot under the topology turn.
3. A precommit rejection disposes the uninvoked token and wrapper. Neither metadata nor Registry property is public.
4. A later lifecycle/property callback exception leaves the exact metadata dictionary and exact wrapper committed, then the original user exception wins and is rethrown after reconciliation/finalization.

Nested same-thread equal or same-name metadata operations carry distinct tokens. A nested commit can promote only its own wrapper. If it makes an outer operation duplicate or cancels the outer tentative batch, the outer rejects or restarts without promoting its token. No metadata equality, name lookup, or public dictionary observation infers commit. The old manual null-to-value intercepted write, separate `AttachSubjectProperty`, and lifecycle-only pending lookup are removed. This is a cold path and adds no property hot-path branch.

## Exact Callback Order and Route Visibility

Tracking pins every array before Core marks the batch committed. Ordinary one-context order is preserved. The domain's canonical single `ILifecycleInterceptor` identity defines one logical coordinator phase at its rank in the complete ordered service set. `WithLifecycle()` supplies the standard built-in `LifecycleInterceptor`, whose phase replaces `ContextInheritanceHandler` at the same recursion boundary. An advanced custom coordinator uses its own resolved ordered rank and need not implement `ILifecycleHandler`.

First attach of one subject is chronological:

| Order | Callback phase | Route visible | Array source |
|---|---|---|---|
| 1 | lifecycle service handlers before coordinator, in forward order | local/previous route; committed parent ledger already visible | selected-route lifecycle array, pre-coordinator segment |
| 2 | exact coordinator phase | publish selected route, then recursively reconcile descendants | logical insertion index in same pinned lifecycle array |
| 3 | lifecycle service handlers after coordinator, in forward order | selected route | same pinned lifecycle array, post-coordinator segment |
| 4 | subject's own `ILifecycleHandler` | selected route and parent projection | subject instance |
| 5 | `SubjectAttached` event | selected route | pinned event delegate at invocation |
| 6 | each property's service `IPropertyLifecycleHandler`s, then subject property handler | selected route | selected-route property-handler array per subject |

Final detach/release of one subject is chronological:

| Order | Callback phase | Route visible | Array source |
|---|---|---|---|
| 1 | each property's service `IPropertyLifecycleHandler`s, then subject property handler | old route and old baseline | current-route property-handler array per subject |
| 2 | `SubjectDetaching` event | old route and parent projection | pinned event delegate at invocation |
| 3 | subject's own `ILifecycleHandler` | old route and parent projection | subject instance |
| 4 | lifecycle service handlers before coordinator, in forward order | old route | current-route lifecycle array, pre-coordinator segment |
| 5 | exact coordinator phase | recursively reconcile descendants, then publish transfer/clear | logical insertion index in same pinned lifecycle array |
| 6 | lifecycle service handlers after coordinator, in forward order | transferred route or route-free state | same pinned lifecycle array, post-coordinator segment |

Nonfinal parent addition/removal still invokes the current lifecycle handler change in the same service/subject ordering but does not repeat first-attach event/property phases. Structural replacement remains detach old membership before attach new membership. A single combined order-oracle test records service segments, coordinator recursion, subject handler, event, property handlers, parent visibility, and route identity for attach and detach.

## Exact Coordinator Exception Deferral

`WriteInterceptorChain<TProperty>` records the exact lifecycle coordinator node reference/index in the pinned chain. Only that node uses a special continuation and owns one coordinator-invocation accumulator. A route-free composed coordinator without a matching Core operation remains an ordinary transparent interceptor.

The node invokes downstream in `try/catch`. Each successful terminal below it commits one ordered Core revision/generation into the same accumulator and replaces its final delta. If downstream throws after one or more stores, including after a successful first store followed by an incompatible second attempt, the node captures the first exception with `ExceptionDispatchInfo`. It then exposes only the last successfully committed coalesced delta, lets Tracking record the final baseline and reconcile once, and has Core finalize in `finally`. The original downstream exception is rethrown with its stack. If no terminal store succeeded, no reconciliation runs. If a repeater is upstream, each call reaches a fresh coordinator invocation and therefore follows this sequence independently.

Exception precedence is:

1. original downstream exception;
2. otherwise an exact metadata commit-notification exception from an advanced provider violating its no-throw obligation;
3. otherwise first reconciliation/user-callback exception;
4. otherwise Core finalization exception.

Core continues required reconciliation/finalization after a postcommit error and records secondary errors diagnostically; they never replace an earlier exception. The built-in Registry commit notification does not throw. If downstream throws before terminal commit, no operation is exposed, Tracking reconciliation does not run, Core cancels/releases the operation, an unclaimed Registry token is discarded, and the original exception propagates.

## Error and Outcome Table

| Condition | Outcome | Stable basis |
|---|---|---|
| external topology contention | wait outside lower locks, reread, execute serially | no failure |
| stale revision, stale snapshot, or cancelled tentative discovery | unbounded internal terminal restart | no prefix/writer/callback/input replay |
| nested cross-context/domain work | execute inline reentrantly | one topology turn |
| nested write into `Preparing` subject | cancel outer, commit nested, restart outer | valid nested-before-outer order |
| later generation during reconciliation | later state wins; stale tail stops | exact generation/descriptor |
| null or unsupported API argument | `ArgumentNullException` or `ArgumentException` | same argument fails again |
| duplicate attach, wrong detach, incompatible domain | `InvalidOperationException` before commit | committed state remains incompatible |
| active coordinator identity change | `InvalidOperationException` before context publication | captured active identity unchanged |
| active initiating structural prefix attaches or detaches its own subject, or route-free structural prefix changes own coordinator | `InvalidOperationException` before publication | same structural call structure fails again |
| scalar prefix attaches or detaches its own subject | nested topology operation completes; selected scalar chain then completes | current scalar behavior; future operations see new route |
| finite application-driven cancellation sequence | keep restarting until final values stabilize | no numeric threshold |
| absent direct ownership reader | metadata/attach ignores that automatic edge; later gated setter remains tracked | explicit metadata contract |
| custom intercepted reader violates direct topology-free storage contract | unsupported advanced-caller contract; no runtime guard or deadlock guarantee | outside library-controlled transient state |
| sustained unfair monitor contention | completion not guaranteed under adversarial scheduling | unsupported liveness; no transient exception |
| ownership reader/factory/predicate/input enumeration throws | original exception, with protocol-specific pre/postcommit state | user exception |
| direct-store writer throws before mutation | original exception; no ledger commit | provider/user exception |
| writer mutates then throws | original exception; provider misuse, no rollback promise | explicit writer contract |
| downstream throws after commit | reconcile/finalize, then original exception | committed state consistent |
| stale route callback after a later generation | return `false`, allocate/publish nothing, stop stale tail | exact generation/descriptor |
| OOM during exact postcommit immutable route materialization | runtime failure outside recoverable semantic guarantees | semantic compatibility was already validated |

There is no public retry or dedicated nesting exception.

## Deterministic No-Transient Schedules

Tests use `ManualResetEventSlim`, `Barrier`, `CountdownEvent`, and task completion. Each losing task signals `attemptingEntry` immediately before its public API call; negative blocked assertions occur only after that handshake. Timeouts are hang guards, never correctness evidence. Tests use When/Then names and explicit Arrange/Act/Assert sections.

1. Two external structural calls start behind a barrier. The first signals from a callback while holding the topology turn. The second signals `attemptingEntry`, cannot enter its callback, then completes after release in a valid serial order.
2. Callback A synchronously writes B and B synchronously writes A. The stack runs inline. A simultaneous reverse external caller signals attempted entry, waits outside lower locks, and reevaluates after release.
3. `TryAddService` predicate and factory on context A each synchronously mutate context B and perform attach/detach/structural work exactly once. A simultaneous external B-to-A caller waits. Each delegate runs exactly once.
4. Fallback add/remove races a coordinator mutation in its target/deeper reverse cone. Exactly one serial order wins; the other publishes legally or observes persistent authority incompatibility. The topology gate spans complete reverse invalidation.
5. A finite one-time nested mutation from structural collection traversal cancels the outer preparation. The nested operation commits independently and the outer terminal rereads direct storage/traversal and restarts without prefix, writer, callback, or input replay. A later outer rejection leaves the nested commit and no reservation leak.
6. The initiating property revision changes before the executor-lock recheck. Terminal preparation restarts and the backing writer executes once.
7. A downstream interceptor calls `next`, observes terminal commit, then throws. Baseline/membership reconcile and Core finalizes before exact rethrow; a preterminal throw exposes nothing.
8. A committed callback reentrantly commits a later generation with the same target. The old route callback returns stale without allocating and cannot clear the later descriptor.
9. More than 256 finite deterministic cancellations stabilize and succeed. There is no numeric limit or retry exception.
10. Handler H1 adds same-executor H3/fallback before the coordinator. The outer subject and every descendant/property phase continue pinned `[H1, coordinator, H2]`; a nested/future operation observes H3; route publication merges H3 into the then-current state. A nested later generation makes the old publication a no-op.
11. A structural prefix legally adds an interceptor and nests another write. The outer chain stays pinned and the nested call uses the new chain.
12. Direct concurrent `TryChangeOwnershipRoute` calls serialize by self-entry. Exact descriptor ABA, reverse fan-out greater than one, and complete invalidation remain correct.
13. A plain context with no lifecycle coordinator explicitly owns/routes a root containing a prepopulated child, and another child is assigned after attach. Neither child gains ownership, route, membership, baseline, or lifecycle callbacks. A context with one custom `ILifecycleInterceptor` proves that exact identity is canonical and receives prepare/reconcile/release; two distinct instances reject.
14. One `AddProperties` call covers two direct-readable structural additions, mixed scalar/structural entries, a second-reader exception, and enumerable reentrancy between yielded additions. Publication is all or nothing with one prepare/commit/reconcile/release.
15. Metadata covers absent ownership reader, derived `base(context)`, Dynamic opt-in direct reader, duplicate name, permanent incompatibility, and value-type candidate boxing only on the cold path.
16. Registry covers success, precommit rejection, postcommit user callback throw, nested same-name equal metadata, nested same-name different metadata, a duplicate created after outer cancellation, and an unregistered nested same-subject `AddProperties` call triggered while the registered outer enumerable is being materialized. Core claims the outer token before enumeration, so only the exact operation token promotes its wrapper.
17. Generated metadata proves direct backing-field discovery bypasses read interceptors. Dynamic creates one proxy/interceptor pair, installs the exact interceptor under the reserved subject-owned `Data` key before `AddProperties`, and its static metadata reader resolves name/type directly from that storage with current default semantics. Weak-reference coverage proves no static subject retention. A computed scalar-dependent structural property has no automatic edge while concurrent scalar changes occur, so the unsupported pattern cannot silently claim ownership. XML/source tests document the custom-reader contract without promising a runtime diagnostic.
18. Place a continuation repeater immediately upstream of the coordinator and separately immediately downstream. For each placement cover zero, one, and two calls with two distinct values. Upstream produces zero, one, or two independent coordinator reconciliations. Downstream produces zero or one reconciliation from the prior baseline to the final successfully stored value. A downstream incompatible second attempt reconciles the first store once before rethrow; a throw after the second successful store reconciles the second value once and preserves the original exception.
19. An active route-free structural prefix attempts coordinator change, own attach, and own detach; an already-owned structural prefix attempts self-detach. No-waiter calls fail persistently on immediate retry. An external attempted-entry waiter blocks, then succeeds after the prefix finishes. A scalar prefix performs own attach and detach successfully without a structural TLS marker; its selected scalar chain completes and a later operation observes the new route.
20. `InitializedContextZeroReadInterceptors` remains lock-free in generated source/disassembly. XML and static source tests prove generated and Dynamic direct delegates are topology-free and that no scalar/read TLS diagnostic guard was added. Advanced custom-reader inversion is documented as unsupported misuse and is not executed as a deadlock test.
21. Bounded mixed rounds combine attach/detach, structural replacement, identity-preserving context mutation, atomic AddProperties, cycles, DAGs, repeated references, and cross-context callbacks. All completed calls map to a valid serial result and quiescent projections agree.

## Memory Ownership and Cleanup

- `OwnershipTopologyGate` is one static object and the only ownership/topology operation monitor.
- Each initialized subject executor owns exactly one private property/state monitor. Removing public `SyncRoot` removes the generated/Dynamic root object; no second subject lock is introduced.
- Inactive/route-free executors retain no ownership state beyond the existing executor and private monitor. Ownership state is allocated on first prospective anchor/membership and cleared on final release.
- First parent membership and first batch entry are inline. Overflow collections are lazy, insertion ordered, pooled where operation-local, and cleared item by item.
- Core owns one `finally` for every batch attempt. It clears pending subject references, exact descriptors, coordinator/domain references, TLS links, and pool items before return.
- Tracking owns one thread-static pooled LIFO reconciliation frame per nested operation. `ReleaseSubjectOwnership` is called exactly once by Core and clears handler arrays, traversal buffers, baselines-in-progress, occurrences, subjects, properties, and previous-frame links.
- Immutable interceptor/service arrays are retained only by immutable context state/cache or the current stack. Pinned array structs do not copy underlying arrays.
- Registry commit tokens/wrappers live only until precommit discard or exact commit-time linked publication. Final release clears Registry/parent projections through ordinary callbacks.
- Tracking's persistent baseline dictionary contains only committed structural properties. It is not an ownership source and removes entries on final release.
- Failed/cancelled batches publish no anchor, membership, route, baseline, Registry wrapper, or activation lifetime edge.

Weak-reference tests cover failed first attach, cancelled tentative discovery, detached explicit root, released cycle, multi-parent final detach, synthetic metadata rejection, superseded generation, and provider-frame cleanup after each exception precedence row.

Every new thread-static `List`, `HashSet`, or `Dictionary` pool follows PR #474's `MaximumRetainedTraversalSize = 1024` policy. Release clears all references. If any retained list capacity or set/dictionary count/capacity-equivalent exceeds 1024, that oversized storage is dropped rather than returned to the thread-static holder. A large-graph test crosses 1024, releases the operation, and proves the next small operation does not retain the large graph/buffer.

## Capability Losses

- Public fallback mutation no longer attaches, detaches, or establishes inheritance.
- A subject cannot have several explicit anchors or unrelated ownership domains.
- An owned factory child cannot be stolen or silently retained as a different root.
- Recursive property ownership is unavailable without one exact lifecycle coordinator; `WithLifecycle()` is the standard built-in registration and an exact single advanced implementation is equally canonical. A plain coordinator-free context owns only explicit roots and composes services.
- Shallow lifecycle without recursive property inheritance is removed.
- An active initiating structural chain cannot synchronously attach or detach its own subject; a route-free structural chain cannot synchronously change its own coordinator. Scalar prefixes retain current self-attach/detach behavior.
- The public `IInterceptorSubject.SyncRoot` member and public atomic-lock snapshot capability are removed. Generated, Dynamic, and manual subject shapes must rebuild.
- Callbacks, factories, ownership readers, structural traversal, and custom writers cannot synchronously wait for another thread needing the topology turn.
- Computed/nonintercepted/scalar-dependent/external-state getters are not automatic ownership edges. Automatic discovery requires the distinct direct storage reader.
- Permanently non-stabilizing direct ownership readers and sustained adversarial monitor contention are unsupported liveness conditions.
- Disconnected domains lose parallel structural and configuration throughput.

Preserved capabilities include nonunique service mutation, identity-preserving composition fallback add/remove, same-thread cross-context `TryAddService` reentrancy, branch-local services, cycles, DAGs, repeated references, compatible multiple parents, `AddProperties` at any time, strict explicit roots, composition-only fallbacks, and ordinary one-context callback behavior.

## Performance Acceptance

- Reads, invokes, scalar writes, and cached service resolution do not enter the topology gate. Scalar generated/API shapes contain no structural flag or topology branch.
- The current zero-interceptor read fast path remains a direct lock-free delegate invocation. Only an intercepted read terminal and scalar write terminal use the one private executor monitor. Exact benchmark/disassembly control `InitializedContextZeroReadInterceptors` must match master and cleaned PR #474 shape; a repeatable regression reopens the design.
- Warmed route-free and owned structural writes whose membership and effective route remain stable allocate zero managed bytes beyond configured application work.
- An actual attach, detach, route replacement, or transfer/reparent may allocate PR #474's fresh immutable `ContextOwnershipRoute` and routed `ContextState`, reverse fan-out snapshots, prospective service-walk storage/cache/`ImmutableArray` results, cache-free invalidation generations, and first-use retained reverse/batch/traversal capacity. Old immutable generations cannot be pooled while lock-free readers may retain them. These objects are counted explicitly and route-changing operations add no other avoidable per-operation allocation.
- Immutable handler arrays are reused by reference and reconciliation/batch/traversal storage is pooled. No task, closure, waiter node, boxed enumerator, catch-all payload, or per-call array copy is permitted on warmed structural rows.
- Exact stable-machine rows are `InitializedContextZeroReadInterceptors`, `StructuralWriteStableTopology`, `OwnershipRouteAttach`, `OwnershipRouteDetach`, `OwnershipRouteTransferReparent`, `StructuralWriteNoRouteChangeConservative`, `OwnershipRouteAttachBranchLocalHandlerFallback`, and `OwnershipRouteTransferReverseFanoutGreaterThanOne`. Run the same patch/digest at exact master, cleaned PR #474 base, and final PR #419 head. Stable-topology throughput/allocation must be equal to or better than exact master outside control noise. Every route row records route/state, reverse snapshot, service-walk/cache/array, invalidation-generation, first-use retained-capacity, operation-count, allocated-byte, and throughput deltas. A repeatable regression beyond agreed noise relative to its applicable master/PR #474 baseline reopens the design.
- A two-disconnected-domain benchmark records the intentional serialization loss.
- Connector and HomeBlaze structural convoying is measured. Operationally material convoying reopens the design.
- `AddProperties` and Registry snapshot rebuild are cold and may allocate their documented materialized input/frozen snapshots, exact commit token/wrapper, and boxed value-type ownership candidates; no property hot path changes for metadata support.
- Local timings are diagnostic. The external stable benchmark machine and exact final hash are authoritative.

Static review confirms one topology monitor, one private executor monitor per initialized subject, route mutator self-entry, no alternate admission word/drain/publication gate, no callback under an executor/context lock except documented `TryAddService` nesting and the direct store, and no new friend declaration.

## Migration and Release Boundary

Core, Tracking, Generator, Dynamic, Registry, Hosting, Connectors, the WebSocket SampleClient, and every compiled semantic consumer change atomically in Task 2. `IInterceptorSubject.SyncRoot` is removed from Core Public API, generated snapshots, Dynamic, manual test subjects, collision diagnostics, NI0014 shape tests, and documentation. `SubjectPropertyMetadata.GetOwnershipValue` and constructor parameters, generated backing-field delegates, Dynamic opt-in, Core snapshot, and compatibility tests change in the same atomic boundary. Generated/custom consumer models must rebuild.

The exact single `ILifecycleInterceptor` resolved for a domain becomes its recursive-ownership coordinator. `WithLifecycle()` remains the standard built-in registration. `WithContextInheritance`, `ContextInheritanceHandler`, and `PropertyReferenceSet` are removed. Ordering attributes naming the old handler target `LifecycleInterceptor`. Generated/Dynamic context constructors become strict roots; parameterless children inherit only inside lifecycle-coordinated domains. Fallback detach shorthand becomes exact detach.

The complete Task 2 manifest includes the broader seven-file constructor/fallback/lifecycle audit, PR #474 direct route callers, Registry handshake files/tests, every compiled SyncRoot and inheritance consumer, Hosting and Connectors production/tests, the WebSocket SampleClient, and the obsolete and changed callback Verify snapshots. Later Tasks 5 and 6 own only first-party root/factory sequencing that already compiles at the Task 2 boundary. Task 2 authors every final test and oracle before production edits without a facade stub. Existing-surface rows run to semantic RED when they still compile; absent final public types establish project-level compilation RED only, and no semantic method-execution claim is made behind that failure. Source review covers the complete final-test manifest before implementation, one final GREEN proves it, and exact-path stage/status checks keep the atomic cutover out of later commits.

PR #419 ships only after focused and full non-integration suites, exact Public API snapshots, static allocation/lock review, independent review, and the agreed external stable-machine benchmark and Connector Tester handoff agree. No compatibility provider, fallback lifecycle adapter, alternate monitor, transient public failure, or public subject monitor survives the release.
