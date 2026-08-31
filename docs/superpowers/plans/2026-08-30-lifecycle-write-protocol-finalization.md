# Lifecycle and Structural Write Protocol Finalization Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish PR #494 with one race-safe lifecycle protocol that preserves pre-terminal interceptor rewriting, removes the whole-chain gate and duplicate topology machinery, and is materially smaller than the reviewed PR.

**Architecture:** The current branch already has immutable structural snapshots, executor structural leases, and exact same-context ownership reservations. The remaining design freezes the final intercepted value at the terminal, performs a faithful raw store and immutable graph publication in one short terminal-lock plus topology-gate transaction, treats leases and reservations as temporary reachability roots, and drains immutable revisioned journals after releasing every framework lock.

**Tech Stack:** C# 13 preview, .NET Standard 2.0 Core, .NET 9 Tracking and consumers, xUnit, Verify, System.Collections.Immutable.

**Spec:** [`docs/superpowers/specs/2026-08-30-lifecycle-write-protocol-redesign-design.md`](../specs/2026-08-30-lifecycle-write-protocol-redesign-design.md)

## Local implementation checkpoint

Tasks 1 through 6 and the implementation, simplification, audit, and verification portions of Task 7 have been completed on the local comparison branch. Public lifecycle documentation remains deferred until the maintainer chooses this candidate for integration, and benchmarks remain deferred until the maintainer approves a performance follow-up. The implementation keeps one ownership graph, one immutable lifecycle journal, one per-subject attachment authority, trusted raw structural reader/writer routing, exact leases and reservations as temporary roots, and callbacks outside framework locks.

Deterministic race work added seven necessary refinements to the original plan:

- ordinary scalar writes cross short pre-chain lifecycle publication admission and reject before interceptor side effects while the subject is non-stable or exclusively reserved;
- a derived target temporarily fenced after its source committed coalesces and resumes one guarded synthetic notification instead of losing the notification or throwing back through the committed source write;
- an ordinary derived retry uses exact attachment and recalculation sequence acquisition, so stale handoff cannot duplicate an already covered publication;
- Registry retains immutable collection refresh publication, with separate attachment and parent projection revisions, so reorder and rekey updates cannot overwrite newer attachment state or enumerate live collections under Registry locks;
- Registry removal ignores a missing property only when its parent subject is already absent, so cyclic detach converges without hiding a missing property on a registered parent;
- generated structural access retains coordinated prepublication revisions while preserving historical timestamp `0` until the first successful attachment; and
- commit markers classify the attempted source before final origin resolution, preserving source delivery and echo-suppression semantics when an inbound write resolves to a final local origin.

Fresh local verification on 2026-08-31 is Core 196/196, Tracking 761/761, Registry 193/193, Generator 273/273, Dynamic 11/11, Connectors 738/738, and ConnectorTester 117/117. Focused derived deferral, rollback, exclusive admission, stale retry, graph capture, reservation, generated timestamp, source-marker, and Registry cycle tests are green. The complete non-integration solution test exited successfully. Debug and Release solution builds completed with zero warnings and zero errors. Release pack completed after the Release build, with only the expected warnings from projects that disable packaging.

The original simplification budget is not met. Against latest PR branch `c5079c6f`, the five production projects are `+4,977/-3,384`, net `+1,593`; against current `origin/master` at `082bb1ce`, they are `+7,089/-2,394`, net `+4,695`. Core plus Tracking are net `+1,676` versus that PR branch and net `+4,261` versus master. Independent simplification review accepted the protocol design and the safe reductions already made, but found no remaining sizeable deletion that did not remove a distinct correctness invariant. This checkpoint must therefore be reviewed as a correctness candidate against the current PR, not treated as the final size-approved implementation.

## Global Constraints

- Continue on local branch `codex/pr-494-lifecycle-protocol-implementation`, whose reviewed PR baseline is `c5079c6f0cb3a06ea2bc395e2dba7b812b3fa88b` and current master comparison is `082bb1cee82f2428fe8e94839294b5405138d79c`.
- Preserve the public `PropertyWriteContext<TProperty>.NewValue` setter. Interceptors may rewrite the value committed by the write only while forwarding the received context by `ref` toward one terminal invocation. Terminal entry shallow-snapshots it; later assignments remain context-local unwind state and have no commit or built-in publication effect.
- Do not replay an interceptor chain. A veto creates no terminal revision, proposal reservation, property topology delta, or write journal. Its final lease release may still finish a deferred sweep caused by another committed operation.
- Generated, opted-in Dynamic, and advanced hand-written structural raw writers must be faithful, synchronous, nonblocking, non-reentrant, and exception-free. Normalization belongs in `On<Property>Changing` or an interceptor.
- The only nested framework-lock order is terminal `SyncRoot`, then the context topology gate, then one executor attachment monitor. A path without `SyncRoot` starts at the topology gate. No topology path may request `SyncRoot`, and no executor monitor may be retained while requesting the topology gate.
- Interceptors, ordinary getters, enumerable traversal, equality implementations, metadata input iterators and publishers, lifecycle handlers, property handlers, Registry callbacks, derived recalculation, and events run outside framework locks. The contract-bound raw reader and faithful writer are the only callout exceptions.
- Resolve and cache origin equality and write timestamp before framework locks. Generated structural prepublication writes deliberately cache timestamp `0` until the first successful attachment. Inside the final commit, use only the cached result, trusted raw reader/writer, revision stamping, and pure immutable state swaps.
- Preserve scalar generated code and public routing shape. With lifecycle active, scalar writes also cross the executor's short pre-chain publication admission so non-stable and exclusive phases reject before arbitrary interceptor side effects. Structural generated accessors use the coordinated reader/writer entry even while detached.
- `StructuralSnapshotBuilder` is the only raw structural-value interpreter. `OwnershipGraph` stores immutable occurrence snapshots and library-owned records only.
- Use subject reference plus child-specific occurrence ordinal as edge identity. Indexes and dictionary keys are payload, never identity.
- Active leases and same-context reservations are temporary reachability roots. A removal may commit while retaining their protected closure; a single deferred-sweep flag triggers final reachability after the last relevant protector leaves. Do not add pending-release groups, closure merges, topology freezes, or general topology retry queues. One target-local deferred synthetic derived notification is permitted when its source already committed and target pre-chain admission is temporarily fenced.
- Lifecycle callbacks are synchronous for their originating operation and outside locks. Topology-changing callback reentry and same-thread second-context topology entry remain rejected. Journals from different threads may overlap.
- No hardcoded waits in tests. Use `ManualResetEventSlim`, `Barrier`, `CountdownEvent`, or `AsyncTestHelpers.WaitUntilAsync` with bounded joins.
- Follow the repository's `When<Condition>_Then<ExpectedBehavior>` naming and Arrange, Act, Assert comments.
- Do not accept a Verify snapshot until its semantic diff has been reviewed.
- Do not commit a task until its focused tests pass and `git diff --check` is clean.
- Do not run long benchmarks, push, or update PR comments until the user reviews the verified local branch.

## Starting Point and Deletion Budget

The following foundations are already committed and independently reviewed:

- Immutable occurrence snapshots: `addf0e11` and `64367d34`.
- Atomic attachment state and structural leases: `72f76d44`, `fffe06d9`, and `30aab1db`.
- Exact reference-counted ownership reservations: `62b2b821`, `43939822`, and `23d4a54b`.

At `23d4a54b`, Core plus Tracking are `+2,967` production lines over master, and Core, Tracking, Generator, Registry, and Dynamic together are `+3,538`. The latter scope is `+444` over PR head and `+15` production files over master. Any later change to linked production code under `src/Shared`, especially `TypedPropertyWriteFactory.cs`, is counted explicitly beside this five-project scope rather than escaping the path-based budget. Completion requires:

- Core plus Tracking at `+2,300` lines or less over master.
- The five-project scope at `+2,800` lines or less over master.
- A materially negative five-project delta versus PR head.
- At most `+12` production files over master.

The remaining tasks therefore target at least 760 Core-plus-Tracking lines and 800 five-project lines removed from the current head. No remaining task adds a production file. Task 4 deletes the standalone callback guard and Task 6 deletes the four recursive topology helpers, taking the file delta from `+15` to `+10`.

## Final File Responsibilities

- `IWriteInterceptor.cs`: per-call write state, mutable-before-terminal `NewValue`, terminal freeze, exact predecessor, origin, and public interceptor contract.
- `WriteInterceptorFactory.cs`: one terminal dispatch path for zero or many interceptors.
- `InterceptorExecutor.cs`: attachment publication, structural lease/reservation ownership, terminal lock, raw commit stamping, and the Core-owned pre-chain logical context scope.
- `ILifecycleInterceptor.cs`: existing public lifecycle surface plus internal friend-visible terminal and topology-admission coordinator contracts.
- `LifecycleInterceptor.cs`: orchestration of structural capture, reservation, commit, attach, detach, admission, and journal dispatch.
- `StructuralSnapshotBuilder.cs`: the only user-value-to-occurrence interpreter.
- `OwnershipGraph.cs`: the only reachability, topology-delta, anchor, protector, and deferred-sweep engine.
- `SubjectOwnership.cs`: one immutable reader-visible subject ownership publication.
- `LifecycleNotifier.cs`: immutable journals, callback scope, ordering, exception draining, and deferred-sweep diagnostics.
- `PropertyAdmission.cs`: atomic `AddProperties` capture, validation, and publication only.
- `SubjectRegistry.cs`: revision-checked application of complete immutable projections.

---

## Task 1: Freeze the final intercepted value and exact predecessor

**Budget:** At most `+20` production lines from current HEAD. No production files added.

**Files:**

- Modify: `src/Namotion.Interceptor/Interceptors/IWriteInterceptor.cs`
- Modify: `src/Namotion.Interceptor/Cache/WriteInterceptorFactory.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs`
- Modify: `src/Namotion.Interceptor.Tests/InterceptorTests.cs`
- Modify: `src/Namotion.Interceptor.Tests/StructuralWriteTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Change/PropertyChangeInterceptorTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Change/DerivedFinalValueTests.cs`

**Interfaces:**

- Produces: `internal TProperty FreezeNewValue()`, `internal void SetTerminalPredecessor(TProperty value)`, and an internal terminal-committed marker on `PropertyWriteContext<TProperty>`.
- Preserves: public `TProperty NewValue { get; set; }` and public get-only `CurrentValue` shape.

- [ ] Add tests for ordered pre-`next` rewrites, context-local mutation after `next`, a second `next`, veto, public `IsWritten` forgery/suppression, and custom post-`next` exception. Assert only one terminal revision; `NewValue` may show the later custom mutation, but `GetFinalValue()` and built-in publications keep the frozen terminal value.

```csharp
[Fact]
public void WhenInterceptorsRewriteBeforeNext_ThenTheLastRewriteIsFrozenAndStored()
{
    // Arrange
    var logs = new List<string>();
    var context = InterceptorSubjectContext
        .Create()
        .WithService(() => new TestWriteInterceptor("a", logs), _ => false)
        .WithService(() => new TestWriteInterceptor("b", logs), _ => false);
    var car = new Car(context);

    // Act
    car.Speed = 5;

    // Assert
    Assert.Equal(7, car.Speed);
}
```

- [ ] Run the new tests before implementation. Expected: ordered rewriting passes, while frozen built-in publication, internal commit authority, and second-terminal rejection fail.
- [ ] Replace the `NewValue` and `CurrentValue` auto-properties with private backing fields. Keep mutable `_newValue`, add `_terminalValue`, `_terminalEntered`, and `_terminalCommitted`. `FreezeNewValue()` rejects a second terminal and shallow-captures `_newValue`; the public setter remains usable. `SetTerminalPredecessor` updates only the internal current-value field.
- [ ] Make `GetFinalValue()` return frozen `NewValue` after terminal entry, including derived backing-store writes. It must not invoke a derived getter during unwind.
- [ ] Route executor return values and built-in property-change and derived decisions through the internal committed marker. Keep public `IsWritten` as compatibility observation only.
- [ ] Keep public API snapshots unchanged and run Core tests.

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --no-restore --filter "FullyQualifiedName~InterceptorTests|FullyQualifiedName~StructuralWriteTests|FullyQualifiedName~VerifyChecksTests.PublicApi"
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore --filter "FullyQualifiedName~PropertyChange|FullyQualifiedName~Derived"
git diff --check
```

- [ ] Record the production delta and commit.

```bash
git add src/Namotion.Interceptor src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests
git commit -m "fix: freeze interceptor values at the write terminal"
```

## Task 2: Move structural lifecycle work to one terminal coordinator

**Budget:** At most `+120` cumulative production lines from current HEAD after Task 1. This task establishes the final graph/journal seam before deleting its migration adapters. No production files added.

**Files:**

- Modify: `src/Namotion.Interceptor/Interceptors/IWriteInterceptor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/ILifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor/Cache/WriteInterceptorFactory.cs`
- Modify: `src/Namotion.Interceptor/Cache/WriteInterceptorChain.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Verify unchanged: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`; the current local raw-reader/writer output is sufficient
- Modify: `src/Namotion.Interceptor.Dynamic/DynamicSubjectFactory.cs`
- Modify: `src/Shared/TypedPropertyWriteFactory.cs`
- Modify: `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubject.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleNotifier.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Modify temporarily as adapters: `src/Namotion.Interceptor.Tracking/Lifecycle/StructuralReconciler.cs`
- Modify temporarily as adapters: `src/Namotion.Interceptor.Tracking/Lifecycle/AttachTraversal.cs`
- Modify temporarily as adapters: `src/Namotion.Interceptor.Tracking/Lifecycle/ReleaseTraversal.cs`
- Modify temporarily as adapters: `src/Namotion.Interceptor.Tracking/Lifecycle/PropertyAdmission.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/OwnershipGraph.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectOwnership.cs`
- Modify temporarily as adapters: `src/Namotion.Interceptor.Tracking/Lifecycle/StructuralReconciler.cs`
- Modify temporarily as adapters: `src/Namotion.Interceptor.Tracking/Lifecycle/AttachTraversal.cs`
- Modify temporarily as adapters: `src/Namotion.Interceptor.Tracking/Lifecycle/ReleaseTraversal.cs`
- Add test: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/TerminalBoundaryCoordinatorTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/TerminalStoreContractTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/CrossContextGateDeadlockTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/DynamicPropertyTypedChainTests.cs`
- Modify: affected Core, Tracking, Dynamic, and Registry public API snapshots

**Interfaces:**

- Produces, inside `ILifecycleInterceptor.cs`, an internal friend-visible coordinator:

```csharp
internal interface IWriteTerminalCoordinator
{
    void ExecuteTerminal<TProperty>(
        ref PropertyWriteContext<TProperty> context,
        Func<IInterceptorSubject, TProperty>? readValue,
        Action<IInterceptorSubject, TProperty> writeValue);
}
```

- Produces a second internal Core contract implemented only by built-in Tracking:

```csharp
internal interface ITopologyAdmissionCoordinator
{
    StructuralWriteLease AcquireStructuralWriteLease(InterceptorExecutor executor);
    Exception? CompleteStructuralWrite(InterceptorExecutor executor, StructuralWriteLease lease, Exception? primaryException);
    OwnershipReservationToken AcquireOwnershipReservation(InterceptorExecutor executor, ReservationMode mode);
    void CompleteOwnershipReservation(InterceptorExecutor executor, OwnershipReservationToken token, bool retainCommittedOwnership);
}
```

- Produces on `InterceptorExecutor`: `internal void CommitRawWriteLocked<TProperty>(ref PropertyWriteContext<TProperty> context, TProperty value, Action<IInterceptorSubject, TProperty> writeValue)`, which requires `SyncRoot` and owns the internal/public committed flags, revision, and cached origin/timestamp write-state stamping.
- Produces the initial immutable `LifecycleJournal` in `LifecycleNotifier.cs`; Task 4 completes all callback variants and revisioned consumer projections without creating a second journal type.
- Produces nested `PreparedTopologyChange` plus `OwnershipGraph.PrepareWrite`, `Publish`, and `PrepareDeferredSweep`. The old helpers may call these through narrow adapters until Tasks 5 and 6 remove their final attach/admission callers.
- Consumes: Task 1 freeze/predecessor methods and the current generated `SetGeneratedPropertyValue` reader/writer shape.

- [ ] Add deterministic tests for downstream structural replacement, initially foreign rewritten to local, initially local rewritten to foreign, rewrite plus veto, downstream same-context worker completion, post-`next` exception with lifecycle journal drain, parent lease retained through full unwind, pre-admission second-context rejection, stale outer Registry journal after a nested newer write, and two concurrent generated writers terminalized in controlled order. The cross-context probe must assert the foreign topology coordinator, gate, and lease admission were never entered.
- [ ] Add a lock-callout test whose origin equality and configurable timestamp provider wait for a worker acquiring the terminal lock. It must complete because both values are resolved before `SyncRoot`.
- [ ] Convert `TerminalStoreContractTests` so legal normalization happens in `IWriteInterceptor`. Keep one invalid raw-writer test documenting that substitution or mutate-then-throw is outside the coordinated contract.
- [ ] Run the new tests before implementation. Expected: the worker-wait test exposes the whole-chain gate and final-value lifecycle assertions fail because lifecycle currently prepares before downstream rewriting.
- [ ] Store the generated/Dynamic raw reader, installed coordinator, and committed immutable lifecycle journal as internal per-call fields. `LifecycleInterceptor.WriteProperty` validates classification, installs itself as coordinator, calls `next` once, and drains only the journal produced by that call after downstream unwind. Use catch/finally so a downstream post-commit exception cannot strand the journal; preserve it as the primary exception if callbacks also fail.
- [ ] Read and pin the apparent attachment/context route without entering its topology gate. Establish or validate the logical context scope immediately afterward and before calling the topology admission coordinator. Acquire the lease, revalidate the pinned route, and if attachment changed, release lease and scope and retry before resolving any interceptor. Retain the successful scope through full chain and explicit lease completion. Tracking marks callback depth on this Core scope later; Core must not reference `LifecycleNotifier`.
- [ ] Route lease and reservation admission/disposal through `ITopologyAdmissionCoordinator`, which takes topology gate then one executor monitor and revalidates attachment. Each token retains the exact coordinator needed after an attachment change. `CompleteStructuralWrite` performs and drains a deferred sweep outside locks and aggregates its failures with the primary chain exception; token `Dispose` remains a no-throw fallback.
- [ ] Add the prepared immutable graph publication and initial journal representation before cutting over the terminal. Keep old attach/detach/admission callers green through narrow adapters only; name their deletion in Tasks 5 and 6 and add no second committed graph state.
- [ ] Fold duplicate terminal bodies into one executor terminal call. Freeze `NewValue`, resolve/cache timestamp and final origin outside locks, and dispatch either to the structural coordinator or the direct scalar/raw path.
- [ ] In the structural coordinator, capture the frozen immutable snapshot and reserve its component outside locks. Acquire parent `SyncRoot`, reread and set the exact predecessor, acquire topology gate, revalidate the active lease/context/phase and captured participants, rebase against the latest committed snapshot, prebuild every fallible publication, call the faithful raw writer with the frozen local, stamp the internal commit marker and revision, publish nonthrowing swaps, and return the journal without invoking it.
- [ ] Make `DynamicSubjectFactory`, `TypedPropertyWriteFactory`, and `RegisteredSubject` structural properties use the same trusted reader/writer route by declared metadata type. Reject attached legacy or setter-only structural entries in the executor before resolving or executing their interceptor chain or raw writer. Preserve scalar and boxed scalar routing.
- [ ] Keep generator production output unchanged in this task. Its existing structural raw reader/writer call is the required hook; inspect generated snapshots to prove no additional lifecycle API is emitted into subjects.
- [ ] Remove `EnterStructuralWriteGate`, `ExitStructuralWriteGate`, executor whole-chain wrapping, proposal-versus-actual reconciliation, post-store authoritative capture, and duplicate terminal stamping.
- [ ] Run and inspect the Core public API snapshot for removal of the two public gate methods. Internal coordinator types must not appear in the public snapshot.

```bash
rg -n "EnterStructuralWriteGate|ExitStructuralWriteGate|IsTheProposedValue|PendingTerminal|PendingStructuralWrite" src/Namotion.Interceptor src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Dynamic src/Namotion.Interceptor.Registry src/Namotion.Interceptor.Generator src/Shared
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore --filter "FullyQualifiedName~TerminalBoundaryCoordinatorTests|FullyQualifiedName~TerminalStoreContractTests|FullyQualifiedName~CrossContextGateDeadlockTests"
dotnet test src/Namotion.Interceptor.Dynamic.Tests/Namotion.Interceptor.Dynamic.Tests.csproj --no-restore --filter "FullyQualifiedName~DynamicSubject"
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore --filter "FullyQualifiedName~DynamicPropertyTypedChain|FullyQualifiedName~VerifyChecksTests.PublicApi"
dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj --no-restore --filter "FullyQualifiedName~InterceptorSubject|FullyQualifiedName~UnifiedSetterEmission"
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --no-restore --filter "FullyQualifiedName~VerifyChecksTests.PublicApi"
git diff --check
```

- [ ] Record the delta and commit.

```bash
git add src/Namotion.Interceptor src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Generator src/Namotion.Interceptor.Generator.Tests src/Namotion.Interceptor.Dynamic src/Namotion.Interceptor.Dynamic.Tests src/Namotion.Interceptor.Registry src/Namotion.Interceptor.Registry.Tests src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests src/Shared
git commit -m "fix: coordinate lifecycle at the structural terminal"
```

## Task 3: Complete temporary-root reachability on the prepared ownership graph

**Budget:** At most `+180` cumulative production lines from current HEAD after Task 2. Migration adapters remain only for attach/detach/admission and are deleted with their last callers in Task 6. No production files added or deleted in this task.

**Files:**

- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/OwnershipGraph.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectOwnership.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleNotifier.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/StructuralWriteLease.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/OwnershipReservation.cs`
- Modify temporarily as adapters: `src/Namotion.Interceptor.Tracking/Lifecycle/StructuralReconciler.cs`
- Modify temporarily as adapters: `src/Namotion.Interceptor.Tracking/Lifecycle/AttachTraversal.cs`
- Modify temporarily as adapters: `src/Namotion.Interceptor.Tracking/Lifecycle/ReleaseTraversal.cs`
- Modify temporarily as adapters: `src/Namotion.Interceptor.Tracking/Lifecycle/ReachabilityWalk.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/GraphOwnershipTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/ReparentCascadeTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipReservationProtocolTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/DownstreamWriteInterceptorReleaseTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/StructuralWriteLockOrderTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/GraphBehavior/CycleTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/GraphBehavior/OccurrenceProjectionTests.cs`

**Interfaces:**

- Consumes Task 2 `PreparedTopologyChange`, `PrepareWrite`, `Publish`, `PrepareDeferredSweep`, and topology admission coordinator.
- Produces the complete temporary-root and deferred-sweep behavior used by structural terminals and later attach/admission cutovers.
- Consumes immutable `StructuralSnapshot` values, attachment records, and exact protector tokens only. It never accepts raw values or delegates.

- [ ] Add or retain table-driven oracle tests for trees, shared DAGs, multiple roots, duplicate occurrences, reorder, rekey, replacement, retained reparenting, anchored cycles, and closed unanchored cycles.
- [ ] Add a deterministic last-path removal while a descendant lease is active. Assert the removal commits, the protected closure remains continuously attached and foreign-unclaimable, and final lease disposal performs one reachability sweep.
- [ ] Add overlapping protector and new-support-before-final-disposal cases. Assert one deferred-sweep flag suffices with no closure groups or merging.
- [ ] Add admission-versus-publication races proving a protector either enters before reachability and is included, or enters after publication against the new attachment epoch.
- [ ] Add a deferred-sweep callback-failure case. Explicit structural-operation completion aggregates it with the primary chain exception; fallback token disposal does not throw and reports the failure through `Trace`.
- [ ] Move occurrence diffing, add-before-remove publication, affected-component reachability, anchors, temporary-root handling, and deterministic journal construction into `OwnershipGraph`.
- [ ] Replace `SubjectOwnership` piecemeal mutation methods with one immutable publication containing incoming parents, reference count, and outgoing snapshots.
- [ ] Route lease/reservation admission and disposal through topology-gate then one executor monitor. Disposal may consume the one deferred flag but must not hold a monitor while entering the gate.
- [ ] Keep legacy attach/detach/admission helpers as thin callers of the prepared graph state so the complete suite stays green. They may not own a second baseline, reachability algorithm, mutable edge state, or callback path. Task 6 deletes them with their final callers.

```bash
rg -n "ReleasePending|TopologyFreeze|protector group|PendingTerminal|PendingStructuralWrite" src/Namotion.Interceptor src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Dynamic src/Namotion.Interceptor.Registry src/Namotion.Interceptor.Generator src/Shared
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore --filter "FullyQualifiedName~GraphOwnership|FullyQualifiedName~Cycle|FullyQualifiedName~Reparent|FullyQualifiedName~Occurrence|FullyQualifiedName~OwnershipReservationProtocol|FullyQualifiedName~DownstreamWriteInterceptorRelease|FullyQualifiedName~StructuralWriteLockOrder"
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore --filter "FullyQualifiedName~Cycle|FullyQualifiedName~Occurrence"
git diff --check
```

- [ ] Record the delta and commit.

```bash
git add src/Namotion.Interceptor src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests src/Namotion.Interceptor.Registry.Tests
git commit -m "fix: retain active structural ownership through writes"
```

## Task 4: Publish immutable callback journals outside locks

**Budget:** At most `+260` cumulative production lines from current HEAD after Task 3. Delete one production file and add none; callback correctness may temporarily precede the large Task 6 helper deletion.

**Files:**

- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleNotifier.cs`
- Delete: `src/Namotion.Interceptor.Tracking/Lifecycle/CallbackReentrancyGuard.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectLifecycleChange.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectPropertyLifecycleChange.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/IPropertyLifecycleHandler.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptorExtensions.cs`
- Modify: `src/Namotion.Interceptor.Registry/SubjectRegistry.cs`
- Modify: `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubject.cs`
- Modify: `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubjectProperty.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/CallbackContractTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleHandlerOrderTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleEventsTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipChangeStreamTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/RegistryHandlerOrderTests.cs`
- Modify: affected Tracking and Registry public API snapshots

**Interfaces:**

- Produces one journal representation nested in `LifecycleNotifier.cs`.
- Uses Task 2's Core-owned logical context scope. `LifecycleNotifier` enters/exits callback depth on that scope and exposes the compact policy checks needed by lifecycle entry points.
- Publishes complete subject/property projections with their own monotonic revision.

- [ ] Add deterministic overlapping-journal tests. Assert no deadlock, each operation receives only its own callback exceptions, and older same-entity Registry delivery cannot overwrite newer state.
- [ ] Add callout probes for lifecycle handlers, subject events, property handlers, and Registry callbacks. Each probe starts a worker that acquires the relevant framework lock and must complete.
- [ ] Preserve topology-changing callback rejection, same-thread second-context rejection, same-context `AddProperties` admission, attach top-down/bottom-up order, detach top-down order, and actual release before addition.
- [ ] Build immutable journals before graph publication. Publish graph and attachment state first, release locks, drain every entry despite failures, then throw one exception or `AggregateException` for the originating operation.
- [ ] Convert the still-temporary attach, release, reconcile, and admission adapters to collect journal entries under the topology gate and return them to the outer `LifecycleInterceptor` entry point. That outer entry releases the gate before draining. No adapter may invoke `LifecycleNotifier`, events, property callbacks, or Registry handlers while the gate is held.
- [ ] Keep exact context in a detaching attachment record through that detach journal. Older overlapping journals use payload context and do not prolong the live record.
- [ ] Make Registry replace a complete subject/property projection only when its entity revision is newer. Stop enumerating live structural values under Registry locks.
- [ ] Replace the standalone callback guard with thin `LifecycleNotifier` calls into the Core-owned logical scope, then delete the guard without broadening reentry.
- [ ] Run and inspect Tracking and Registry public API snapshots for the minimal context/revision/projection payload changes.

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore --filter "FullyQualifiedName~Callback|FullyQualifiedName~LifecycleHandlerOrder|FullyQualifiedName~LifecycleEvents|FullyQualifiedName~OwnershipChangeStream"
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore --filter "FullyQualifiedName~RegistryHandlerOrder|FullyQualifiedName~OccurrenceProjection|FullyQualifiedName~Cycle"
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore --filter "FullyQualifiedName~VerifyChecksTests.PublicApi"
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore --filter "FullyQualifiedName~VerifyChecksTests.PublicApi"
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --no-restore --filter "FullyQualifiedName~SourceMonitorHandlerOrder|FullyQualifiedName~SubjectUpdateEmissionOrder"
git diff --check
```

- [ ] Record the delta and commit.

```bash
git add src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests src/Namotion.Interceptor.Registry src/Namotion.Interceptor.Registry.Tests src/Namotion.Interceptor.Connectors.Tests
git commit -m "fix: publish lifecycle journals outside locks"
```

## Task 5: Rebuild explicit attach and detach on the ownership graph

**Budget:** At most `+100` cumulative production lines from current HEAD after Task 4. No production files added; this cutover removes Lifecycle's attach/detach wrappers while admission retains the final temporary helper callers until Task 6.

**Files:**

- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/OwnershipGraph.cs`
- Modify: `src/Namotion.Interceptor/InterceptorSubjectExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/AttachmentStateCoherenceTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/AttachResidueTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/RecursiveAttachTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/DetachAnchorVisibilityTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/DetachParentVisibilityTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/GraphOwnershipTests.cs`

**Interfaces:**

- Consumes the existing exclusive reservation/transition token and Task 3 ownership transaction.
- Produces no second attach or detach engine.

- [ ] Add deterministic attach races against a structural setter, another attach, detach, and foreign reservation. Cover attachment, metadata, and structural-terminal capture revisions.
- [ ] Assert a failed attach publishes no snapshot, ownership, anchor, callback, or attachment residue without a rollback traversal.
- [ ] Add detach cases for another anchor, protected descendant, newly added support, self/back edge, provisional promotion, and an unanchored cycle. Protector-rooted closures release after the final protector.
- [ ] Implement attach as exclusive reservation, unlocked capture, gate-time revision validation, one graph publication, then one unlocked journal.
- [ ] Implement detach as anchor removal through the same reachability engine, detaching-record publication, unlocked journal, and final context clear for actually released subjects.
- [ ] Remove `RollbackRejectedAttach`, recursive seeding, claimed-but-unowned paths, `_releasing`, seeded snapshot flags, immediate claim/release adapters, and recursive reservation-token transport.

```bash
rg -n "RollbackRejectedAttach|SeedAndAttachComponent|ClaimComponentForRoot|AreSnapshotsSeeded|IsReleasing|MarkReleasing|TryClaimDiscovered|ReleaseUnusedClaims" src/Namotion.Interceptor src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Dynamic src/Namotion.Interceptor.Registry src/Namotion.Interceptor.Generator src/Shared
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore --filter "FullyQualifiedName~Attach|FullyQualifiedName~Detach|FullyQualifiedName~Anchor|FullyQualifiedName~AttachmentState|FullyQualifiedName~AttachResidue|FullyQualifiedName~RecursiveAttach|FullyQualifiedName~GraphOwnership"
git diff --check
```

- [ ] Record the delta and commit.

```bash
git add src/Namotion.Interceptor src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests
git commit -m "refactor: attach and detach through one ownership graph"
```

## Task 6: Rebuild property admission and simplify derived validation

**Budget:** The four obsolete topology helper files are deleted with their final admission callers. The planned cumulative line reduction was not achieved because the completed admission, projection, deferred-sweep, and derived-cascade protocols required additional state and deterministic race handling. See Local implementation checkpoint.

**Files:**

- Modify: `src/Namotion.Interceptor/Interceptors/SubjectPropertyRegistration.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/PropertyAdmission.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptorExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Change/DerivedPropertyData.cs`
- Delete: `src/Namotion.Interceptor.Tracking/Lifecycle/StructuralReconciler.cs`
- Delete: `src/Namotion.Interceptor.Tracking/Lifecycle/AttachTraversal.cs`
- Delete: `src/Namotion.Interceptor.Tracking/Lifecycle/ReleaseTraversal.cs`
- Delete: `src/Namotion.Interceptor.Tracking/Lifecycle/ReachabilityWalk.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/AddPropertiesLifecycleTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/DetachCallbackAdmissionTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/NormalizingSetterDerivedRaceTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/ConcurrentPublicationVerdictTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Change/DerivedPropertyConcurrencyTests.cs`

**Interfaces:**

- `PropertyAdmission` consumes materialized registrations, immutable snapshots, exclusive admission reservation, and the common graph publication.
- Derived alias validation repeats an exact-reservation test and topology-gate barrier only while an uninstrumented alias observes a same-context reserved orphan. Separately, a target-local synthetic derived notification can defer before its chain when its target lifecycle is temporarily unavailable after the source terminal committed.

- [ ] Add admission races for input materialization, duplicate names, foreign subjects, metadata generation, attach/detach, same-context callback admission, and a structural getter waiting for worker activity. Assert input once, accepted getter once per attempt, and publisher exactly once only after acceptance.
- [ ] Add a derived-alias race that reads the raw field between faithful store and graph publication plus back-to-back writers exposing two consecutive transient values. Cross the gate and reevaluate while each orphan has an exact same-context reservation; throw only when the current orphan has no explaining reservation.
- [ ] Implement admission as materialize/capture outside locks, acquire an exclusive admission token, validate under the gate, release the gate, invoke the registration's existing contract-bound exception-free publisher once, reacquire the gate, revalidate metadata/attachment/reservation revisions, publish graph state, and drain callbacks in caller order. The exclusive token spans both gate sections. A third-party publisher that violates its documented exact-assignment/no-throw contract is not repaired. Preserve detached and detaching metadata behavior.
- [ ] Remove duplicate capture/claim loops, `AdmitUnowned`, seeded/releasing metadata branches, and built-in Registry raw collection refresh.
- [ ] Replace context-wide transaction counts, withheld lists, pending-descriptor inference, completion registration, and sticky derived faults with the reservation/barrier loop. Never replay arbitrary interceptor side effects; derived getter reevaluation occurs only after a barrier explained by the exact newly observed reservation.
- [ ] Delete the four helper files after PropertyAdmission's final calls move to `OwnershipGraph`. Remove their immediate claims, seeded/releasing branches, rejected-attach rollback, recursive traversal, and duplicate callback code with their last references.

```bash
rg -n "StructuralReconciler|AttachTraversal|ReleaseTraversal|ReachabilityWalk|_transactionsInFlight|_withheld|TryRunWhenTransactionEnds|HasWithheldRecalculation|PendingTerminal|RegisterOrRun|sticky" src/Namotion.Interceptor src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Dynamic src/Namotion.Interceptor.Registry src/Namotion.Interceptor.Generator src/Shared
rg -n "RefreshCollectionProperty" src/Namotion.Interceptor.Registry src/Namotion.Interceptor.Tracking/Lifecycle
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore --filter "FullyQualifiedName~AddPropertiesLifecycle|FullyQualifiedName~DetachCallbackAdmission|FullyQualifiedName~NormalizingSetterDerivedRace|FullyQualifiedName~ConcurrentPublicationVerdict|FullyQualifiedName~Derived"
dotnet test src/Namotion.Interceptor.Dynamic.Tests/Namotion.Interceptor.Dynamic.Tests.csproj --no-restore --filter "FullyQualifiedName~DynamicSubject"
git diff --check
```

- [ ] Record the delta and commit.

```bash
git add src/Namotion.Interceptor src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests src/Namotion.Interceptor.Registry
git commit -m "refactor: simplify lifecycle admission and derived validation"
```

## Task 7: Remove migration residue and verify the complete branch

**Budget:** At least 800 five-project and 760 Core-plus-Tracking production lines removed from `23d4a54b`. Final gates from Starting Point are mandatory.

**Files:**

- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleScratch.cs`
- Modify only as needed: final owner files listed above
- Modify: affected public API snapshots
- Modify: `docs/design/tracking-lifecycle.md`
- Modify: `docs/tracking.md`

- [x] Remove scratch pools used only by deleted traversals, test-only graph methods, repeated routing, immediate-claim adapters, duplicate notifier overloads, and migration types without a final responsibility.
- [x] Prove there is one snapshot builder, graph engine, terminal protocol, admission protocol, and journal path.

```bash
rg -n "EnterStructuralWriteGate|ExitStructuralWriteGate|StructuralReconciler|AttachTraversal|ReleaseTraversal|ReachabilityWalk|CallbackReentrancyGuard|RollbackRejectedAttach|AreSnapshotsSeeded|_transactionsInFlight|_withheld|TryRunWhenTransactionEnds|PendingTerminal|PendingStructuralWrite|RegisterOrRun|ReleasePending|TopologyFreeze|RefreshCollectionProperty" src/Namotion.Interceptor src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Dynamic src/Namotion.Interceptor.Registry src/Namotion.Interceptor.Generator src/Shared
```

Expected: no match except the intentional `RefreshCollectionProperty` path. Registry consumes its immutable revisioned projection because retained collection reorder and rekey updates need publication without live enumeration.

- [x] Recalculate production deltas against PR head and master. List the remaining master delta by product-semantic responsibility. If a budget is missed, simplify the responsible owner before broad verification.
- [x] Run focused projects, then build, all non-integration tests, pack, public API checks, and repeated deterministic concurrency filters.

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Dynamic.Tests/Namotion.Interceptor.Dynamic.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --no-restore --filter "FullyQualifiedName~DefaultSubjectFactory|FullyQualifiedName~SubjectUpdateInsertedItem|FullyQualifiedName~SubjectUpdateEmissionOrder|FullyQualifiedName~PartialApplyGraphState|FullyQualifiedName~SourceMonitorHandlerOrder"
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
dotnet pack src/Namotion.Interceptor.slnx
git diff --check origin/master...HEAD
```

- [x] Audit every framework lock for forbidden callouts and request independent correctness and deletion reviews.
- [ ] Update public lifecycle docs with final-value freeze, faithful terminal contract, lock order, temporary roots, callbacks, Dynamic/manual requirements, and direct mutable-collection limitation after the maintainer selects this comparison candidate for integration.
- [ ] Commit only after all required checks pass.

```bash
git add src docs/design/tracking-lifecycle.md docs/tracking.md
git commit -m "refactor: finalize the lifecycle write protocol"
```

- [ ] Stop on the verified local branch. Report commits, tests, remaining production delta, and contract boundaries. Ask before benchmarks, push, PR changes, or review replies.
