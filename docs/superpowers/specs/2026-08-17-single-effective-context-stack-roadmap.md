# Single Effective Context Stack Roadmap

**Date:** 2026-08-17

**Status:** Revised approved direction; detailed designs are approved one pull request at a time

## Purpose

Namotion.Interceptor currently uses fallback contexts for service composition, lifecycle attachment, and parent-context inheritance. One topology mutation can therefore change service resolution, ownership, reference membership, cache invalidation, and authority selection together.

The replacement separates those responsibilities through four stacked pull requests:

```text
master
  -> PR #474: internal effective ownership-route state
       -> PR #419: explicit ownership and global topology serialization
            -> PR #472: stable unique authorities
                 -> PR #440: hosted lifecycle coordination
```

The experimental #419, #472, and #440 branches remain sources of tests and reproduced schedules. Their production implementations are not transplanted wholesale. PR #474 is lifecycle- and route-behavior-neutral infrastructure with one reference-identity service-materialization correction. PR #419 is the coordinated semantic cutover. PR #472 generalizes authority identity. PR #440 builds hosted lifecycle state on the resulting ownership and authority model.

## Stack-Wide Acceptance Criterion

Every pull request in this stack satisfies one concurrency and failure rule:

> no library-controlled ownership/context/lifecycle operation may fail solely from transient state, contention, unfinished transitions, stale snapshots, or timing; library waits/retries/restarts or yields a valid serial result; any library-thrown failure remains failure on immediate retry with unchanged args and committed observable application state.

Caller cancellation, user or external exceptions, permanent semantic incompatibility, and explicit programming-contract violations remain valid failures. A permanently nonconverging direct ownership reader or sustained adversarial scheduler contention is an unsupported liveness condition, not a finite library retry failure.

This rule applies to #474 route publication, #419 ownership and lifecycle coordination, #472 unique-authority publication, and #440 hosted start, stop, refusal, cancellation, and drain. Each detailed design must identify its valid persistent failures, internal retry boundaries, and deterministic no-transient-failure tests without restating this criterion.

## Shared Model

### Separate concepts

The final model keeps these concepts distinct:

- **Object-reference graph:** subject relationships formed by properties, collections, dictionaries, and dynamically added structural metadata.
- **Lifecycle ownership:** the configured-context domain responsible for a subject. Explicit attachment anchors a root. Committed property references establish or retain inherited ownership.
- **Context resolution:** subject-local services, public composition fallbacks, and one internal effective ownership route.
- **Unique authority:** a service contract for which an active effective context may expose zero or one exact instance.
- **Registry projection:** an optional observer of committed lifecycle membership and object-reference relationships, never an ownership source of truth.
- **Hosted lifecycle:** start, stop, cancellation, and drain state projected from committed ownership.

One configured context may own several disconnected explicit roots. They share configured authorities without becoming one object-reference graph.

### Subject executor and routes

Every subject retains its own `InterceptorExecutor`, subject-local services, cached chains, metadata, and one private property/state monitor. A child never replaces its executor with a parent context. Its one internal ownership route targets the selected parent executor and carries the exact configured-context ownership-domain identity. `IInterceptorSubject` no longer exposes `SyncRoot`; there is no supported public atomic-lock snapshot capability and no second per-subject lock.

Explicit attachment, lifecycle branch inheritance, and fallback composition are separate relationships:

1. explicit attachment creates one root anchor in an exact plain configured context;
2. when and only when the ownership domain resolves exactly one `ILifecycleInterceptor` identity, that canonical coordinator performs branch inheritance and selects one committed compatible parent route when no explicit route wins; `WithLifecycle()` is the standard built-in registration, not a concrete-type gate;
3. fallback composition aggregates services and has no lifecycle meaning.

Resolution order remains local services, public fallbacks, then the one internal ownership route. Ordering attributes apply to the complete gathered service set. Service materialization deduplicates by reference identity: repeated discovery of the same exact instance collapses to its first ordered occurrence, while distinct instances remain distinct even when `Equals` reports equality. Nonunique services remain intentionally plural.

### Global topology serialization

PR #419 introduces one process-wide reentrant ownership/topology turn. Potentially structural writes enter it before action selection. Explicit attach/detach, lifecycle ownership and reconciliation, route publication, context service/fallback mutation, and dynamic property metadata mutation share the same turn. Nested synchronous work runs inline through monitor reentrancy. External callers wait before taking lower-ranked locks and re-evaluate committed state when admitted.

Reads, invokes, scalar writes, and lock-free service resolution do not enter this turn. The common one-global-context consumer therefore replaces several interacting synchronization mechanisms with one allocation-free warmed stable-topology structural turn. An actual route install, replacement, transfer, or clear retains PR #474's fresh immutable route/state publication cost. Disconnected domains intentionally lose parallel structural and configuration throughput. Their scalar and read work remains independent.

Zero-interceptor reads remain the current lock-free direct delegate call. An intercepted read terminal and every scalar write terminal use only the executor's private property/state monitor. Structural and metadata operations take the topology turn and then one executor monitor. Generated, Dynamic, and manual subjects expose no monitor. This removes the public atomic-lock snapshot capability and the possibility of caller-created root-to-global lock inversion.

The topology monitor provides mutual exclusion and reentrancy, not FIFO admission or starvation freedom. The acceptance criterion assumes finite contention and ordinary scheduler progress. Sustained adversarial contention is an unsupported liveness condition; PR #419 does not add an allocation-heavy fair queue.

### Runtime mutation

A context mutation may publish while subjects are active when every affected active domain retains the same exact unique-authority identity set. The current operation pins its immutable interceptor and handler arrays; legal mutation affects future operations. A mutation that changes an active authority set rejects before state publication as a permanent semantic incompatibility.

PR #419 applies this rule to lifecycle coordinator identity. PR #472 generalizes the same publication test to every declared unique authority. `TryAddService` predicate/factory behavior, fallback return values, input enumeration, and invalidation remain synchronous. Internal retry never replays one API invocation's user callback or input materialization.

The topology turn is reentrant across contexts. A `TryAddService` predicate or factory may synchronously mutate another built-in context; nested work runs inline and each predicate/factory is invoked exactly once per public call. Every internal route mutator enters the topology turn itself before taking a context lock, so direct callers cannot bypass serialization.

`AddProperties` remains supported at any time and participates in the topology turn. One public call is one atomic, once-materialized metadata batch. Every metadata entry added to a lifecycle-owned subject receives its ordinary property lifecycle attachment in committed input order. Only direct-readable structural metadata performs ownership discovery and membership work. A route-free or coordinator-free publication performs neither lifecycle nor ownership work; a later lifecycle attach initializes the then-current committed metadata in order. Registry binds an exact pending `RegisteredSubjectProperty` to an exact one-shot Core commit notification. Core claims that identity as the first `AddProperties` entry action before enumeration or application code, keeps it publicly invisible before commit, and promotes that exact wrapper at Core commit before later user callbacks.

## PR #474: Internal Effective Ownership-Route State

### Contract

PR #474 provides one immutable internal ownership-route descriptor, exact-descriptor compare-and-publish, route-aware service resolution, reverse invalidation, delegation, and cycle handling independently from public fallbacks.

Acceptance is governed by the [stack-wide acceptance criterion](#stack-wide-acceptance-criterion). Exact-descriptor comparison additionally ensures an old descriptor cannot clear a later descriptor even when both target the same context.

### Included

- Store route target and ownership-domain identity in immutable context state.
- Retain the route-free base state shape and allocate routed state only on successful route publication.
- Resolve the route after public fallbacks.
- Keep a public fallback and internal route independently removable when both target the same context.
- Preserve reverse dependency registration until the final relationship disappears.
- Cover exact compare, repeated paths, delegation, cycles, deep graphs, invalidation, and concurrent route replacement.

### Capability and release

No public capability changes. PR #474 is independently releasable and gives #419 the only route publisher it may use.

## PR #419: Explicit Ownership and Lifecycle Reconciliation

### Contract

PR #419 makes fallback mutation composition-only and establishes strict explicit roots, one ownership domain, one effective route, a permanent Core membership ledger, prospective structural admission, and recursive lifecycle reconciliation.

Acceptance is governed by the [stack-wide acceptance criterion](#stack-wide-acceptance-criterion). All potentially structural and topology-changing library work uses the process-wide turn described above.

### Included

- Add strict `AttachToContext`, `DetachFromContext`, and boolean/out `TryGetAttachContext` APIs.
- Allow a plain configured context with no lifecycle coordinator to own and route an explicitly attached root and compose services, but perform no implicit child discovery, adoption, release, or structural-setter lifecycle work in that domain.
- Make fallback add/remove pure service composition.
- Enforce zero or one lifecycle coordinator identity across every active effective context.
- Replace Tracking root sentinels and boxed reference counts with Core explicit-anchor and property-membership state.
- Reserve complete structural changes before backing-field commit, then commit one exact generation.
- Cancel a complete tentative outer batch when a nested structural write targets one of its reserved subjects. The nested write commits independently under current committed/route-free ownership; the outer Core boundary restarts discovery from final values without replaying interceptor prefix, backing writer, lifecycle callback, context callback, or input materialization.
- Preserve later committed nested generations. Every callback or publication phase remains an exact dependency-scoped obligation: a raw generation or route-descriptor mismatch never drops a valid phase, while an exact later operation explicitly adopts and replaces only obligations whose subject or selected-parent route dependency it invalidates. Semantically valid older pinned tails continue exactly once.
- Defer a downstream write-interceptor exception that occurs after terminal commit until Tracking records the committed baseline and reconciles and Core finalizes. The original downstream exception remains primary.
- Preserve position-dependent repeated-continuation behavior: repetition upstream of the coordinator creates and settles one invocation per call; uninterrupted repetition downstream performs ordered stores in one settlement segment and reconciles once to the final successfully stored value. A supported nested topology operation first settles a committed pending segment, then continues the same coordinator invocation in a new segment. A later failure settles every earlier successful store before the original failure escapes.
- Preserve cycles, shared DAGs, repeated collection references, ordered compatible parents, deterministic transfer, and explicit-root precedence.
- Make the exact single `ILifecycleInterceptor` resolved by an ownership domain its canonical recursive-ownership coordinator. `WithLifecycle()` installs the supported official Tracking provider, which must be the reference-identical `ILifecycleHandler` occupying the one logical coordinator slot. Zero instances mean explicit-root-only operation and two distinct instances are incompatible. Remove optional inheritance configuration and functional `ContextInheritanceHandler` behavior.
- Keep the normally visible public provider facade capability-minimal and stack-only, generic where write values require it, and reservation-oriented. Core alone gates, commits, restarts, cancels, and finalizes. Official Tracking owns traversal and reconciliation. Existing friend access could internalize the seam, but the stack deliberately keeps the existing public lifecycle contract and its operation/view types as versioned advanced public API instead of adding a parallel internal interface or adapter. Documentation discourages third-party implementation; changes still follow the package's normal breaking-change/version policy.
- Cut generated and Dynamic context constructors over to strict explicit roots. Parameterless children inherit through committed parent routes only in lifecycle-coordinated domains.
- Preserve the canonical Core/generator structural classifier delivered immediately before PR #419.
- Update first-party factories, connectors, OPC UA, samples, HomeBlaze, tests, snapshots, and user documentation at their planned stack boundaries.

The #419 cutover is one no-stub atomic boundary. Every final test and Verify oracle is authored before production edits. Existing-surface tests and changed oracles that remain compilable are run to an intended semantic RED first. Once tests reference the absent final public ownership types, each affected project is recorded honestly as compilation RED; that result does not claim its semantic methods executed. The remaining final rows are source-reviewed against the closed manifest and all semantic behavior is proved together at the one complete final GREEN gate. The one commit stages the exact closed manifest, including every compiled `SyncRoot` and inheritance consumer, and post-stage and post-commit status gates prevent those changes from leaking into a later pull-request task.

### Persistent failures and capability losses

- duplicate explicit attach, missing/wrong detach, unsupported attach target, incompatible ownership domains, and authority-changing active publication fail persistently before commit;
- an active initiating structural interceptor prefix cannot synchronously attach or detach its own subject, and an active route-free structural prefix cannot synchronously change its own coordinator, because an already selected structural chain cannot be spliced or replayed; scalar prefixes retain current behavior, complete their already selected chain, and affect only future operations;
- a lifecycle callback cannot synchronously cause a true inverse ownership transition of the same subject whose callback is executing; that exact callback context is a programming-contract violation, while nested topology involving other subjects remains supported;
- generated/custom backing writers must be synchronous direct stores;
- callbacks, factories, ownership readers, and structural traversal must not synchronously wait for another thread that needs the topology turn;
- ownership discovery reads must be synchronous topology-free direct reads of intercepted storage, and every structural change to that storage must flow through its gated setter or `AddProperties`; computed/external/scalar-dependent getters are not automatic ownership edges;
- the public subject monitor and atomic-lock snapshot capability are removed;
- disconnected domains lose parallel structural and configuration throughput;
- fallback mutation no longer attaches, detaches, or inherits;
- several explicit roots on one subject, unrelated parent domains, stealing an owned factory child, and configurable shallow lifecycle are removed.

The complete state machine, visibility/error tables, lock order, memory ownership, tests, and performance gates live in `2026-08-18-explicit-subject-ownership-design.md`.

### Release

PR #419 is a coordinated binary-semantic breaking release on #474. Core, Tracking, Generator, Dynamic, affected connectors, and rebuilt consumer models ship together. It does not depend on #472 markers or #440 hosting changes.

## PR #472: Stable Unique Authorities

### Contract

PR #472 generalizes #419 lifecycle-coordinator identity to every declared `IUniqueContextService<TContract>` authority. It reuses the same global topology turn, active-domain record, prospective context publication, reverse dependency walk, and exact reference-identity set comparison.

Acceptance is governed by the [stack-wide acceptance criterion](#stack-wide-acceptance-criterion).

### Included

- Declare and audit lifecycle, registry, transaction, hosting, and other authority contracts.
- Allow the same exact instance through repeated paths and reject distinct instances for one authority contract.
- Preserve legal late nonunique service/fallback mutation.
- Validate prospective authority sets before immutable-state publication.
- Keep input factory/materialization semantics exact and non-replayed.
- Migrate first-party authority configuration to bootstrap where late change is permanently incompatible.

### Capability and release

An active effective context cannot acquire, replace, or combine a different unique authority. Independent domains may use different authorities. Nonunique branch services remain mutable. PR #472 is independently releasable on #419 and settles issue #466 without changing hosted drain behavior.

## PR #440: Hosted Lifecycle Coordination

### Contract

PR #440 adds one unique hosting authority per ownership domain and one atomic acceptance/drain boundary. Per-target state serializes start and stop while unrelated targets may progress when no ownership/topology turn is required.

Acceptance is governed by the [stack-wide acceptance criterion](#stack-wide-acceptance-criterion).

### Included

- Serialize start/stop per target and deduplicate concurrent requests.
- Define awaited attach, detach, refusal, cancellation, failure, and drain completion.
- Take the drain snapshot under the same state transition that closes admission.
- Observe/log drain and cancellation callback failures without losing shared completion.
- Preserve caller ownership of service and subject objects.
- Keep factory APIs internal until a production consumer requires a public surface.

### Capability and release

Several hosting handlers cannot compete for one active domain, and no start is accepted after the committed drain boundary. PR #440 is independently releasable on #472.

## Documentation, Verification, and Performance

`docs/interceptor.md` becomes the canonical user-facing ownership/context model in PR #419. Feature documents link to it rather than duplicating the glossary. PR #472 adds unique-authority timing. PR #440 adds hosted transition and drain semantics.

Every pull request receives an approved detailed design and plan, starts from an exact base, uses TDD, preserves ordinary callback order unless its design names a phase change, runs focused and full non-integration suites, inspects Public API snapshots, and receives independent review. Connector changes additionally run targeted integration and the agreed Connector Tester scope.

Performance acceptance for the stack is:

- scalar writes, reads, invokes, and cached service resolution are shape-neutral and allocation-neutral;
- warmed stable-topology structural writes allocate no managed bytes beyond configured application work;
- actual route attach, detach, and transfer/reparent may allocate PR #474's fresh immutable route/state, reverse fan-out snapshots, prospective service-walk/cache/`ImmutableArray` results, invalidation generations, and first-use retained capacity, and are measured separately against exact master and the cleaned PR #474 base;
- `InitializedContextZeroReadInterceptors` is a lock-free source/disassembly and stable-machine control at master, cleaned PR #474, and final PR #419;
- the normal one-global-context case is no slower than exact master outside stable-machine control noise, otherwise the design reopens;
- PR #419 records the intentional two-domain structural/configuration throughput loss;
- connector and HomeBlaze convoying is measured and reopens the design if operationally material;
- local timings are diagnostic; the stable benchmark machine is authoritative.

## Whole-Stack Success Criteria

- #474 route publication, #419 ownership, #472 authority mutation, and #440 hosting satisfy the stack-wide acceptance criterion.
- Fallback composition never owns subjects or invokes lifecycle callbacks.
- Every subject has at most one explicit anchor, one ownership domain, and one effective route.
- Parent membership, Tracking baselines, registry/parent projections, routes, and callbacks agree with committed structural values at quiescence.
- Cycles, shared DAGs, repeated references, compatible multiple parents, and final component release remain supported.
- Active contexts retain legal late nonunique mutation and reject only persistent unique-authority changes.
- `AddProperties` remains dynamically usable as one atomic batch with synthetic structural reconciliation where required.
- Hosted targets have deterministic start, stop, cancellation, failure, refusal, and drain outcomes.
- The one-context stable-topology path remains allocation-free and performance-neutral under the stated benchmark gates; route-mutation rows remain within the separately stated PR #474-relative gates.
