# Explicit Subject Ownership and Lifecycle Design

**Date:** 2026-08-18

**Status:** Revised after independent written-spec review; awaiting maintainer review

**Stack position:** PR 2 on `feature/effective-ownership-route`

**Pull request:** Complete rewrite of #419

## Purpose

Fallback contexts currently perform three different jobs: service composition, explicit lifecycle
attachment, and parent-context inheritance. One fallback mutation can therefore change service
resolution, lifecycle membership, object-graph traversal, reference counts, and cache invalidation
at the same time. The overlap also lets one subject participate in several independent lifecycle
domains, even though the normal and intended consumer model uses one configured context and one
lifecycle coordinator.

This pull request separates those responsibilities. Public fallback contexts become service
composition only. Explicit attachment and property inheritance use the ownership route introduced
by PR 1. Core owns compact per-subject state and atomic transition mechanics. Tracking owns
object-graph discovery, route-selection policy, lifecycle callbacks, and reconciliation.

The result is one effective ownership domain per subject, deterministic parent transfer, rejection
before an incompatible property value commits, and less fallback-specific lifecycle machinery.

## Concepts and Terms

- **Subject executor:** the per-subject `InterceptorExecutor`. It remains the subject's
  `IInterceptorSubjectContext`, stores subject-local services and caches, and owns the subject's
  compact ownership record.
- **Plain configured context:** a context created and configured independently of a subject. An
  explicit root attaches to one of these. Another subject's executor is not a valid explicit
  attachment target.
- **Object-reference graph:** relationships expressed by subject-valued properties, collections,
  and dictionaries. Cycles and repeated references are valid.
- **Explicit attachment:** an application-owned root relationship created by `AttachToContext` and
  released by `DetachFromContext`.
- **Parent membership:** a distinct parent property that currently references a subject. Repeated
  occurrences inside the same collection property form one membership, matching current reference
  count semantics.
- **Lifecycle ownership:** responsibility for coordinating one subject's attach, detach, handlers,
  and recursive graph transitions.
- **Ownership domain:** the plain configured context supplied to an explicit root. Its reference
  identity names the domain. Descendants retain that identity even when their effective route
  targets a parent executor.
- **Ownership coordinator:** the zero-or-one `ILifecycleInterceptor` resolved for an ownership
  domain. Tracking's `LifecycleInterceptor` is the built-in implementation.
- **Effective ownership route:** the single internal PR 1 route used for service resolution. An
  explicit attachment wins. Otherwise the earliest surviving compatible parent membership that
  can form an acyclic route wins.
- **Fallback composition:** a relationship created by `AddFallbackContext`. It aggregates services
  but creates no lifecycle membership and invokes no lifecycle callbacks.
- **Registry projection:** an optional observer of lifecycle and property changes. It indexes the
  graph but neither defines the graph nor owns subjects.

## Goals

- Give explicit root attachment a strict, direct API.
- Keep explicit attachment and property membership separate.
- Give every owned subject one ownership domain and at most one effective ownership route.
- Reject incompatible attachment or property assignment before it commits a backing value or
  ownership, route, lifecycle, registry, or reference-count state.
- Support multiple parents, repeated references, cycles, and deterministic route transfer.
- Preserve subject-local and branch-local nonunique services and interceptors.
- Preserve the normal single-context lifecycle callback sequence.
- Keep graph traversal and lifecycle policy in Tracking while Core owns atomic state mechanics.
- Remove functional context inheritance as an optional handler capability.
- Keep the ordinary scalar interception path allocation-free and at least as fast as `master`.
- Release every subject, context, property reference, reservation, and reconciliation entry when
  the final external ownership anchor disappears.

## Non-goals

- Enforcing every unique context authority. PR 3 generalizes the lifecycle rule to registry,
  transaction, hosting, and other audited authorities.
- Changing hosted-service start, stop, or drain semantics. PR 4 owns those changes.
- Removing fallback composition or nonunique branch services.
- Replacing or renaming `InterceptorExecutor`.
- Supporting optional shallow lifecycle tracking. `WithLifecycle()` always includes recursive
  property inheritance.
- Recovering arbitrary application side effects after a lifecycle callback violates its no-throw
  contract.
- Recomputing the complete object graph for every reference mutation.

## Public Model and API

### Explicit attachment

Core provides subject extension methods with these semantics:

```csharp
public static void AttachToContext(
    this IInterceptorSubject subject,
    IInterceptorSubjectContext context);

public static void DetachFromContext(
    this IInterceptorSubject subject,
    IInterceptorSubjectContext context);

public static IInterceptorSubjectContext? TryGetAttachContext(
    this IInterceptorSubject subject);
```

The interface parameter preserves existing generated and Dynamic constructor signatures. At entry,
`AttachToContext` requires the exact built-in plain `InterceptorSubjectContext` implementation. It
rejects an `InterceptorExecutor`, another subclass, or any unsupported
`IInterceptorSubjectContext` implementation before service resolution, route publication,
ownership mutation, or callbacks.

Explicit attachment is strict rather than idempotent:

- the first attachment succeeds;
- any second explicit attachment throws, including attachment to the same context;
- a successful detach permits a later new attachment;
- the first explicit attachment of a subject already inherited in the same exact domain succeeds,
  changes only the active route and external anchor, and emits no new subject-attach callbacks;
- attachment of an inherited subject to a different domain throws;
- a subject can have compatible parent memberships while explicitly attached, but the explicit
  route remains active.

`DetachFromContext` requires the exact context used for attachment. It throws when the subject has
no explicit attachment or when the supplied context differs. It removes only explicit ownership.
Compatible parent memberships may retain lifecycle ownership without a detach and attach callback
pair.

`TryGetAttachContext` reports only the explicit attachment. It returns `null` for a subject owned
solely through parent membership.

### Reference count

`GetReferenceCount` remains a Tracking extension because it describes object-reference graph
membership. Core stores the count in the subject's ownership record so the read is direct and does
not use `IInterceptorSubject.Data`.

The count includes distinct parent properties. It excludes explicit attachment and collapses
repeated occurrences of the same child inside one parent property. Index changes inside a retained
collection membership refresh lifecycle metadata without changing the count.

Every parent-membership addition or removal that survives reconciliation produces the existing
property-reference lifecycle change with the updated count. Concurrent structural commits may be
coalesced into their final net transition, as they are today. PR 2 does not add a revision journal
or promise one callback per superseded intermediate value. Only the transition between unowned and
owned produces `SubjectAttached` or `SubjectDetaching`. Route transfer by itself produces neither.

### Removed configuration surface

`WithContextInheritance()` is removed. `ContextInheritanceHandler` is removed as a functional and
configurable capability. `WithLifecycle()` always installs complete recursive lifecycle tracking.

This is intentional. A shallow lifecycle that attaches a parent while leaving its children in a
different service and registry state creates surprising partial graphs and no longer has a
supported first-party use case.

### Fallback composition

`AddFallbackContext` and `RemoveFallbackContext` retain their service-composition behavior and
return values. They no longer invoke `ILifecycleInterceptor`, attach subjects, detach subjects,
establish parent membership, or recurse into properties.

Fallbacks can still contribute nonunique services and interceptors. Repeated paths still
deduplicate the same service instance. Late nonunique additions remain legal and invalidate
dependent caches.

## Package Boundary

Core and Tracking communicate through a deliberate public provider contract. PR 2 does not add
friend-assembly access.

`ILifecycleInterceptor` remains the one public lifecycle authority contract and expands to include
prospective structural-write coordination. It inherits `IWriteInterceptor`, formalizing what the
built-in `LifecycleInterceptor` already implements. PR 2 does not add a second independently
configurable ownership service. Tracking's `LifecycleInterceptor` implements the complete
contract. Core uses that same instance as the captured lifecycle-authority identity, explicit
attach and detach coordinator, and terminal structural-write coordinator. The plain configured
context, not the coordinator instance, remains the ownership-domain identity.

The contract uses three public stack-only input contexts plus one controlled operation facade:

```csharp
public interface ILifecycleInterceptor : IWriteInterceptor
{
    void AttachSubjectToContext(
        ref SubjectAttachmentContext context,
        ref SubjectOwnershipOperation operation);

    void DetachSubjectFromContext(
        ref SubjectDetachmentContext context,
        ref SubjectOwnershipOperation operation);

    void PrepareSubjectPropertyWrite<TProperty>(
        ref SubjectOwnershipWriteContext<TProperty> context,
        ref SubjectOwnershipOperation operation);
}
```

The public operation inputs are stack-only and have these read contracts:

```csharp
public readonly ref struct SubjectAttachmentContext
{
    public IInterceptorSubject Subject { get; }
    public IInterceptorSubjectContext AttachContext { get; }
}

public readonly ref struct SubjectDetachmentContext
{
    public IInterceptorSubject Subject { get; }
    public IInterceptorSubjectContext AttachContext { get; }
}

public readonly ref struct SubjectOwnershipWriteContext<TProperty>
{
    public PropertyReference Property { get; }
    public TProperty FinalValue { get; }
    public long ExpectedRevision { get; }
}
```

Core also passes one stack-only `SubjectOwnershipOperation`. It is the only mutation facade:

```csharp
public ref struct SubjectOwnershipOperation
{
    public IInterceptorSubjectContext OwnershipDomain { get; }
    public object? ProviderState { get; set; }

    public SubjectOwnershipView GetView(IInterceptorSubject subject);

    public void ReserveExplicitAttachment(IInterceptorSubject subject);
    public void ReserveExplicitDetachment(IInterceptorSubject subject);

    public void ReserveParentAddition(
        IInterceptorSubject subject,
        PropertyReference parentProperty);

    public void ReserveParentRemoval(
        IInterceptorSubject subject,
        PropertyReference parentProperty);

    public void SelectActiveParent(
        IInterceptorSubject subject,
        PropertyReference? parentProperty);

    public void ReserveFinalRelease(IInterceptorSubject subject);

    // Called by attach and detach providers. The property terminal performs the
    // corresponding commit itself around the backing-field write.
    public bool TryCommit();
    public void PublishSelectedRoute(IInterceptorSubject subject);
    public void FinalizeSelectedRoute(IInterceptorSubject subject);
}
```

`ReserveParentAddition` derives the candidate route target from `parentProperty.Subject.Context`.
One parent property is one Core membership even when a collection contains the same child several
times. Tracking retains per-occurrence index metadata in its reconciliation state; Core does not.
`SelectActiveParent` accepts `null` only for an explicit route or final release already reserved in
the same operation. `TryCommit` returns true only when every reserved subject still has its
expected generation. On false, the provider returns without callbacks and Core retries from the
winning generation; the operation never partially commits. A true ownership conflict throws before
`TryCommit`. Property-write commit also requires the initiating revision to remain current and is
performed by the Core terminal, not the provider.
Route publication and finalization require a matching committed reservation and exact PR 1 route
descriptor. Misordered or repeated calls throw before changing state.

The read-only view and its allocation-free ordered membership enumerator are public provider
contracts:

```csharp
public readonly ref struct SubjectOwnershipView
{
    public bool Exists { get; }
    public IInterceptorSubjectContext? ExplicitAttachContext { get; }
    public IInterceptorSubjectContext? OwnershipDomain { get; }
    public PropertyReference? ActiveParentProperty { get; }
    public int ReferenceCount { get; }
    public long Generation { get; }
    public SubjectParentMembershipEnumerable ParentMemberships { get; }
}

public readonly struct SubjectParentMembership
{
    public PropertyReference ParentProperty { get; }
}

public readonly ref struct SubjectParentMembershipEnumerable
{
    public SubjectParentMembershipEnumerator GetEnumerator();
}

public ref struct SubjectParentMembershipEnumerator
{
    public SubjectParentMembership Current { get; }
    public bool MoveNext();
}
```

The enumerator exposes memberships in stable insertion order. Views and enumerators are valid only
for the synchronous operation generation that created them. Tracking uses them to implement direct
reference-count reads, deterministic transfer, and affected-component anchor detection. It cannot
replace an ownership record, construct a generation, publish an arbitrary route, or bypass the
operation's generation checks. `IInterceptorExecutor` adds this read contract so Tracking's
existing `GetReferenceCount` extension can read the Core-owned count without new friend access:

```csharp
int OwnershipReferenceCount { get; }
```

Reading the count for a never-used subject may create its normal lazy executor, but never creates
an ownership record.

For a structural property write, Core stores the committed operation token and provider-owned
reconciliation state in the by-reference `PropertyWriteContext<TProperty>`. The exact coordinator's
existing `WriteProperty` call receives control after `next`, obtains a fresh stack-only facade
through `context.TryGetSubjectOwnershipOperation(out var operation)`, reads `ProviderState`, updates
its baseline, and invokes callbacks. The method returns `false` when no structural operation
committed. Core recognizes the exact coordinator node in the compiled chain and wraps it in
`finally` so pending Core reservations and deferred route work are released even when a provider
violates the no-throw contract. This wrapper does not add another interceptor or change the
coordinator's ordering identity.

The write context exposes the operation only during that exact committed unwind:

```csharp
public bool TryGetSubjectOwnershipOperation(
    out SubjectOwnershipOperation operation);
```

The contract is an advanced provider API:

- at most one distinct coordinator may resolve in one active effective context;
- that exact coordinator instance occurs exactly once in the ordered write chain;
- calls are synchronous;
- `WriteProperty` calls `next` at most once and performs postcommit reconciliation during unwind;
- implementations must not throw from lifecycle callbacks;
- implementations must not retain an operation context, view, enumerator, or reservation beyond
  the synchronous call;
- application code normally configures the built-in coordinator through `WithLifecycle()`.

Core already grants Tracking friend access for commit revision and raw timestamp propagation. PR 2
adds no new use. Removing those two existing uses and the friend declaration is explicitly deferred
to a focused follow-up so PR 2 does not expose unrelated raw storage encoding or enlarge its public
surface further.

## Core Ownership State

Every `InterceptorExecutor` has one nullable ownership field:

```text
SubjectOwnershipState?
  ExplicitAttachment?
  FirstParentMembership
  AdditionalParentMemberships?
  ActiveParentMembership?
  OwnershipDomain
  LifecycleCoordinator?
  ReferenceCount
  Generation
  PendingTransition
```

The field remains `null` until the subject first participates in explicit or inherited ownership.
When the final attachment, parent membership, and transition disappear, Core clears the field back
to `null`.

The common first parent membership is stored inline. An ordered overflow collection is allocated
only for a second distinct parent property. The collection preserves insertion order so transfer is
deterministic. A membership records only the source property, candidate route target, ownership
domain, and transition identity. Core does not traverse property values or interpret registry
state.

The active membership refers to one of the recorded memberships. Its PR 1 route descriptor is the
exact publication generation. A stale detach or transfer can change the route only when both the
ownership generation and route descriptor still match.

Explicit attachment is a separate field, not a zero-property sentinel and not part of the
reference count.

### Domain activation

The first explicit root activates its plain configured ownership domain and captures the resolved
zero-or-one lifecycle coordinator, including the valid no-coordinator case. Several disconnected
roots may share the same activated domain.

An activated plain context uses a derived immutable context state that references one lazy
`ContextAuthorityActivation` record. Route-free, inactive contexts retain the PR 1 state shape.
The record contains:

```text
ContextAuthorityActivation
  Status: Activating | Active | Releasing
  LifecycleCoordinator: exact instance or null
  ExplicitRootLeaseCount
  Generation
  TransitionToken
```

`ExplicitRootLeaseCount` counts explicit anchors in this exact domain, not subjects. The record is
removed after the last explicit anchor and every ownership transition from that anchor has
quiesced. PR 3 extends the authority value stored in this same permanent record rather than
replacing the record or its synchronization.

Every context-state replacement, including cache invalidation through `WithoutCaches()`, preserves
the exact activation record just as PR 1 preserves an exact ownership-route descriptor. An inactive
plain context uses the base state and pays no activation-record allocation.

Every owned subject pins the captured coordinator identity, including `null`, across its complete
effective context. A service, fallback, or ownership-route mutation that would change that identity
for any active downstream subject is rejected before publication. Adding another path to the same
coordinator instance remains valid. Capturing `null` prevents adding the first coordinator anywhere
in that active effective cone. The cold mutation path may walk active reverse dependencies;
steady-state resolution does not. Non-lifecycle service mutation remains legal.

The activation record is lazy and permanent infrastructure that PR 3 can extend for the complete
unique-authority map. PR 2 must not add a lifecycle-only compatibility bridge that PR 3 removes.

### Authority publication gate

One permanent Core gate linearizes only authority-relevant publication:

- domain activation and final release;
- ownership reservation publication and cancellation;
- ownership-route publication, transfer, and clear;
- service and fallback state publication whose prospective state must be checked against active
  domains.

The gate is never entered by cached service resolution, intercepted reads, scalar writes, method
invocations, lifecycle callbacks, reconciliation, property getters, or target code. Context-local
`TryAddService` predicates and factories retain their current behavior under that context's
mutation lock and run before the publication gate is entered. The publisher re-reads the state,
validates the complete prospective authority result, and publishes while holding both locks.

The order for context mutation is the existing context mutation lock followed by the authority
publication gate. A holder of the publication gate never waits for another context's mutation lock.
Multi-context ownership work publishes a pending activation token first, then reserves each subject
under the publication gate and its existing `SyncRoot` one at a time. It never holds several
subject locks simultaneously. Later phases match the token and generation. This two-phase protocol
allows the implementation to release one local lock before entering another without exposing an
unprotected interval.

Ownership-ledger reservation uses publication-gate-then-`SyncRoot` order. Ownership-route state
uses the context-publisher order instead: the committed ownership token first authorizes one exact
route descriptor, then the executor takes its own context mutation lock followed by the publication
gate and publishes that descriptor without holding `SyncRoot`. No publication-gate holder acquires
a context mutation lock.

Reverse dependency registration and the prospective context state are both stable while the gate
is held. Therefore adding a fallback cannot race a coordinator mutation in its target: one
publication wins, and the loser validates against that winner. A mutation of a deeper target walks
the reverse dependency graph under the same gate and compares the prospective coordinator with
every active domain it reaches. The walk uses pooled, cycle-aware buffers and occurs only on the
cold mutation path.

Domain activation follows this state table:

| Current state | Operation | Result |
|---|---|---|
| Inactive | First valid explicit attach | Publish `Activating` with one lease and captured coordinator |
| Activating | Same transition token | Continue reservation or commit |
| Activating | Competing attach or authority mutation | Observe the generation and retry after it changes |
| Active | Compatible explicit attach | Increment lease count |
| Active | Mutation preserving captured coordinator identity | Publish normally |
| Active | Mutation changing captured coordinator identity | Reject before publication |
| Active | Final explicit-anchor release | Publish `Releasing` until ownership cleanup quiesces |
| Releasing | Same transition token | Complete cleanup and remove the activation record |
| Releasing | Competing activation or mutation | Observe the generation and retry after it changes |

Retry is library controlled around a short publication phase. It never blocks while a user
callback, service factory, or property getter is running.

## Tracking Responsibilities

Tracking owns policy and graph knowledge:

- classify structural properties;
- enumerate subject references in objects, collections, dictionaries, and read-only enumerable
  shapes;
- maintain the committed property-value reconciliation baseline;
- discover affected subtrees with pooled, cycle-aware worklists;
- select the earliest compatible acyclic parent route;
- identify anchored and unanchored cyclic components;
- propose batched Core ownership reservations;
- invoke lifecycle handlers, subject handlers, events, and property handlers in characterized
  order;
- update parent and registry projections through their existing handlers;
- clean reconciliation entries after final detach.

Core is the source of truth for ownership membership. Tracking dictionaries are reconciliation and
callback projections, not independent ownership ledgers.

## Structural Property Write Protocol

A structural assignment must validate the final proposed value before the generated backing field
changes. A post-commit handler cannot provide that guarantee.

### Compiled write seam

Core resolves the zero-or-one ownership coordinator while compiling the cached write chain. It does
not perform a service lookup on each write.

The ordinary scalar Core terminal remains unchanged when no coordinator is present. Known scalar
property types do not enter the ownership-aware terminal. Potentially structural shapes use a
specialized terminal that calls the public coordinator contract immediately before the backing
write.

The generated `_context is null` fast path needs one narrow handshake because an unowned subtree
can be reserved for attachment while one of its descendants is being changed. The generator emits
a conservative compile-time `canContainSubjects` flag for each property setter:

- scalar unowned setters retain the existing direct write with no lock, branch, or allocation
  beyond today's `_context` check;
- a potentially structural unowned setter locks the subject's already-existing `SyncRoot`,
  re-reads `_context`, and writes directly only when it is still null;
- attachment publishes the subject's executor and pending reservation through that same
  `SyncRoot` before it reads the subject's structural properties;
- once the executor exists, the setter leaves the null-context path and the ownership-aware
  terminal observes the pending reservation.

A setter that acquired `SyncRoot` first commits before discovery reads it. An attachment that
acquired it first publishes the executor and reservation before the setter can proceed. No
structural mutation can therefore land between final subtree validation and the parent commit.
This changes only unowned potentially structural writes. Attached writes already synchronize on
`SyncRoot`, and unowned scalar initialization remains unchanged. Dynamic and non-generic property
entry points already create or receive an executor and use the same terminal protocol.

The existing `LifecycleInterceptor` remains in its characterized `IWriteInterceptor` position so
custom and built-in write interceptor ordering does not change. The terminal hook performs only
prospective ownership reservation and commit. Lifecycle reconciliation occurs when the existing
interceptor unwinds.

### Transition sequence

For a potentially structural write:

1. Outer write interceptors run normally and may suppress or transform the value.
2. At the terminal boundary, Tracking sees the final proposed value and compares it with its last
   committed reconciliation baseline.
3. Tracking discovers additions, removals, transfers, and affected descendants with pooled
   worklists. Before reading a newly discovered subject's structural properties, Core publishes
   that subject's executor and reserves it against its current generation.
4. Discovery completes only after every affected subject is reserved without publishing a route or
   lifecycle membership.
5. A stale baseline or generation cancels the reservation and retries against the winning commit.
   A true ownership-domain conflict cancels the reservation and throws. In both cases the backing
   property is still unchanged for the rejected or retried attempt.
6. The terminal performs the backing-field write under the existing subject synchronization.
7. If the backing write throws, Core cancels every reservation and publishes no ownership change.
8. Core commits the write revision and the reserved ownership ledger. Effective route visibility
   remains unchanged until the characterized lifecycle-handler phase.
9. `LifecycleInterceptor` records the committed reconciliation baseline before invoking callbacks,
   then reconciles the transition under its lifecycle synchronization.
10. At its former `ContextInheritanceHandler` position, attach publishes the selected route before
    recursive descent; detach performs recursive descent before clearing or transferring the route.
11. It continues the established lifecycle sequence without holding Core ownership-transition
    locks.
12. Core finalizes any deferred route work in `finally`, so a callback contract violation cannot
    strand the ownership record on the old route.
13. A concurrent later commit may supersede reconciliation work. Only the latest committed value
    becomes the stable baseline, and every completed setter returns after its commit has either
    been reconciled or proven superseded.

Reservation handles are compact Core values. Tracking owns and pools any multi-subject batch
buffers. Scalar writes allocate neither.

Structural write state follows this table:

| Current state | Operation | Result |
|---|---|---|
| Unowned | Compatible parent addition | Reserve domain, membership, and selected route |
| Unowned | Incompatible child or descendant | Reject before backing-field commit |
| Owned in domain A | Parent addition from domain A | Reserve membership; select only when needed |
| Owned in domain A | Parent addition from another domain | Reject before backing-field commit |
| Owned | Non-active parent removal | Reserve membership removal only |
| Owned | Active parent removal with replacement | Reserve deterministic transfer |
| Owned | Final external-anchor removal | Reserve affected-component release |
| Any stable state | Stale expected generation or revision | Cancel the complete batch and retry |
| Any reserved state | Competing library transition | Observe the winning generation and retry |
| Any committed state | Superseded before reconciliation | Reconcile the final net transition |

### Direct collection mutation

The prospective terminal protocol applies to property assignment. Directly mutating an ordinary
collection instance without passing through the intercepted property setter remains outside the
lifecycle protocol, as it is today. PR 2 does not add collection proxies or claim that an unrelated
`List<T>.Add` passed through the property terminal. Consumers use property replacement or an
explicit property write when they need lifecycle-visible collection changes.

## Explicit Attach and Detach Protocol

### Attach

`AttachToContext` performs this sequence:

1. Validate that the supplied target is a supported plain configured context.
2. Validate that the subject has no existing explicit attachment.
3. Resolve and validate the prospective effective zero-or-one lifecycle coordinator before any
   factory, route, membership, or callback side effect.
4. Activate or join the configured ownership domain.
5. When a coordinator exists, discover the complete current subtree incrementally. Publish and
   reserve each discovered subject through its structural-write handshake before reading its own
   structural properties.
6. Reject an incompatible descendant by cancelling all reservations. No route, ledger entry,
   callback, registry entry, or property write remains.
7. Commit the explicit anchor and reserved ownership ledger. Publish the explicit root route.
8. Invoke lifecycle callbacks in the characterized order. Each inherited route becomes visible
   only when callback iteration reaches the coordinator's former `ContextInheritanceHandler`
   position for that subject, immediately before its recursive descent.
9. A same-domain inherited subject that merely gains an explicit anchor emits no new attach
   callbacks.

A no-coordinator context attaches only the explicit subject route. It performs no graph traversal
or lifecycle callback. The captured `null` coordinator cannot change while that attachment is
active.

### Detach

`DetachFromContext` first validates the exact explicit context and reserves the transition.

If no compatible parent membership survives, lifecycle detach runs while the old route remains
resolvable. Core clears the explicit record and route after the required detach callbacks, including
from a `finally` path when application callback code violates the no-throw contract. It then removes
the domain lease and releases the ownership state when empty.

If a compatible parent membership survives, Core transfers the effective route to that parent.
The subject remains in the same lifecycle domain, so no final `SubjectDetaching` or new
`SubjectAttached` callback occurs. Service resolution may change because the explicit configured
route and inherited parent branch can contribute different nonunique services. Core publishes a
new context state and invalidates affected compiled chains. The route switch is the explicit-detach
linearization point and completes before `DetachFromContext` returns.

An explicit root remains owned when the application merely drops its last CLR reference. The
application must call `DetachFromContext` to release explicit ownership.

Explicit transitions follow this table:

| Current state | Operation | Result |
|---|---|---|
| No explicit anchor | Attach to valid plain context | Reserve and commit one explicit anchor |
| Explicitly attached | Any attach, including same context | Reject before mutation |
| Inherited in same domain | Attach to that exact domain | Add anchor and select explicit route without lifecycle churn |
| Inherited in another domain | Attach | Reject before mutation |
| No explicit anchor | Detach | Reject before mutation |
| Explicitly attached | Detach with different context | Reject before mutation |
| Explicitly attached with compatible parent | Exact detach | Remove anchor and transfer route without lifecycle churn |
| Explicitly attached without surviving parent | Exact detach | Run final detach with old route visible, then clear |
| Any transition | Stale token or generation | Leave the winner intact and retry or report its strict result |

## Parent Membership and Route Selection

### Compatibility

An unowned child is compatible with the parent's ownership domain. An already owned child is
compatible only when its exact configured ownership-domain identity matches. Merely sharing the
same coordinator instance is insufficient.

The complete proposed child subtree is checked. A compatible root with an incompatible descendant
is rejected before the parent backing property changes.

Fallback composition does not establish compatibility and is not an ownership anchor.

### Deterministic selection

Explicit attachment supplies the active route whenever present. Otherwise Tracking selects the
earliest surviving compatible parent membership that does not create an ownership-route cycle.

Secondary compatible memberships are recorded but do not churn the route. Adding or removing one
is O(1) in the common case. Removing the active membership scans the subject's remaining ordered
memberships and ancestry until it finds a valid replacement.

### Cycles and repeated references

The complete object-reference graph may contain cycles and repeated references. The active
ownership routes form an acyclic forest over that graph:

- back-edges remain valid parent memberships and registry relationships;
- a back-edge does not become an active route when that would make a route cycle;
- several occurrences of one child in the same parent property remain one membership;
- different parent properties remain distinct memberships;
- removing one of several references does not detach the child;
- removing the active parent transfers to a surviving compatible membership when possible.

Reference count alone does not keep an unanchored cycle alive. When the final explicit or external
route anchor disappears, Tracking walks only the affected component. If no explicit root or
incoming membership from outside the component remains, it detaches the complete component and
clears every internal membership and route despite nonzero internal reference counts.

Full component traversal is therefore limited to attach, final detach, active-route transfer, and
possible anchor loss. Ordinary reads, scalar writes, service resolution, and compatible secondary
membership changes do not walk the graph.

## Lifecycle Traversal and Callback Order

`WithLifecycle()` registers one `LifecycleInterceptor` that performs recursive traversal itself.
The interceptor also implements `ILifecycleHandler` and occupies the existing lifecycle-handler
ordering phase. Its handler invocation performs the descendant transition directly. Ordering
attributes that currently target `ContextInheritanceHandler` move to `LifecycleInterceptor`.

During attach handler iteration, handlers ordered before the coordinator keep seeing the child's
pre-inheritance local context. Reaching the coordinator publishes the selected route and performs
descendant traversal. Handlers ordered after it and the subject handler see the inherited route and
keep their current deepest-first observations.

During final detach, `SubjectDetaching`, the subject handler, and service handlers ordered before
the coordinator see the old route. Reaching the coordinator performs descendant traversal and then
clears or transfers the route at the same phase where `ContextInheritanceHandler` currently removes
the fallback. Handlers ordered after it see the new route or the route-free subject. A compatible
property detach that selects another parent uses that same phase. An explicit-to-inherited transfer
has no lifecycle callback sequence and publishes its route atomically as part of explicit detach.

The coordinator is not optional and cannot be registered twice. `ContextInheritanceHandler` is no
longer registered or functional. Its public type is removed in this coordinated breaking release;
custom ordering attributes migrate from that type to `LifecycleInterceptor`.

The built-in coordinator owns the lifecycle-handler dispatch loop. It resolves the ordered handler
array containing its own `ILifecycleHandler` identity and, when iteration reaches that exact
instance, invokes its internal recursive seam with the live stack-only ownership operation instead
of making an ordinary handler callback. This preserves the ordering position without ambient
suppression, a second service instance, or a retainable operation facade. All other handlers are
invoked normally. Attach, detach, structural writes, and direct collection reconciliation enter the
same dispatch helper with a live Core operation.

The implementation must preserve checked-in characterization for:

- service lifecycle handler order;
- subject lifecycle handler order;
- `SubjectAttached` and `SubjectDetaching` placement;
- property lifecycle handler placement;
- registry-before-parent relationships;
- attach and detach descent direction;
- prepopulated explicit-root subtrees;
- derived-property initialization relative to lifecycle callbacks.

The normal single-context sequence is a compatibility contract. Live-state checks may stop a stale
reentrant callback tail after membership changes, but they may not reorder an ordinary successful
sequence.

Callback-time service visibility is pinned explicitly:

| Phase | Attach visibility | Detach visibility |
|---|---|---|
| `SubjectAttached` / `SubjectDetaching` | Selected inherited or explicit route | Old route |
| Subject `ILifecycleHandler` | Inherited route | Old route |
| Service handler before coordinator | Local or previous route | Old route |
| Coordinator and recursive descent | Publish selected route | Traverse, then clear or transfer |
| Service handler after coordinator | Selected inherited route | New route or no route |
| Property lifecycle handlers | Existing characterized post-subject phase | Existing characterized pre-final-release phase |

## Concurrency

Each subject ownership record has a monotonic generation and at most one pending transition.
Reservations compare the expected generation before commit. A later generation cannot be cleared
or transferred by an older operation, including a later route that uses the same target.

Structural transitions reserve all affected subjects before the backing property commits.
Reservation acquisition never executes callbacks and never holds several subject synchronization
locks while waiting. Competing library transitions retry or observe the winning committed
generation. A true ownership-domain incompatibility throws; ordinary contention does not become a
spurious incompatibility.

Tracking uses its lifecycle synchronization for reconciliation and callback ordering. Core route
publication uses the PR 1 context mutation protocol plus the authority publication gate. The exact
orders are:

1. a context publisher takes its one context mutation lock, completes any existing
   `TryAddService` predicate or factory work, then takes the authority publication gate for
   validation and publication;
2. an ownership publisher takes the authority publication gate, then at most one subject
   `SyncRoot`, publishes or cancels that subject's reservation, and releases both before moving to
   another subject;
3. Tracking takes its lifecycle synchronization only after Core publication locks have been
   released.

No path holding the authority publication gate waits for another context mutation lock. Generated
backing-field writers retain the existing rule that they perform only the field write while
`SyncRoot` is held. User callbacks, events, lifecycle handlers, reconciliation, property getters,
and service factories never run under the authority publication gate or a Core ownership
reservation lock. `TryAddService` predicates and factories continue to run under their one existing
context mutation lock, exactly as documented today.

Subtree discovery alternates short reserve phases with unlocked reads: reserve one subject, release
Core locks, read that subject's structural properties, then reserve each discovered child before
reading it. A reserved subject's structural setters observe its published executor and pending
transition, so the snapshot cannot change behind the walk. A stale generation restarts discovery
from the committed winner without retaining partial reservations.

Deterministic schedules must pin at least these seams:

- a structural write inside a reserved but previously unowned descendant;
- first executor publication racing a null-context structural setter;
- explicit activation racing a lifecycle service mutation;
- fallback publication racing a coordinator mutation in its target or deeper cone;
- two structural commits where the first is superseded before reconciliation;
- callback-time route visibility before, at, and after the recursive lifecycle phase;
- stale attach, detach, transfer, and final-component release using the same route target but a
  later descriptor generation.

Quiescent invariants are:

1. every attached subject has exactly one ownership domain;
2. every inherited subject has exactly one active acyclic route unless explicit attachment wins;
3. every recorded parent membership corresponds to a committed parent property baseline;
4. the Tracking reconciliation baseline matches the latest committed structural value;
5. registry and parent projections agree with ownership after callbacks settle;
6. no pending reservation or stale route descriptor survives quiescence;
7. an unanchored component has no ownership record, route, or lifecycle projection.

## Failure Semantics

Expected contract failures are fail-fast with no library-owned commit:

- invalid explicit target;
- duplicate explicit attachment;
- missing or wrong-context explicit detach;
- more than one prospective lifecycle coordinator;
- lifecycle coordinator mutation on an active domain;
- incompatible property child or descendant.

For property assignment, these failures happen before the backing property, ownership ledger,
route, lifecycle projection, registry projection, or reference count changes. For explicit
attachment, they happen before route publication and lifecycle callbacks.

Complete-subtree validation may initialize the normal lazy executor on a traversed subject so its
structural setters can participate in reservation. A rejected operation does not unpublish that
executor: it is retained only by its own subject, contains no ownership record or route, and creates
no external lifetime edge. Executor initialization is therefore infrastructure preparation, not a
lifecycle or ownership commit.

The final proposed property value is incompatible when the child or any reachable descendant is
already owned by a different exact configured-context domain, or when its branch composition would
change the lifecycle coordinator identity captured by the parent's domain. An unowned subtree, a
subtree already owned by the same exact domain, repeated references, same-domain multiple parents,
and same-domain cycles remain compatible. Sharing one coordinator instance does not make two plain
configured contexts the same ownership domain.

Ownership validation runs at the terminal because preceding write interceptors may transform
`NewValue`. A custom outer interceptor may therefore observe or externally record a write attempt
before the terminal rejects it. Arbitrary custom interceptor side effects are not transactional and
cannot be rolled back. Moving validation ahead of those interceptors would validate the wrong value
and change existing interceptor order. Built-in interceptors must defer ownership, lifecycle, and
registry mutation until the terminal accepts the final value.

Lifecycle handlers and events are synchronous no-throw contracts. Built-in implementations must
catch or otherwise isolate expected operational failures so they uphold that contract. If
application callback code nevertheless throws, its exception propagates. The property and
ownership commit is not rolled back, deferred Core route finalization still runs, and arbitrary
callback side effects may be partial. PR 2 does not add fault states, callback compensation, or
retry machinery for contract violations.

## Memory Lifetime

Final release clears all strong references introduced by ownership:

- explicit attachment context;
- active and secondary parent memberships;
- PR 1 route descriptor and reverse using-context entry;
- lifecycle coordinator and ownership-domain references;
- Tracking reconciliation baselines;
- registry and parent projection entries;
- pending reservations and pooled batch contents.

An installed ownership route intentionally retains its source executor through the target's reverse
dependency entry, and the executor retains its subject. This is what keeps an explicitly attached
root alive until explicit detach. After final route removal, no ownership infrastructure may retain
the executor or subject. Tracking must not use a permanent subject-keyed ownership dictionary as
the source of truth. Any callback or reconciliation projection keyed by a subject is removed on
final detach.

Weak-reference tests cover an implicitly detached child, an explicitly retained and then detached
root, a released cyclic component, a multi-parent final detach, and failed or superseded
reservations.

## Consumer Migration

The runtime and all first-party producers migrate in one pull request:

- generated context-taking constructors call `AttachToContext`;
- `DynamicSubject` context-taking construction uses explicit attachment;
- HomeBlaze and first-party application roots attach explicitly and detach at their ownership
  boundary;
- connector, subject-update, and OPC UA child factories create route-free children, publish them
  through the parent property, and verify the committed child before recursive population;
- paths that intentionally create independent roots attach explicitly;
- custom `ILifecycleInterceptor` providers implement the expanded prospective coordination
  contract and its synchronous no-throw rules;
- old generated model assemblies must be rebuilt;
- tests that used fallback mutation as lifecycle shorthand move to explicit APIs.

The migration audit includes every `new Subject(context)`, `AddFallbackContext`,
`RemoveFallbackContext`, `WithContextInheritance`, and direct `ContextInheritanceHandler`
registration in production, tests, samples, and documentation.

## Documentation

`docs/interceptor.md` becomes the canonical user-facing explanation of:

- subject executors and plain configured contexts;
- explicit attachment;
- parent membership and reference counts;
- ownership domains and active parent transfer;
- composition-only fallbacks;
- registry projection;
- strict errors and migration examples.

Affected feature documents start with a short introduction, a local terms list, and a contract at
a glance. They link to `docs/interceptor.md` rather than repeating the complete model.

`docs/design/tracking-lifecycle.md` is rewritten around the Core ownership record, prospective
reservation, route forest, reconciliation, callback phase, concurrency, and memory release. It no
longer describes fallback mutation or `ContextInheritanceHandler` as lifecycle machinery.

## Performance Contract

Performance must be the same as or better than `master` for the normal one-global-context use case.
A repeatable regression requires redesign, not an assumption that the new semantics justify it.

Required properties are:

- a never-owned subject allocates no ownership record;
- the common single parent is inline and allocates no membership collection;
- reference counts no longer use `IInterceptorSubject.Data` or boxed integers;
- the ordinary scalar Core write terminal is unchanged without a coordinator;
- unowned scalar setters retain their current direct fast path;
- only unowned potentially structural setters use the new `SyncRoot` handshake;
- known scalar writes perform no ownership dispatch, reservation, or graph work;
- cached reads, method calls, and service resolution add no ownership allocation;
- the authority publication gate is absent from every stable resolution and scalar-interception
  path;
- compatible secondary-parent changes do not traverse the complete graph;
- structural traversal uses reusable pooled buffers;
- stable effective routes do not invalidate caches;
- only route changes and legal service topology changes invalidate dependent states.

Local development performs static field-layout, allocation, chain-shape, lock, and invalidation
analysis. Local benchmark timings are diagnostic only. Before the pull request is declared ready,
the maintainer is asked to run the agreed comparisons on the stable benchmark machine against both:

- exact stacked PR 1 base `169672c3ca496e8338f1a6be62d5e900c8e605ad`;
- exact design-time `master` `4eb5fc132fef55d0277b13d585d8b611737b23db`.

The filters include the normal registry and context-depth suites plus a focused unowned structural
initialization case that exercises the new null-context handshake. Results must show no repeatable
regression outside control-row noise for the normal one-global-context workloads and no new
steady-state allocation. Any signal above noise, including an unacceptable construction cost from
the structural handshake, reopens the design. The maintainer is asked before the external handoff;
development-machine numbers never accept the change.

## Test and Verification Design

Core tests cover:

- plain target validation;
- strict first attach, duplicate attach, exact detach, and reattach;
- explicit context reporting;
- lazy ownership allocation and final clearing;
- zero and one coordinator activation;
- rejection of coordinator mutation after activation;
- activation racing direct service addition, conditional service creation, fallback addition, and
  mutation of a fallback target;
- inline and overflow parent membership behavior;
- exact generation, stale cancel, stale clear, and route transfer;
- fallback composition without lifecycle callbacks;
- complete public provider facade, view, enumerator, reference-count, and write-context API
  snapshots;
- Core-to-Tracking package-boundary compilation without new friend access.

Generator and Dynamic tests cover:

- a scalar null-context setter retaining the existing direct generated shape;
- a potentially structural null-context setter locking `SyncRoot` and re-reading `_context`;
- first executor publication racing that setter in both orders;
- generated and Dynamic context-taking constructors using strict explicit attachment;
- no new steady-state allocation after an executor is present.

Tracking tests cover:

- first parent attach and final detach;
- several parents in one domain;
- repeated collection occurrences and index refresh;
- deterministic earliest-parent selection and transfer;
- explicit-to-inherited transfer without lifecycle churn;
- cross-domain root, child, and descendant rejection before property commit;
- a reserved unowned child accepting no later incompatible descendant before the parent commit;
- cycles with one anchor, several anchors, and final anchor removal;
- shared DAGs and back-edge route selection;
- callback-order and service-visibility characterization before, at, and after the recursive phase
  for attach, detach, registry, parents, properties, and derived initialization;
- superseded structural commits producing the current final net reconciliation rather than a
  per-commit callback journal;
- a minimal custom `ILifecycleInterceptor` proving prepare, postcommit unwind, and Core `finally`
  finalization through the complete public provider contract;
- callback contract violations without rollback machinery;
- concurrent write, reparent, attach, detach, and stale-reservation schedules;
- weak-reference memory release.

Consumer verification includes generator snapshots, Dynamic, Registry, Hosting, Connectors,
non-integration OPC UA, HomeBlaze Services, public API snapshots, the full non-integration solution
suite, solution build, and pack.

Because connector-created child publication changes, the stable-machine handoff includes the
agreed Connector Tester matrix in addition to benchmarks: 100 cycles per connector, every rotating
chaos profile, and server plus both-client structural mutation at rate 1. Long-running benchmark
and Connector Tester work is not treated as authoritative on the development machine.

Tests follow repository naming and Arrange, Act, Assert conventions. Concurrency tests use
barriers, events, or other deterministic synchronization and never hardcoded sleeps.

## Capability Changes

PR 2 removes these capabilities:

- fallback mutation as implicit attach or detach;
- several explicit root attachments for one subject;
- explicit attachment to a subject executor;
- optional shallow lifecycle without recursive inheritance;
- several lifecycle coordinators in one effective ownership context;
- one subject participating in unrelated ownership domains;
- temporary publication of an incompatible child;
- aggregation of every parent branch as an ownership route;
- retaining an unanchored cycle solely through internal reference counts.

PR 2 preserves:

- pure fallback service composition;
- nonunique branch services and interceptors;
- late nonunique service additions and invalidation;
- multiple compatible parents;
- repeated object-reference paths;
- cycles and shared DAGs;
- several disconnected explicit roots in one ownership domain;
- subject-local executor services;
- current single-context callback ordering.

## Release Boundary

PR 2 is independently releasable on PR 1 as a coordinated breaking release. Core, Tracking, source
generation, Dynamic, Registry, connector child factories, OPC UA loaders, HomeBlaze migrations,
tests, and affected documentation ship together.

The pull request contains no unique-authority framework beyond the permanent lifecycle activation
seam and no hosting state-machine changes. PR 3 generalizes authority stability without replacing
the ownership ledger. PR 4 consumes the completed ownership and authority contracts.

The old #419 production implementation is not transplanted wholesale. Reproduced schedules,
callback-order characterization, and focused regression tests may be reused. The finished pull
request is reviewed both alone and stacked with PR 1.
