# Explicit Subject Ownership and Lifecycle Design

**Date:** 2026-08-18

**Status:** Approved for written-spec review

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
- Reject incompatible attachment or property assignment before it creates observable state.
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
subject.AttachToContext(context);
subject.DetachFromContext(context);
var context = subject.TryGetAttachContext();
```

`AttachToContext` accepts an `InterceptorSubjectContext` that is not an `InterceptorExecutor`. It
rejects another subject's executor or an unsupported `IInterceptorSubjectContext` implementation
before service resolution, route publication, ownership mutation, or callbacks.

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

Every distinct committed parent-membership addition or removal produces the existing property
reference lifecycle change with the updated count. Only the transition between unowned and owned
produces `SubjectAttached` or `SubjectDetaching`. Route transfer by itself produces neither.

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
prospective structural-write coordination. PR 2 does not add a second independently configurable
ownership service. Tracking's `LifecycleInterceptor` implements the complete contract, and Core
uses that same instance as the ownership-domain identity, explicit attach and detach coordinator,
and terminal structural-write coordinator.

The contract uses three public stack-only operation facades:

```csharp
public interface ILifecycleInterceptor
{
    void AttachSubjectToContext(ref SubjectAttachmentContext context);

    void DetachSubjectFromContext(ref SubjectDetachmentContext context);

    void PrepareSubjectPropertyWrite<TProperty>(
        ref SubjectOwnershipWriteContext<TProperty> context);
}
```

Core constructs each facade and owns its mutable reservation state. The attachment facades expose
the subject, exact configured context, domain, and controlled reserve, commit, or finalize
operations needed for complete-subtree coordination. The write facade exposes the property, final
proposed value, and operations to reserve additions and removals for a child and source property.
Tracking enumerates the affected subtree and requests those changes. It cannot replace the
ownership record, publish a route directly, or bypass generation checks. A facade cannot be
retained beyond its synchronous method call.

The contract is an advanced provider API:

- at most one distinct coordinator may resolve in one active effective context;
- calls are synchronous;
- implementations must not throw from lifecycle callbacks;
- implementations must not retain an operation context or reservation beyond the call;
- application code normally configures the built-in coordinator through `WithLifecycle()`.

Core already grants Tracking friend access for commit revision and raw timestamp propagation. PR 2
adds no new use. The implementation should replace those two uses with small cohesive read-only
public contracts and remove the friend declaration if that can be done without exposing raw
storage encoding or materially enlarging the pull request. Otherwise the two existing uses remain
and their removal is recorded as a focused follow-up.

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

Every owned subject pins the captured coordinator identity, including `null`, across its complete
effective context. A service, fallback, or ownership-route mutation that would change that identity
for any active downstream subject is rejected before publication. Adding another path to the same
coordinator instance remains valid. Capturing `null` prevents adding the first coordinator anywhere
in that active effective cone. The cold mutation path may walk active reverse dependencies;
steady-state resolution does not. Non-lifecycle service mutation remains legal.

The activation record is lazy and permanent infrastructure that PR 3 can extend for the complete
unique-authority map. PR 2 must not add a lifecycle-only compatibility bridge that PR 3 removes.

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

The ordinary Core terminal remains unchanged when no coordinator is present. Known scalar generic
property types do not enter the ownership-aware terminal. Potentially structural shapes use a
specialized terminal that calls the public coordinator contract immediately before the backing
write.

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
   worklists.
4. Core reserves every affected subject against its current generation without publishing a
   route or lifecycle membership.
5. A stale baseline or generation cancels the reservation and retries against the winning commit.
   A true ownership-domain conflict cancels the reservation and throws. In both cases the backing
   property is still unchanged for the rejected or retried attempt.
6. The terminal performs the backing-field write under the existing subject synchronization.
7. If the backing write throws, Core cancels every reservation and publishes no ownership change.
8. Core commits the write revision and the reserved transition generation. A new route may now be
   published for attach. A route needed by detach callbacks remains visible until those callbacks
   finish.
9. `LifecycleInterceptor` records the committed reconciliation baseline before invoking callbacks,
   then reconciles the transition under its lifecycle synchronization.
10. It invokes the established lifecycle sequence without holding Core ownership-transition locks.
11. It finalizes deferred route clear or transfer in `finally`, so a callback contract violation
    cannot strand the Core ownership record on the old route.
12. A concurrent later commit may supersede reconciliation work. Only the latest committed value
    becomes the stable baseline, and every completed setter returns after its commit has either
    been reconciled or proven superseded.

Reservation handles are compact Core values. Tracking owns and pools any multi-subject batch
buffers. Scalar writes allocate neither.

### Direct collection mutation

Direct in-place collection mutation continues to use the existing collection and lifecycle APIs.
The prospective terminal protocol applies to property assignment. It does not pretend that a
collection changed through an unrelated API passed through the property setter.

## Explicit Attach and Detach Protocol

### Attach

`AttachToContext` performs this sequence:

1. Validate that the supplied target is a supported plain configured context.
2. Validate that the subject has no existing explicit attachment.
3. Resolve and validate the prospective effective zero-or-one lifecycle coordinator before any
   factory, route, membership, or callback side effect.
4. Activate or join the configured ownership domain.
5. When a coordinator exists, discover and reserve the complete current subtree before publishing
   any lifecycle membership.
6. Reject an incompatible descendant by cancelling all reservations. No route, ledger entry,
   callback, registry entry, or property write remains.
7. Publish the root route and all reserved inherited ownership.
8. Invoke lifecycle callbacks in the characterized order for subjects that transition from
   unowned to owned. A same-domain inherited subject that merely gains an explicit anchor emits no
   new attach callbacks.

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
new context state and invalidates affected compiled chains.

An explicit root remains owned when the application merely drops its last CLR reference. The
application must call `DetachFromContext` to release explicit ownership.

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

During handler iteration, reaching the coordinator's position performs descendant traversal.
Handlers ordered before that phase keep their current top-down observations. Handlers ordered
after it and subject handlers keep their current deepest-first observations. The coordinator is
not optional and cannot be registered twice.

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
publication uses the PR 1 context mutation protocol. The implementation plan must state the exact
lock order and add deterministic tests for every cross-lock schedule. User callbacks, events,
service factories, property getters, and target code never execute while a Core ownership lock or
context mutation lock is held.

Quiescent invariants are:

1. every attached subject has exactly one ownership domain;
2. every inherited subject has exactly one active acyclic route unless explicit attachment wins;
3. every recorded parent membership corresponds to a committed parent property baseline;
4. the Tracking reconciliation baseline matches the latest committed structural value;
5. registry and parent projections agree with ownership after callbacks settle;
6. no pending reservation or stale route descriptor survives quiescence;
7. an unanchored component has no ownership record, route, or lifecycle projection.

## Failure Semantics

Expected contract failures are fail-fast and side-effect-free:

- invalid explicit target;
- duplicate explicit attachment;
- missing or wrong-context explicit detach;
- more than one prospective lifecycle coordinator;
- lifecycle coordinator mutation on an active domain;
- incompatible property child or descendant.

For property assignment, these failures happen before the backing property changes. For explicit
attachment, they happen before route publication and lifecycle callbacks.

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
- the ordinary Core write terminal is unchanged without a coordinator;
- known scalar writes perform no ownership dispatch, reservation, or graph work;
- cached reads, method calls, and service resolution add no ownership allocation;
- compatible secondary-parent changes do not traverse the complete graph;
- structural traversal uses reusable pooled buffers;
- stable effective routes do not invalidate caches;
- only route changes and legal service topology changes invalidate dependent states.

Local development performs static field-layout, allocation, chain-shape, lock, and invalidation
analysis. Local benchmark timings are diagnostic only. Before the pull request is declared ready,
the maintainer is asked to run the agreed comparisons against exact `master` on the stable
benchmark machine. Results must show no repeatable timing regression outside control-row noise and
no new steady-state allocation. Any signal above noise reopens the design.

## Test and Verification Design

Core tests cover:

- plain target validation;
- strict first attach, duplicate attach, exact detach, and reattach;
- explicit context reporting;
- lazy ownership allocation and final clearing;
- zero and one coordinator activation;
- rejection of coordinator mutation after activation;
- inline and overflow parent membership behavior;
- exact generation, stale cancel, stale clear, and route transfer;
- fallback composition without lifecycle callbacks;
- public provider API snapshots;
- Core-to-Tracking package-boundary compilation without new friend access.

Tracking tests cover:

- first parent attach and final detach;
- several parents in one domain;
- repeated collection occurrences and index refresh;
- deterministic earliest-parent selection and transfer;
- explicit-to-inherited transfer without lifecycle churn;
- cross-domain root, child, and descendant rejection before property commit;
- cycles with one anchor, several anchors, and final anchor removal;
- shared DAGs and back-edge route selection;
- callback-order characterization for attach, detach, recursion, registry, parents, properties, and
  derived initialization;
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
