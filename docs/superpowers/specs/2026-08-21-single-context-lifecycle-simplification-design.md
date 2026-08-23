# Single-Context Lifecycle Simplification Design

**Date:** 2026-08-21

**Status:** Approved design, pending maintainer review of this written specification

**Primary baseline:** `master` at `0418410c`

## Revision after implementation spike (2026-08-23)

A full implementation spike was run against this specification. Its findings are in `docs/spike/SPIKE-FINDINGS.md`, and the corrections below are authoritative wherever they disagree with the text further down. The ownership model survived the spike. The cost model, three under-specified areas, and the framing did not.

### Decisions taken

1. **Scope stays the full design**, with an honest estimate. This is not "a narrow replacement of the ownership nucleus inside the existing lifecycle machinery". That framing is what produced the under-scoping, and it is withdrawn.

2. **A reachability index is required, not deferred.** The complete context-local scan was measured at 135 times slower than master on a single shared-parent removal in a 2000-subject context, and an instrumented probe confirmed a full 2003-node mark per removed edge. Three replacements were implemented and measured. The chosen algorithm is a **backward search from the questioned subject up its committed incoming edges to the nearest anchored ancestor**, which restores the large-context case to 1.877 microseconds against master's 1.276 and costs 228 lines. A forward mark with precise invalidation was rejected because cross-parent removals always invalidate, so it never helps the common shape. Incrementally maintained reachability was rejected because it pays maintenance on every edge mutation including tree-shaped removals that the zero-remaining-edges short circuit makes free, measuring four times slower than master on `ChangeAllTires`.

3. **The read path does not change, and a write from a getter is a contract violation.** The spike proposed moving user getters out of `SyncRoot` so the lifecycle gate could span the terminal write. That is withdrawn. For a generated subject the read terminal's inner reader is a backing-field read, and `[Derived]` properties are not intercepted reads at all, so no user code runs there; `DerivedPropertyChangeHandler` already evaluates derived getters outside its own lock. The only user code that does reach the read terminal is a `RegisteredSubject.AddProperty` getter, and all three production callers pass a pure closure (`MethodPropertyInitializer`, `OpcUaSubjectLoader`, and the attribute pass-through on `RegisteredSubjectProperty`). Against that, `SyncRoot` on the read terminal is what pairs a backing read with the write terminal's field write, revision increment and write-state publication, so removing it permits torn reads of every value type wider than 64 bits, which is 44 files' worth of `decimal`, `decimal?`, `DateTimeOffset?` and `TimeSpan?` properties on exactly the connector-writes-while-reader-reads path. The rule instead becomes a stated contract: **a property getter must not write a subject-typed property.** Violating it is a contract violation, not a supported shape, and it is detected by a `[Conditional("DEBUG")]` guard so Release pays nothing. Lock order is then a total order, gate before `SyncRoot`, with no cycle to close.

4. **Parent snapshots activate lazily.** Folding parent tracking into `WithLifecycle()` made it unconditional and was measured at roughly 9 percent on structural removal plus 1.8 megabytes per operation on bulk assignment, paid by every consumer that previously did not opt in. Publishing on demand from a mutating structure would need a lock, which correction C1 forbids on that read path, so the mechanism is lazy *activation* rather than lazy materialisation: the first `GetParents()` on a subject sets a per-subject bit, and from then on that subject publishes its snapshot eagerly on every edge change. A consumer that never asks pays nothing, a consumer that asks pays what `WithParents()` cost it before, and the read stays a lock-free snapshot read with no new synchronisation. The lifecycle remains the sole writer. Only source-scope walks and path resolution consume it.

5. **Transient races block; only persistent conflicts throw.** The original text conflates the two, and the implementation threw for both. A subject genuinely owned by another context is a programming error and throws. A concurrent attach arriving while a structural write is in flight is a timing detail, not a caller error, and must not surface as an exception. The structural write therefore takes the executor's attachment monitor **before** resolving the interceptor chain and holds it through the terminal, so the window does not exist. Ordering is preferred over retry, which can livelock under sustained attach churn. This also removes a failure mode observed in the spike, where an unhandled rejection on a raw thread terminated the host process.

6. **The unattached structural write hole is closed.** Structural setters publish an executor even when the subject has never been attached, so the attachment guard always runs. This costs one executor allocation per subject that ever takes a structural write, including during deserialization and bulk construction, and that cost is accepted rather than left as an unstated gap. The specification previously read as though the guard covered this case; it did not.

7. **Subtree-scoped subject-local services are removed**, deliberately and as a named breaking change rather than as collateral. Preserving them would require retaining the executor-as-context relationship, the fallback graph, delegation resolution, cycle detection and cross-context invalidation, measured at about 670 of the 1088 lines of `InterceptorSubjectContext.cs`, which is the entire simplification. If subtree scoping is wanted later it returns through the separately designed mechanism already listed under deferred extensions.

8. **Hosted-service stop order is resolved, see decision 11.** Making release order deterministic flips subtree detach from children-first to parent-first. The flip was measured on both arms, it corrects an order that is currently not LIFO, and no first-party consumer is exposed. The hosting rework rebases on top of this change and inherits the resulting overlap question.

9. **The provisional anchor rule is kept, and its mutual-reference behaviour is correct rather than a wart.** Master was measured: a context-taking constructor gives no durable anchor at all, so `new Device(ctx)` adopted into a parent and then removed is released cleanly, which is why dependency-injection construction works. But the everyday back-reference is broken on master. Building `root = new Person(ctx)`, `root.Mother = child`, `child.Father = root` and then clearing `root.Mother` empties the registry of **both** subjects, and leaves the caller's own root half-dead: writing to it still fires the scalar change but no longer fires its derived properties. Retention on master is an artifact, a lone root survives only because nothing questions it. A reachability model cannot inherit that. The rule stated for consumers is: **a subject constructed with a context is a root; it stops being a root once it is attached into a graph that is already rooted somewhere else, and from then on it follows that graph.** That phrasing also settles the case an "outside my own subtree" phrasing would get wrong, since an unrooted island cannot adopt anything. In a mutually referencing pair the first-constructed subject is the adopter and keeps its anchor; that is the same outcome as any root the caller constructed and never removed, and `DetachFromContext` is how a caller gives it up. No fourth anchor variant is needed.

10. **The two upward walks are one query.** `EdgeProvidesIndependentSupport` and the backward reachability search differ only in start node and in whether the questioned subject's own anchor counts. They are unified into a single `HasAnchoredAncestor(start, excluded)`; release passes no exclusion, adoption passes `(parent, excluded: subject)`. This is also a correctness fix, because only one of the two validated its candidates against committed outgoing edges, which the standing rule requires. With the backward search chosen, the anchored-root set and the forward mark leave production entirely: the graph version, the mark cache, the mark stack, `RecomputeReachability`, thirteen version bumps and seven root-set mutations exist solely to feed an assertion compiled out in Release. The oracle moves into the test assembly as an independent reimplementation, which makes it stronger because it can no longer share a bug with the code it checks.

11. **Detach callback order flips for handlers behind the descent, and that is a fix.** Measured on both arms with a three-level chain: attach order is unchanged (`leaf, mid, top`), and only detach flips, from `leaf, mid, top` to `top, mid, leaf`. Master starts deepest-first and stops deepest-first, which is not LIFO; the new order makes stop the reverse of start, so a parent stops before the children it uses rather than after them. Nothing documented breaks, because `docs/design/tracking-lifecycle.md` documents attach order only and its detach paragraph is about registry resolvability. There is no first-party hosted subject nested inside another, so no in-repo behaviour changes today, and the hosted-service ownership rework replaces the single global dispatch queue with one serialized chain per target, which removes cross-target stop ordering altogether. That rework rebases on top of this change and inherits the question as an overlap question rather than an order question.

12. **Structural writes from lifecycle callbacks are forbidden, as a declared breaking change.** Master permits them for a different property and the contract is documented at the top of the current reconciliation loop. The new design forbids all of them. Treated as a contract violation with the same `[Conditional("DEBUG")]` detection as decision 3, so Release pays nothing.

13. **The merged lifecycle takes the descent's place as the public ordering seam.** The whole documented ordering model is expressed as a handler's position relative to `ContextInheritanceHandler`, which this change deletes. Both positions survive unchanged, with the same measured orders; the seam type becomes `LifecycleInterceptor`, so `[RunsBefore(typeof(ContextInheritanceHandler))]` migrates to `[RunsBefore(typeof(LifecycleInterceptor))]`. For those attributes to bind at all the merged lifecycle must implement `ILifecycleHandler`, because ordering constraints resolve only between services that both implement the interface being resolved. `ContextInheritanceHandler` and `ParentTrackingHandler` are public types, so this is a source-breaking rename of a public seam and belongs in the breaking-changes list.

14. **Verification is full and happens at the end.** This is a foundational change, so the final gate is every integration suite plus the Connector Tester, not a targeted subset. Two items make that non-optional rather than cautious: MQTT starts caching property mappings for connector root subjects for the first time, and the generator's base contract gains a required shape, so every base assembly built by the released generator rebuilds.

### Constraints discovered by implementing, which the original text does not state

- **`Reconcile` commits outgoing edges before updating incoming records.** Incoming and outgoing state legitimately disagree mid-operation, in three windows: reconcile commit, attach-time seeding, and cycle drain. Three independent implementations hit this and worked around it three different ways. Any algorithm that reads incoming edges must validate candidates against committed outgoing edges. This is the single most load-bearing undocumented detail in the area and belongs in the design, not in a comment.
- **Release order is observable and must be deterministic.** Master releases through a recursive descent, so detach callbacks arrive top-down. A mark-and-sweep yields an unordered set, and releasing by iterating the owned-subject dictionary is nondeterministic. Release must traverse from the removed edge over committed outgoing edges, visiting only unreachable subjects, in first-visit order.
- **Ordering attributes fail silently in two ways.** `ServiceOrderResolver` sorts First, Middle and Last groups independently and drops any constraint naming a type in another group without error. Separately, services are filtered by the requested interface before ordering, so an ordering attribute binds only when both parties implement the interface being resolved. A merged lifecycle must implement `ILifecycleHandler` for migrated attributes to bind at all.
- **Writes on unattached subjects are entirely unintercepted**, so a foreign reference is only detectable at attach or assign time. This is why claiming must walk the whole prospective subtree before mutating anything.

### Verification practices this design requires

The spike produced two independent instances of tooling reporting success while coverage shrank, and two complete, internally consistent, invalid benchmark comparisons. Implementation of this design must therefore reconcile **per-project** test counts against a recorded baseline rather than trusting a summary, and must validate benchmark arms against an independently measured mechanism rather than trusting the comparison harness. Every risky replacement should carry a `[Conditional("DEBUG")]` oracle that recomputes the previous answer and asserts agreement; that technique caught nothing in three variants, which is precisely why it was worth having.

## Purpose

This design simplifies subject context ownership around one permanent rule: a subject is either unattached or attached to exactly one `IInterceptorSubjectContext`. Explicit roots and inherited graph membership decide whether that one attachment remains active. Arbitrary object graphs, including shared DAGs and cycles, remain supported.

The implementation is a narrow replacement of the ownership nucleus inside the existing lifecycle machinery. It preserves the current interceptor chain, lifecycle handler pipeline, Registry integration, property baselines, collection traversal helpers, pooling, and observable callback ordering wherever the new ownership contract does not intentionally change behavior.

The earlier effective-route, explicit-ownership, unique-authority, and hosted-lifecycle roadmap is reference material, not the implementation baseline for this design. A separate maintainer review compares exact master behavior, that original plan, and this proposal.

## Goals

- Give every subject zero or one exact context, compared by reference identity.
- Make `AttachToContext` a strict explicit-root operation, and make context-taking constructors provisional roots that the first inherited edge releases.
- Make context inheritance and parent membership intrinsic to the built-in `LifecycleInterceptor`.
- Support multiple parents, repeated collection occurrences, shared DAGs, arbitrary cycles, and final orphan-cycle release.
- Keep lifecycle attachment distinct from individual object-reference edges.
- Reject cross-context structural assignments before the backing property changes.
- Preserve current lifecycle, Registry, property-handler, and cache-boundary ordering.
- Keep Core independent from Tracking policy through a small high-performance lifecycle seam that third-party lifecycle or tracking packages can implement.
- Remove executor-local service containers and fallback context composition.
- Add generic singleton service contracts without lifecycle-specific service slots.
- Keep reads and scalar writes free of ownership synchronization overhead.
- Prefer the smallest repository-wide churn compatible with the new invariants.

## Non-goals

- No ownership-route descriptor, route generation, route cache, or selected-parent route.
- No transactional ownership ledger, tentative batch engine, committed overlay, obligation adoption, release forest, or callback recovery protocol.
- No process-wide topology lock.
- No lock-free multi-subject ownership transaction.
- No automatic backfill when lifecycle, Registry, or another service is registered after subjects are already attached.
- No subject-local services or public service fallback graph.
- No mirrored compile-time reimplementation of the runtime subject-type classifier. The generator classifies conservatively and fails closed.
- No lifecycle lock on the parent-query read path.
- No automatic ownership from derived, computed, external, or independently changing getters.
- No runtime guard for calling an interceptor continuation more than once.
- No obsolete compatibility aliases for removed configuration APIs.
- No hosted lifecycle redesign in this change.

## Concepts

- **Context attachment:** the nullable exact `IInterceptorSubjectContext` stored by a subject executor.
- **Explicit root:** a subject whose context was anchored by `AttachToContext`. An inherited subject can be promoted to an explicit root in the same context.
- **Provisional root:** a subject anchored by a context-taking constructor. It is owned like an explicit root, and the anchor is cleared automatically the first time the subject gains an inherited structural edge that provides *independent support*, meaning the edge's parent has an anchored ancestor other than the subject itself. Clearing on the first edge of any kind is unsound and was proven so during implementation: the everyday back-reference `child.Parent = root` would consume the root's own anchor, after which the next removal anywhere releases the whole graph. Independent support is decided by walking committed incoming edges, which is exact, because reachability from a root means some root lies in the ancestor closure; a self-edge or a back-edge from the subject's own subtree therefore never consumes the anchor. It exists so that construction-time attachment, including the dependency-injection path where the container selects the context-taking constructor for every subject it builds, does not create anchors that nothing ever releases.
- **Inherited ownership:** context attachment retained because the subject is reachable from an explicit root through active structural edges managed by one lifecycle interceptor.
- **Structural edge:** one active occurrence of a subject in an intercepted, non-derived scalar subject, subject collection, or subject dictionary property.
- **Owned subject:** a subject currently attached to a context, whether explicitly, by inherited reachability, or both.
- **Lifecycle interceptor:** the zero-or-one `ILifecycleInterceptor` service responsible for structural ownership policy in one context.
- **Registry projection:** the optional richer navigation and metadata view built from committed lifecycle notifications. Registry is never the source of ownership truth.
- **Property baseline:** the value last reconciled by lifecycle for one structural property, represented by the existing `_lastProcessedValues` mechanism.

## Permanent Invariants

1. An executor stores either no context or one exact context. It never composes several subject contexts.
2. A subject has at most one root anchor, explicit or provisional. A repeated `AttachToContext` always throws, including one using the same context. A provisional anchor may be promoted to an explicit one by `AttachToContext` while the subject is still unadopted.
3. An implicitly attached subject may be promoted to an explicit root only in its current exact context.
4. An explicit detach removes only the root anchor. The subject remains attached when it is still reachable from another root in the same context. Adoption by a structural edge clears a provisional anchor and never clears an explicit one.
5. A structural edge may connect owned subjects only when both use the same exact context. A conflicting assignment fails before the backing writer.
6. The object-reference graph may contain any shape supported by ordinary object models, including multiple roots, multiple parents, repeated occurrences, DAGs, self-cycles, and larger cycles.
7. Subject attach and detach events describe transitions between unattached and context-owned states. They do not describe every structural edge addition or removal.
8. Every structural occurrence is an edge. Repeating the same subject twice in one collection contributes two parent edges and two references.
9. Registry reflects lifecycle state and can be omitted without changing ownership behavior.
10. Without an `ILifecycleInterceptor`, explicit attachment works for the root only. Structural assignments do not propagate context or create lifecycle memberships.
11. A lifecycle callback is synchronous, fast, and exception-free by contract.
12. An interceptor may invoke its continuation zero or one time per invocation. A second call is an unsupported contract violation with no runtime guard.
13. Quiescent state must agree across backing structural values, lifecycle memberships, parent/reference views, Registry projection, and executor contexts.

## Design Approach and Churn Boundary

Three implementation shapes were considered:

1. Retrofitting the current reference-count engine with new checks has the smallest textual diff but leaves cycle reachability awkwardly mixed with last-reference recursive teardown.
2. Replacing only the ownership nucleus inside `LifecycleInterceptor` makes the final state match the new contract while retaining the surrounding proven machinery.
3. Implementing the earlier transactional ownership design provides stronger reentrancy and recovery semantics but introduces substantially more state, public protocol, test schedules, and cognitive complexity.

This design chooses a narrow hybrid of options 1 and 2. The existing `LifecycleInterceptor`, handler pipeline, event API, property traversal, `_lastProcessedValues`, collection refresh behavior, and thread-local pools remain. The internal attached-subject/reference-count decision state is replaced by explicit roots, occurrence-aware incoming edges, executor context state, and cycle-safe reachability.

The implementation must not introduce route descriptors, ownership generations, global overlay state, transactional reconciliation phases, or a second graph abstraction unless a concrete correctness failure proves the approved model insufficient.

## Public Subject and Context API

### `IInterceptorSubject`

`IInterceptorSubject.Context` and `IInterceptorSubject.SyncRoot` are removed. `IInterceptorSubject` exposes its executor instead:

```csharp
public interface IInterceptorSubject
{
    IInterceptorExecutor Executor { get; }

    ConcurrentDictionary<(string? property, string key), object?> Data { get; }

    IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; }

    void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties);
}
```

The existing `Data`, `Properties`, and `AddProperties` declarations remain. Generated, Dynamic, and manual subjects expose the same executor contract. The generator's supported-base contract, generated-member collision table, and hijacking diagnostics replace the `Context` and `SyncRoot` slots with `Executor`; this is a contract migration, not only a generated-property rename.

The public monitor is removed because callers could otherwise take a subject lock before entering lifecycle topology work and invert the library lock order. The executor retains one private subject/property monitor for terminal reads, writes, context transitions, attachment-revision checks, and metadata publication. Metadata publication belongs on that list because `AddProperties` currently serializes its read-merge-write of the property lookup on the same object that `SyncRoot` exposes, and an unattached `AddProperties` has no lifecycle lock to fall back on.

### `IInterceptorExecutor`

`IInterceptorExecutor` no longer inherits `IInterceptorSubjectContext`. It remains responsible for read, write, and method interception and exposes nullable context attachment state needed by Core extensions and advanced lifecycle implementations:

```csharp
public interface IInterceptorExecutor
{
    IInterceptorSubjectContext? Context { get; }

    bool IsExplicitlyAttached { get; }

    TProperty GetPropertyValue<TProperty>(
        string propertyName,
        Func<IInterceptorSubject, TProperty> readValue);

    bool SetPropertyValue<TProperty>(
        string propertyName,
        TProperty newValue,
        TProperty currentValue,
        Action<IInterceptorSubject, TProperty> writeValue);

    bool SetStructuralPropertyValue<TProperty>(
        string propertyName,
        TProperty newValue,
        TProperty currentValue,
        Action<IInterceptorSubject, TProperty> writeValue);

    object? InvokeMethod(
        string methodName,
        object?[] parameters,
        Func<IInterceptorSubject, object?[], object?> invokeMethod);
}
```

The concrete executor also supplies a narrow advanced Core state-transition capability used by lifecycle providers. It can attach an unowned executor to an exact context, promote inherited attachment to explicit, remove an explicit anchor, and clear a context after lifecycle release. These operations are allocation-free, enforce local one-context invariants, serialize through the private executor monitor, and update an internal attachment revision. They do not know about edges, reachability, Registry, or callbacks.

Generated setters select `SetStructuralPropertyValue` from the declared property type at generation time. Dynamic and manual property paths make the equivalent declared-type classification before resolving an interceptor chain. This lets a structural write capture attachment state even when the subject was unattached at entry, while ordinary scalar setters retain their direct path without an attachment-revision comparison.

The compile-time classification must fail closed, and it must not attempt to reproduce the runtime classifier. A Roslyn symbol cannot answer the runtime question: the generator emits the `IInterceptorSubject` base-list entry itself, so a same-compilation subject symbol does not carry that interface, and `dynamic`, unresolved types, and multi-dimensional subject arrays each classify differently at compile time than at run time. The generator therefore emits the scalar route only for declared types that provably cannot hold a subject, which is the primitives, `string`, `decimal`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid`, enums, and nullable forms of those. Everything else, including `object`, `dynamic`, interfaces, unresolved types, and every same-compilation type, takes the structural route. A false positive costs one predictable branch on an uncommon property. A false negative would silently skip the attachment-revision guard on exactly the path the guard exists for, while the lifecycle still performs structural work because it classifies from metadata.

Because the structural route must observe attachment state, a structural write cannot use the generated no-executor short circuit. It publishes the executor first, so a subject that takes a structural write while unattached allocates one executor. Scalar writes on unattached subjects keep the direct field write and allocate nothing. This allocation is intentional, and the benchmark set measures unattached structural writes alongside unattached scalar writes so its size is known rather than assumed.

The exact low-level transition method names may be adjusted during implementation for a smaller safe public surface, but third-party `ILifecycleInterceptor` implementations must be able to perform the same operations without reflection, `ConditionalWeakTable`, subject `Data`, or friend-assembly access.

### Subject context extensions

Core provides these subject-level extensions:

```csharp
public static IInterceptorSubjectContext? TryGetContext(
    this IInterceptorSubject subject);

public static IInterceptorSubjectContext GetContext(
    this IInterceptorSubject subject);

public static void AttachToContext(
    this IInterceptorSubject subject,
    IInterceptorSubjectContext context);

public static void DetachFromContext(
    this IInterceptorSubject subject,
    IInterceptorSubjectContext context);
```

`TryGetContext` returns null when unattached. `GetContext` returns the exact context or throws `InvalidOperationException` when unattached. There are no direct subject service-resolution extensions. Callers use `subject.GetContext().GetService<T>()` or the nullable `TryGetContext()` path explicitly.

### Explicit attachment rules

| Existing state | Operation | Result |
|---|---|---|
| Unattached | `AttachToContext(context)` | Attach to `context`, mark explicit, and run lifecycle discovery when configured |
| Inherited in the same context | `AttachToContext(context)` | Promote to explicit without a subject attach event |
| Already explicit | `AttachToContext(anyContext)` | Throw before state change, including the same context |
| Owned by another context | `AttachToContext(context)` | Throw before state change |
| Exact explicit attachment | `DetachFromContext(context)` | Remove the explicit flag; remain inherited if reachable, otherwise detach |
| No explicit attachment | `DetachFromContext(context)` | Throw before state change |
| Explicit attachment in another context | `DetachFromContext(context)` | Throw before state change |

`new Subject(context)` and `new DynamicSubject(context)` remain supported and attach after normal constructor chaining. They create a *provisional* root anchor, not a strict explicit one. A provisional anchor keeps the subject owned while it is being populated and is cleared automatically the first time the subject gains an inherited structural edge in the same context. `AttachToContext` sets a strict explicit anchor that is never cleared automatically, and a second `AttachToContext` still throws.

The distinction exists because a context-taking constructor is not a deliberate statement that the subject is a root. Dependency injection makes that decisive: when the context is registered as a service, `ActivatorUtilities.CreateInstance` selects the context-taking constructor for every subject it builds, so every deserialized object would otherwise become an unreleasable explicit root whose hosted services never stop and whose Registry entry is never evicted. Master already behaves the way a provisional anchor behaves, because the constructor's fallback addition is silently consumed by the later parent edge and released when the subject is unparented. Provisional anchors keep that ergonomics while leaving the strict contract available to callers who ask for it by name.

When no lifecycle interceptor is configured, explicit attach sets only the root executor context and explicit bit. Explicit detach clears them. The root's structural descendants remain unattached.

### Temporary construction ownership

Master has first-party loaders that attach a newly created child to the parent context before populating it, then assign it to the parent. The current fallback set silently lets the later parent edge replace the practical role of that attachment. Strict explicit roots require the transfer to be stated:

```csharp
var context = parent.GetContext();
child.AttachToContext(context);
try
{
    PopulateChild(child);
    parent.Child = child;
}
finally
{
    child.DetachFromContext(context);
}
```

After the assignment succeeds, removing the explicit anchor is callback-silent because the parent edge keeps the child reachable. If population or assignment fails, the same `finally` releases the temporary root and its otherwise unreachable component. Collection and dictionary loaders perform the detach only after the complete structural value has been assigned so every new child already has an inherited route.

This explicit pattern is required only when a loader attaches an already-constructed subject with `AttachToContext`. A child built through a context-taking constructor needs no `finally` at all, because its provisional anchor is cleared by the assignment itself. That removes the transfer protocol from the OPC UA loader, the connector update appliers, and every dependency-injection construction path, which is where it would otherwise have been hardest: `SubjectItemsUpdateApplier` creates items at four call sites and commits the structural value at two, so temporary roots would have had to be threaded through both commit points with release on the exception and no-change paths.

Long-lived application roots such as HomeBlaze's deserialized root are already attached by their constructor. They must not be attached a second time; if a strict anchor is wanted there, it is a promotion of the existing provisional anchor rather than a fresh attach.

## Removed Context and Configuration Capabilities

`IInterceptorSubjectContext` becomes a flat service container. `AddFallbackContext` and `RemoveFallbackContext` are removed, together with fallback traversal, reverse dependency tracking, delegation-cycle handling, and fallback cache invalidation.

Executors are no longer service containers, so subject-local service registration and subject-local fallback composition disappear. An attached executor resolves interceptor and service arrays directly from its one configured context. An unattached executor uses an empty interceptor set.

`ContextInheritanceHandler`, `ParentTrackingHandler`, `WithContextInheritance()`, and `WithParents()` are removed without obsolete aliases. Their supported behavior moves into the built-in lifecycle implementation:

- `WithLifecycle()` enables inherited context ownership and authoritative parent membership.
- `WithRegistry()` continues to install Registry and enables `WithLifecycle()` automatically.
- `WithFullPropertyTracking()` continues to enable `WithLifecycle()` automatically.
- `WithSourceMonitoring()` enables `WithLifecycle()` automatically because branch-scope walks require the merged parent state.
- Existing repeated chains such as `WithRegistry().WithLifecycle()` remain idempotent for the already-installed default lifecycle implementation.

Extensions that require the built-in lifecycle establish it before publishing dependent services. In particular, `WithRegistry()` must fail on a custom lifecycle singleton before adding `SubjectRegistry`, so a configuration conflict cannot leave a partially installed built-in projection. Ordering attributes, rather than registration order, preserve the required interceptor and handler sequence.

## Core and Tracking Extensibility Boundary

Core owns attachment mechanism. Tracking owns lifecycle policy.

| Core | Built-in Tracking |
|---|---|
| Executor creation and interceptor execution | `LifecycleInterceptor` implementation |
| Nullable executor context and explicit bit | Explicit-root and owned-subject sets |
| Private executor monitor and attachment revision | Incoming structural edge occurrences |
| `GetContext`, `TryGetContext`, explicit attach/detach entry points | Parent snapshots and reference counts |
| Allocation-free raw attachment state transitions | Structural traversal and property baselines |
| Flat service container and singleton validation | Context conflict validation and subject claiming |
| Optional `ILifecycleInterceptor` strategy seam | Reachability, cycle release, callbacks, and handler ordering |
| Atomic metadata publication continuation | `AddProperties` ownership admission |
| Terminal lifecycle interceptor ordering | Per-lifecycle reentrant topology lock |

Registry remains above Tracking and projects its committed notifications. Core has no Registry reference and no parent, edge, reachability, callback, or cycle type.

### Public lifecycle seam

The current Core `ILifecycleInterceptor` is evolved rather than replaced by a parallel internal interface. Its intended shape is:

```csharp
public interface ILifecycleInterceptor :
    IWriteInterceptor,
    ISingletonContextService<ILifecycleInterceptor>
{
    void AttachSubjectToContext(
        IInterceptorSubject subject,
        IInterceptorSubjectContext context);

    void DetachSubjectFromContext(
        IInterceptorSubject subject,
        IInterceptorSubjectContext context);

    void AddProperties(
        ref SubjectPropertyRegistrationContext context,
        SubjectPropertyRegistrationDelegate next);
}
```

The final property-registration context and delegate names may follow existing interception naming conventions. The semantic requirements are fixed: the context exposes the subject and once-materialized additions, `next` atomically publishes the complete metadata lookup, and the continuation may be invoked zero or one time.

Core delegates explicit attach/detach and attached-subject metadata addition to the configured singleton lifecycle implementation. Without one, Core performs simple root attachment/detachment and direct metadata publication. Structural property changes already reach the lifecycle implementation through `IWriteInterceptor`, so no additional per-write lifecycle dispatch is added.

Core orders the singleton lifecycle interceptor closest to the terminal backing writer. Equality and other outer interceptors can suppress a write before lifecycle work. After a successful terminal write, lifecycle reconciliation finishes before control returns to outer change, derived, and transaction interceptors.

A third-party lifecycle or tracking package can implement `ILifecycleInterceptor`, store any graph representation, choose its own correct synchronization model, and use the Core raw executor transitions. It does not need Tracking internals. The built-in Registry depends on the built-in Tracking notification protocol, so installing it together with an incompatible custom lifecycle fails through singleton conflict rather than silently producing an incomplete Registry. A custom lifecycle package provides its own compatible projection and configuration extensions.

The seam adds no dispatch to reads. When lifecycle is absent, it adds no write interceptor. When present, scalar writes pay the same lifecycle interceptor dispatch already present on master and immediately call `next`. Explicit attach/detach and `AddProperties` are cold operations.

The built-in `LifecycleInterceptor` also implements Tracking's `ILifecycleHandler` and occupies the logical descent slot currently occupied by `ContextInheritanceHandler`. The handler dispatcher treats that reference-identical service slot as the point where recursive child discovery and release occur. Handlers ordered before the slot continue to observe top-down attachment, handlers after the slot and subject handlers continue to observe bottom-up attachment, and Registry remains before descent so ancestors are registered before descendant callbacks. Ordering attributes on built-in handlers migrate from `ContextInheritanceHandler` to `LifecycleInterceptor` where needed.

`SourceMonitor` changes its ordering dependency from `ContextInheritanceHandler` and `ParentTrackingHandler` to the built-in `LifecycleInterceptor`. Its reparent notifications and branch walks consume the lifecycle's authoritative parent state, so removing `WithParents()` does not weaken source synchronization scope semantics.

## Context Services and Singleton Contracts

The generic uniqueness marker is named `ISingletonContextService<TContract>`:

```csharp
public interface ISingletonContextService<TContract>
{
}
```

Uniqueness is keyed by `TContract`, not by implementation or registration type. One service object may implement several singleton contracts. The context discovers an implementation type's singleton contracts once, caches the result by implementation type, and performs no singleton reflection on interceptor hot paths.

Registration rules are:

- Direct registration of a second service for the same singleton contract throws, including registration of the exact same object again.
- `TryAddService` first evaluates its existing-service predicate. When that predicate matches, it returns false without invoking the factory and without attempting singleton registration.
- When `TryAddService` invokes the factory and the produced object conflicts with an existing singleton contract, it throws.
- Singleton validation is repeated against the latest service snapshot after a reentrant factory returns, so a reentrant registration cannot create a duplicate.
- Singleton and ordinary services may be added while subjects are attached.
- Service publication replaces the immutable context snapshot and invalidates locally cached chains. An in-flight operation may finish with its pinned old snapshot; later operations observe the new service.
- A late-added lifecycle, Registry, or other stateful service receives no automatic replay or graph backfill. Correct late initialization is the consumer's or service's responsibility.

`WithLifecycle()` remains idempotent when its default `LifecycleInterceptor` already exists because its `TryAddService` predicate returns false before a second factory call. If a different custom lifecycle already occupies the singleton lifecycle contract, an attempt to install the built-in lifecycle throws.

First-party configuration-owned services opt into singleton contracts where duplicate instances would create competing authorities or duplicate event streams. At minimum this includes the lifecycle interceptor, SubjectRegistry, SourceMonitor, transaction coordinator/writer slots, property-change channel, and hosted-service lifecycle handler. Ordinary user services, ordered interceptor chains, validators, and lifecycle handlers remain multi-registration unless their implementations explicitly declare a singleton contract.

A service object that fills several roles is registered once. Normal assignability makes that one registration visible through every implemented service interface. Existing configuration code that reentrantly publishes the same object once per role, such as SourceMonitor's concrete and `ILifecycleHandler` registrations, is collapsed to one registration; registering the same object twice is still a singleton conflict.

## Built-in Lifecycle State

Each built-in `LifecycleInterceptor` owns one context-local state set and one private reentrant topology lock. The state remains conceptually close to master:

```text
owned subjects: subject -> incoming edge occurrence state
explicit roots: set of subjects
property baselines: PropertyReference -> last reconciled value
```

Incoming edge state replaces the current `PropertyReferenceSet` and boxed counter in `subject.Data`. It records real occurrences:

- Scalar subject property: one edge identified by parent and property.
- Subject collection: one edge per occurrence with its current index.
- Subject dictionary: one edge per subject value with its key.
- General enumerable subject shape: one edge per enumerated occurrence with its current ordinal or key according to the declared property shape.

The common one-parent and unique-subject cases should retain inline or pooled storage and avoid allocating a general multiset until multiplicity requires it. This is an implementation optimization, not a semantic distinction.

`GetReferenceCount()` derives the number of active incoming lifecycle edge occurrences. An explicit root contributes zero. `[a, a, b]` gives `a` a count of two and `b` a count of one.

`GetParents()` remains a Tracking extension and returns occurrence-aware parent entries, including list indices and dictionary keys. It does not depend on Registry. An unattached subject or a subject in a context without the built-in lifecycle returns an empty result.

`GetParents()` must not take the lifecycle lock. The lifecycle is the only writer of parent state, but it publishes that state as an immutable per-subject snapshot that readers take without any lock, exactly as the current `ParentsSet` does. This is not an optimization, it is required for correctness: `SourceMonitor` holds its own lock across a graph walk that calls `GetParents()`, and it is also called from inside the lifecycle lock through `HandleLifecycleChange`. A `GetParents()` that acquired the lifecycle lock would make those two orders opposite and deadlock. `SourceOwnershipManager` and `HostedServiceHandler` compose into the same cycle. Single authority is preserved because the lifecycle remains the sole writer; only the read is lock-free.

Occurrence identity is new work rather than preserved behavior. Master's parent entries are already wrong for duplicates and reorders: a reorder leaves stale indices because parent tracking never observes collection refresh, and a duplicate removal leaks a parent entry permanently because attach records the first occurrence index while removal iterates in reverse and records the last. Index-stable occurrence identity therefore has to be built and tested from scratch.

Registry may retain its optimized parent and child snapshots for navigation, but those snapshots are projections. They do not become a second ownership authority.

## Structural Edge Reconciliation

Structural classification uses the declared `SubjectPropertyMetadata.Type`, not the generic `TProperty` hint, because boxed dynamic paths may use `object`.

Collection reconciliation retains matching occurrences wherever possible. For duplicate subjects, retained old and new occurrences are matched deterministically in enumeration order. Moving retained occurrences refreshes their indices without subject detach/attach transitions. Excess old occurrences are removed in the current reverse-removal order, and excess new occurrences are added in forward order.

Examples:

- `[a, a, b]` to `[a, b]` removes one edge to `a`; `a` remains owned with reference count one.
- `[a, b]` to `[b, a]` retains both edges and refreshes indices without subject attach/detach events.
- `{ x: a, y: a }` contains two edges to `a` because the keys identify two occurrences.
- Changing a dictionary key is one edge removal and one edge addition unless the dictionary abstraction itself reports stable key identity through its normal value shape.

The existing unique-subject fast path remains. Duplicate-heavy collection reconciliation may take a slower multiset path because duplicates are uncommon and correctness requires occurrence preservation.

## Explicit Attach

With the built-in lifecycle configured, `AttachToContext` performs this operation under the target lifecycle's reentrant topology lock:

1. Recheck the executor's exact context and explicit state under its private monitor.
2. Reject duplicate explicit attachment or a conflicting context before callbacks.
3. If the subject is already inherited in the same context, add the explicit-root flag and root-set membership without subject attach callbacks.
4. For an unattached subject, discover its current direct-readable structural component using visited sets so cycles terminate.
5. Validate every discovered subject. A subject may be unattached or already owned by the same exact context. A different context is a conflict.
6. Claim newly owned executors through Core's raw attachment transitions before invoking a backing writer or lifecycle callback. A competing context claim loses and causes this operation to release any provisional claims and throw before callbacks.
7. Seed property baselines and incoming edge occurrences.
8. Reuse the current lifecycle handler, subject event, and property-handler sequence to publish the new ownership state.

The traversal joins already-owned same-context subjects without repeating subject attach callbacks. Every newly owned subject receives exactly one attach transition, including subjects in cycles.

Without lifecycle, Core performs only the executor context and explicit-bit transition.

## Structural Writes

The built-in lifecycle is terminal among write interceptors. Its structural write protocol is:

1. Classify the declared property type. Scalar properties immediately call `next` without taking the lifecycle lock.
2. Reject structural write reentrancy from a lifecycle callback before calling `next`.
3. Enter the parent context's lifecycle lock and verify that the parent still belongs to this exact lifecycle.
4. Read the proposed structural value and discover newly reachable candidate subjects using pooled visited state.
5. Reject every subject owned by another context before the backing writer.
6. Provisionally claim newly reached unattached subjects through Core executor transitions. If a competing context wins any claim, revert this operation's provisional claims and throw before the backing writer.
7. Call `next` once. Because lifecycle is terminal, this reaches only the backing writer. A suppressed outer interceptor never enters this protocol.
8. Reconcile the committed value against `_lastProcessedValues`, preserving the existing authoritative getter reread where required by the generated or Dynamic property contract.
9. Publish edge additions and removals, parent/reference state, new subject transitions, baselines, Registry notifications, and callbacks in current order.
10. On any removal, explicit-root demotion, or final release candidate, run reachability before clearing ownership.

A generated or custom structural backing writer must be synchronous and must not replace the proposed structural value with a different subject graph after validation. A writer that mutates and then throws or introduces an unvalidated conflicting subject is a contract violation. Core and Tracking do not implement rollback for such a writer.

## Reachability and Cycle Release

Reference count no longer determines ownership. After an edge removal or explicit-root removal, lifecycle computes reachability from all explicit roots in that context over its committed structural baselines.

The first implementation intentionally uses a complete context-local mark scan:

1. Mark every explicit root.
2. Traverse committed outgoing structural edges with a visited set.
3. Retain every marked owned subject.
4. Release every unmarked owned subject and its active edge occurrences.

This is `O(subjects + edges)` for removal operations. Add-only writes do not need a full reachability scan. The algorithm correctly releases orphaned self-cycles and larger cycles and retains shared subgraphs reachable from any root.

An affected-component or decrementally maintained reachability index is deferred until benchmarks demonstrate that the complete scan is material. The public contract does not expose the scan strategy.

## Per-Lifecycle Concurrency

The built-in implementation uses one private reentrant lock per `LifecycleInterceptor`, which is equivalent to one topology lock per configured context because lifecycle is a singleton contract.

- Structural writes on an attached parent take only that parent's lifecycle lock.
- Explicit attach takes only the target context's lifecycle lock.
- Explicit detach takes only the subject's current lifecycle lock.
- `AddProperties` on an owned subject takes only that subject's lifecycle lock.
- Reachability reads only state owned by that same lifecycle.
- Encountering a subject owned by another context throws. The operation never acquires the other context's lifecycle lock.
- Independent contexts therefore perform structural changes concurrently without lock ordering between lifecycle instances.

Competing adoption of an unattached subject is serialized by the candidate subject executor's private monitor. The first context transition wins. A losing lifecycle releases its earlier provisional claims and throws before its backing property write or callbacks.

Structural property write contexts capture the executor attachment revision. The terminal checks that revision under the private executor monitor. A structural write that selected an unattached or old-context chain and then races with attach, detach, or reattach fails before its backing mutation instead of silently bypassing the new lifecycle. Scalar writes do not pay this attachment-revision check because they cannot change ownership topology.

Callbacks execute while the lifecycle lock is held. Monitor reentrancy permits the supported same-lifecycle `AddProperties` case. Library code always takes a lifecycle lock before an executor monitor when both are needed. The executor monitor is private, so application code cannot invert that order through a public `SyncRoot`.

This design does not require a process-wide gate. A fully lock-free ownership graph remains out of scope because multi-subject validation, context claiming, backing mutation, reachability, and ordered projection would require reservation descriptors, versions, retry, and rollback comparable to the rejected transactional design.

## Lifecycle Callback Reentrancy

Tracking maintains thread-local callback-depth state shared across built-in lifecycle instances.

During a lifecycle, subject event, or property lifecycle callback:

- Reads are allowed.
- Scalar writes are allowed.
- `AddProperties` on a subject owned by the currently executing lifecycle is allowed and reentrant.
- `AddProperties` on an unattached subject is allowed because it performs no lifecycle ownership work.
- `AddProperties` on a subject owned by another context throws before enumeration or publication because taking a second lifecycle lock could deadlock with opposing callbacks.
- Every ordinary structural setter throws before its backing writer, including a setter on another context.
- Explicit attach and detach throw before state change.

This deliberately supports the real dynamic-property initializer case without implementing general nested structural transactions.

Lifecycle callbacks are exception-free by contract. If a callback violates the contract, the exception propagates and locks are released through `finally`, but the library provides no rollback, callback continuation, obligation adoption, or recovery guarantee. The operation may already have committed lifecycle or Registry state.

## Continuation Contract

Every read, write, invoke, and metadata publication interceptor continuation may be invoked zero or one time per interceptor invocation. Branching code may choose whether to call `next`, but it may not call `next` twice.

Core does not add a watermark, continuation index, atomic guard, or debug-only check. This keeps the hot path unchanged. A repeated call is documented unsupported behavior, and correctness after that violation is not guaranteed.

## Lifecycle and Registry Notification Order

The current observable notification sequence is a compatibility constraint. Existing ordering tests should be reused and expanded instead of replacing the protocol.

For an edge addition or first subject entry:

1. Update authoritative incoming membership and reference count.
2. Make the new exact context visible before attach callbacks.
3. Invoke ordered context `ILifecycleHandler` services. Registry participates at its current ordered slot.
4. Invoke the subject's own `ILifecycleHandler`.
5. Raise `SubjectAttached` only when the subject changed from unattached to context-owned.
6. Invoke property lifecycle attachment for the subject's properties in current order when this is its first context entry.

For an edge removal or final subject release:

1. Update authoritative incoming membership and reference count.
2. For final release, perform current property lifecycle teardown in its existing order.
3. Raise `SubjectDetaching` while the subject's old context and Registry subject are still available.
4. Invoke the subject's own `ILifecycleHandler`.
5. Invoke ordered context `ILifecycleHandler` services. Registry removes its projection at its current ordered slot.
6. Clear the executor context only after that subject's teardown callbacks complete.

For nonfinal edge additions and removals, the context and subject lifecycle handlers receive the property-edge change, but `SubjectAttached` and `SubjectDetaching` do not fire.

`SubjectAttached` is the cache population boundary and fires exactly once for each transition from null context to a context. `SubjectDetaching` is the cache eviction boundary and fires exactly once for each transition that will clear the context. Adding another parent, removing one surviving parent, promoting inherited ownership to explicit, or removing an explicit flag while inherited does not repeat these events.

Replacing one structural property value continues to publish removals before additions. Existing acyclic traversal and handler service ordering are preserved. Cycles use deterministic first-visit traversal, and each subject receives at most one attach or detach transition for one ownership change.

`ContextInheritanceHandler` and `ParentTrackingHandler` are removed as separate implementations. The reference-identical built-in `LifecycleInterceptor` occupies the former context-inheritance descent slot, and authoritative parent membership is updated internally before notification. Registry, SourceMonitor, user handlers, subject handlers, events, and property handlers retain their observable relative order around that boundary.

Several observations change intentionally, and all of them are breaking.

`GetParents()` reflects the committed edge before the first context handler runs. The earlier claim that master merely made this visible later was wrong in kind: `ParentTrackingHandler` is opt-in, and neither `WithFullPropertyTracking()` nor `WithRegistry()` registers it, so for the common Registry configuration the change is from empty to populated rather than from later to earlier. The resolved order on master is Registry, then parent tracking, then descent. Registry does not read parent state during attach, so it does not require the old visibility, but the ordering audit must also account for `HostedServiceHandler`, which is ordered after the descent, and for three first-party handlers that carry no ordering attributes at all and are therefore positioned by registration order.

The inherited context becomes visible before the first attach callback. On master a child's context does not yet resolve the parent's services inside the first context handler, because the inheritance handler has not run.

Structural writes from lifecycle callbacks stop working. Master permits them for a property other than the one being reconciled and forbids only same-property reentrancy. This design forbids all of them, which also retires the deadlock-avoidance path in the derived-property handler that exists so derived getters with subject-typed side effects can run inside the lifecycle lock.

Subject-local service registration and subtree-scoped services are removed. No first-party production code uses them, but they are a documented capability with a written rationale and an ordering guarantee.

Making the lifecycle terminal moves `ValidationInterceptor` from inside the lifecycle to outside it, because its only ordering attribute is vacuous unless transactions are registered and registration order decides today. A validation throw therefore happens at a different point relative to provisional subject claiming.

Adding a structural accessor helper changes the generated base contract, so every base assembly compiled by an older generator must be rebuilt. That surfaces as the existing stale-base diagnostic, which is a build error under warnings-as-errors.

## Reference Count and Parents

Reference count remains public for compatibility but changes from a separately boxed mutable counter to a derived lifecycle value.

```text
ReferenceCount = number of active incoming structural edge occurrences
```

An explicit root may be owned with reference count zero. Reference count is not an attachment predicate and must not be used as shorthand for `TryGetContext() != null`.

`SubjectLifecycleChange.ReferenceCount`, `GetReferenceCount()`, and `RegisteredSubject.ReferenceCount` remain unless implementation review finds an unavoidable API conflict. They report the same occurrence-aware count. First attach through an edge normally reports one. Final release reports zero after released lifecycle memberships are removed, including orphan-cycle release.

Every edge occurrence receives its normal `IsPropertyReferenceAdded` or `IsPropertyReferenceRemoved` handler change. A repeated collection subject may therefore produce more property-edge notifications than master, while subject attach/detach events remain once per context transition.

First-party code that currently treats `ReferenceCount > 0` as context ownership must migrate to `TryGetContext() != null` or the appropriate Registry membership check.

## `AddProperties`

`AddProperties` is the one supported topology-bearing operation during same-lifecycle callbacks. It remains a cold path and prioritizes atomic metadata and ownership behavior over micro-optimizing every classification step.

One call performs:

1. Reject cross-context callback reentrancy before enumerating user input.
2. Materialize the metadata sequence exactly once.
3. Build the complete prospective immutable property lookup and validate duplicate names before publication.
4. Classify ownership candidates only when `IsIntercepted`, not `IsDerived`, the declared type can contain subjects, and `GetValue` is available.
5. Invoke each qualifying getter exactly once before metadata publication and capture its value.
6. Discover the complete prospective structural subgraphs from the captured values and validate every subject against the current exact context.
7. Provisionally claim every newly reached unattached executor through Core before metadata publication. A competing context claim releases this call's provisional claims and fails the batch.
8. Invoke the Core publication continuation once to atomically swap the complete immutable metadata lookup.
9. Invoke property lifecycle handlers for every published metadata entry in input order.
10. Let Registry create each corresponding `RegisteredSubjectProperty` before structural edge notifications need to resolve it.
11. Admit the captured structural values as ordinary assignments, seed `_lastProcessedValues` from the captured values, and publish inherited ownership and Registry edges.

If input enumeration, duplicate validation, a qualifying getter, context validation, or provisional claiming fails, no metadata, Registry property, lifecycle edge, or committed ownership state is published. Provisional executor claims are released before the failure escapes. The generated and Dynamic metadata publisher is a synchronous direct immutable-dictionary assignment. A custom publisher that mutates and then throws violates the publication contract; no rollback is promised for that violation.

Ownership getters used during metadata admission must be synchronous, stable, side-effect-free, callable before metadata publication, and authoritative for the property's initial stored value. They must not mutate ownership or metadata. Later structural changes must pass through the intercepted setter. Computed structural values that change independently are unsupported ownership sources.

The input metadata enumerable follows the same cold-path contract: it is synchronous, stable, and free of topology or metadata side effects. Core materializes it exactly once after callback reentrancy admission. Iterator reentrancy or mutation is a contract violation and receives no replay or rollback.

Adding properties to an unattached subject only publishes metadata. A later lifecycle attach discovers then-current intercepted, non-derived structural properties through their normal getters.

Registry's current manual `RegisteredSubject.AddProperty` projection insertion, explicit `AttachSubjectProperty` workaround, and synthetic null-to-value intercepted write are replaced by the central property lifecycle admission path. `IPropertyLifecycleHandler.AttachProperty` ensures the Registry property projection exists before initial structural edges are published.

## Derived and Computed Properties

Derived or nonintercepted properties never establish ownership edges, even when their declared or runtime value contains subjects.

For example, a derived `LastSubjectOfCollection` property that returns the final element of an owned subject collection does not add another parent edge. The underlying collection property owns the real edges. Registry and change tracking may expose the derived value normally, but lifecycle ignores it for reachability.

Dynamic properties that represent stored structural values must be non-derived, provide a stable getter, and route subsequent changes through their intercepted setter.

## Errors and Contract Violations

| Condition | Outcome |
|---|---|
| Duplicate explicit attach | Throw before state change |
| Attach to a different current context | Throw before state change |
| Missing, inherited-only, or wrong-context explicit detach | Throw before state change |
| Structural assignment containing another-context subject | Throw before backing property write |
| Competing context wins a provisional subject claim | Release this operation's provisional claims and throw before backing property write |
| Stale structural write crosses an attachment revision | Throw before backing property write |
| Structural setter during any lifecycle callback | Throw before backing property write |
| Explicit attach/detach during any lifecycle callback | Throw before state change |
| Callback-time `AddProperties` targets another owned context | Throw before enumeration or metadata publication |
| Duplicate singleton contract registration | Throw before service snapshot publication |
| `TryAddService` predicate finds an existing match | Return false without factory invocation |
| Late stateful service registration | Publish service; no automatic backfill |
| Duplicate metadata name or rejected structural initial value | Throw before batch publication |
| Lifecycle callback throws | Propagate after releasing locks; no recovery guarantee |
| Backing writer or metadata publisher mutates then throws | Contract violation; no rollback guarantee |
| Interceptor calls `next` more than once | Contract violation; behavior unsupported and unguarded |

## Performance Model

Reads, invokes, scalar writes, and ordinary service resolution gain no topology lock and no ownership graph lookup.

The zero-interceptor read and write paths remain direct cached delegates. An attached scalar write with lifecycle pays the same lifecycle interceptor dispatch present on master, performs the structural type rejection, and forwards immediately.

Structural operations in different contexts run concurrently because each built-in lifecycle has its own lock. Structural operations in one context serialize, which is required for one coherent membership and Registry projection. Callback contracts require handlers to be fast because they execute inside that lock.

The executor attachment-revision check is limited to structural terminals. The private executor monitor already protects terminal writes; the new comparison must not add allocation.

The built-in implementation reuses existing pooled lists and visited sets. The unique-subject collection path stays optimized. Duplicate occurrence reconciliation may allocate or rent additional cold-path multiset state only when duplicates require it.

Removal operations initially pay a complete context-local reachability scan. Benchmarks determine whether an affected-component index is justified. No incremental index is added preemptively.

Fallback removal should substantially simplify context service snapshots, service lookup, chain invalidation, and executor construction. The implementation should measure this benefit rather than preserve unused fallback machinery for compatibility.

## Migration

### Core and generator

- Remove `IInterceptorSubject.Context` and `IInterceptorSubject.SyncRoot`.
- Add `IInterceptorSubject.Executor`.
- Make executor context nullable and store explicit attachment state.
- Stop inheriting `IInterceptorSubjectContext` from `IInterceptorExecutor` and `InterceptorExecutor`.
- Move the subject monitor fully into the executor.
- Add subject context query and explicit attach/detach extensions.
- Add the advanced allocation-free executor context-transition seam.
- Flatten `InterceptorSubjectContext` to local services and remove fallback topology machinery.
- Add `ISingletonContextService<TContract>` validation.
- Update the generator's supported-base contract, member table, diagnostics, and tests for the `Executor` slot and removal of `Context` and `SyncRoot`.
- Update hand-written and mocked `IInterceptorSubject` implementations to provide an executor rather than a context and public monitor.
- Update generated and Dynamic context constructors to call explicit attach.
- Update generated and Dynamic metadata publication to use the lifecycle `AddProperties` seam.

### Tracking

- Keep `LifecycleInterceptor`, `_lastProcessedValues`, traversal helpers, pools, events, and handler dispatch.
- Replace `_attachedSubjects` reference-count ownership decisions with occurrence-aware subject state, explicit roots, and reachability.
- Add one reentrant lock per lifecycle interceptor and hold it across the terminal write. Holding the lock across the terminal is what retires the post-writer concurrent-baseline repair model; terminal ordering is the precondition that makes holding it safe, not the cause. Position in the chain alone changes nothing, because the backing write would still commit outside the lock and two threads writing one structural property would interleave exactly as they do now. The repair must therefore not be removed before the lock scope changes, or the concurrency defect its tests pin is reintroduced. The authoritative getter reread is kept for a separate reason: it is the only place the stored value is read back for setters that normalize.
- Integrate context inheritance and parent membership directly.
- Remove `ContextInheritanceHandler`, `ParentTrackingHandler`, `WithContextInheritance()`, and `WithParents()`.
- Keep `GetReferenceCount()` as a derived lifecycle state query, and reimplement `GetParents()` over lifecycle-published immutable snapshots with occurrence-stable indices. The latter is new work with new tests, not a carry-over.
- Add callback-depth reentrancy validation.
- Make built-in feature extensions establish their required default lifecycle before adding dependent services, while preserving runtime interceptor and handler order through ordering metadata.

### Registry and consumers

- Keep Registry as `ILifecycleHandler` and `IPropertyLifecycleHandler` projection logic.
- Teach property attachment to create missing dynamic Registry property projections.
- Remove the synthetic dynamic structural write and manual property attachment workaround.
- Keep `WithRegistry()` automatically enabling the built-in lifecycle.
- Replace subject `.Context` accesses with `GetContext()` or `TryGetContext()` according to required nullability.
- Replace ownership checks based on reference count with context or Registry membership checks.
- Migrate loaders that currently preattach a child with `AddFallbackContext` to the explicit attach, populate, parent assignment, explicit detach transfer pattern.
- Change `WithSourceMonitoring()` from `WithParents()` to `WithLifecycle()`, order `SourceMonitor` after the merged lifecycle boundary, and consume the lifecycle's authoritative parent state.
- Update SourceMonitor and connector code to consume the preserved edge notifications and context query API.
- Remove or replace fallback-depth benchmarks and executor-as-context casts; retain their relevant service-chain and executor-publication coverage against the flat-context design.
- Accept intentional Public API snapshot changes.

No obsolete members or forwarding aliases are added. The change is one coordinated breaking migration across first-party projects.

## Verification

Implementation follows test-driven development and preserves current tests as the primary compatibility oracle wherever contracts remain unchanged.

Required focused tests include:

- Explicit first attach, inherited-to-explicit promotion, exact detach, inherited retention, duplicate attach, missing detach, and wrong-context failures.
- Context-taking generated and Dynamic constructors as explicit roots.
- Explicit root-only behavior without lifecycle.
- Same-context multiple parents and multiple explicit roots.
- Cross-context scalar subject, collection, dictionary, and dynamic property rejection before backing mutation.
- Self-cycle, larger cycle, shared DAG, multiple-root reachability, and final orphan-cycle release.
- `[a, a, b]` reference counts and occurrence-aware parent/child projections.
- Duplicate collection removal and reorder without false subject detach/attach transitions.
- Exact existing context-handler, subject-handler, event, property-handler, Registry, and SourceMonitor order.
- SourceMonitor branch-scope updates after reparenting without `WithParents()` or a Registry dependency.
- `SubjectAttached` cache population once and `SubjectDetaching` cache eviction once.
- Scalar callback writes, permitted nested same-lifecycle `AddProperties`, permitted unattached `AddProperties`, and rejected structural, explicit, or cross-context metadata callback operations.
- Atomic scalar and structural metadata batches, initial structural getter invocation once, Registry projection before edge publication, and derived structural exclusion.
- Same-context structural concurrency, parallel independent-context structural concurrency, competing cross-context adoption, and stale structural write attachment revision.
- Singleton contracts by interface, repeated same-instance rejection, multi-contract services, reentrant factory validation, and legal late service addition.
- A minimal custom lifecycle implementation using only the public Core seam and no Tracking internals.
- Public API snapshots for every affected package.

Default verification runs:

```powershell
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

The loader transfer changes touch connector implementations, so the implementation plan must include targeted Connector and OPC UA tests in addition to the default suite. Before implementation begins, agree the Connector Tester scope required for this cross-cutting ownership change and confirm it before finalizing the branch. Concurrency tests use event-based coordination, `ManualResetEventSlim`, `CountdownEvent`, or existing async helpers, never hardcoded sleeps.

## Benchmark Acceptance

Read `docs/benchmarking.md` before collecting or interpreting results.

At minimum, compare against the exact implementation base for:

- Zero-interceptor reads and writes.
- Scalar writes with the built-in lifecycle configured.
- Structural scalar-subject replacement.
- Unique-subject collection replacement.
- Duplicate-subject collection replacement and reorder.
- Explicit attach and detach of shallow and deep graphs.
- Removal reachability for DAGs and cycles at representative graph sizes.
- Parallel structural writes within one context.
- Parallel structural writes across independent contexts.
- Registry attach, detach, lookup, and parent/child projection workloads.
- Flat context service lookup and chain rebuild, replacing fallback-delegation-depth cases that no longer represent a supported capability.

Use the existing Registry benchmark filter where applicable:

```powershell
pwsh scripts/benchmark.ps1 -Filter "*RegistryBenchmark*" -LaunchCount 3
```

Add focused lifecycle benchmark rows when the existing suite does not isolate reachability or independent-context concurrency. Scalar paths must remain allocation-neutral. Structural results are reviewed for both CPU and allocation regressions. If complete reachability scans or per-lifecycle contention are operationally material, reopen only the internal algorithm, not the approved public ownership model.

## Deferred Optimizations and Future Extensions

- Replace complete removal reachability scans with affected-component or incremental reachability only after benchmark evidence.
- Shard occurrence reconciliation storage further if duplicate-heavy collections are common.
- Add a runtime continuation guard only if real contract violations justify hot-path cost.
- Reintroduce service composition only through a separately designed mechanism that cannot change subject ownership implicitly.
- Add hosted lifecycle coordination and other singleton authorities as independent features on the generic marker.
- Permit broader callback-time topology operations only with a new design that solves cross-context lock ordering and observable callback consistency.
- A custom lifecycle implementation may choose a different synchronization algorithm, including optimistic or lock-free techniques, without changing the Core attachment contract.

## Acceptance Summary

The change succeeds when subjects have one nullable context, explicit roots and same-context structural edges produce correct ownership across DAGs and cycles, existing callback/cache behavior remains stable, duplicate occurrences become real edges, Registry remains a projection, Core remains policy-light and third-party-extensible, independent contexts retain structural concurrency, and benchmarks show no material scalar-path regression.

The implementation is intentionally simpler than the original roadmap. It gives up fallback service composition, subject-local services, broad structural reentrancy, repeated continuations, transactional callback recovery, incremental cycle release, and route/authority machinery in exchange for a direct model that matches the primary requirement.
