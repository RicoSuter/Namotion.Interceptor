# Lifecycle and Structural Write Protocol Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace PR #494's whole-chain lifecycle gate and raw-value reconciliation with a terminal-revision protocol that is deadlock-free, continuously owns retained subjects, and is quiescently consistent.

**Architecture:** Structural writes acquire a nonblocking executor lease, prepare reservations at the raw terminal boundary, publish one pending terminal revision with the backing store, and commit immutable occurrence snapshots under a short topology gate. Attach, detach, and `AddProperties` use the same capture, reserve, pure commit, and notify phases. Arbitrary user work runs outside framework locks; the raw terminal is the sole trusted nonblocking and non-reentrant store allowed under the per-subject terminal lock.

**Tech Stack:** C# 13 preview, .NET Standard 2.0 Core, .NET 9 Tracking and consumers, xUnit, Verify, System.Collections.Immutable, BenchmarkDotNet.

**Spec:** [`docs/superpowers/specs/2026-08-30-lifecycle-write-protocol-redesign-design.md`](../specs/2026-08-30-lifecycle-write-protocol-redesign-design.md)

## Global Constraints

- Work from PR head `331b701931cb9d92832523bb014c222bca072cc6` or a descendant that contains this plan and its spec.
- Do not replay an interceptor chain to resolve attachment or capture staleness.
- Do not invoke an interceptor, arbitrary getter, enumerable, equality implementation, lifecycle handler, event handler, property lifecycle handler, metadata input iterator, public metadata publisher, or derived recalculation while holding the attachment monitor, terminal `SyncRoot`, or context topology gate. A generator-emitted raw field reader and the raw terminal are the only exceptions and must satisfy the trusted-access contract.
- A lock may protect pure library state only. Add a test whenever a new callout is moved across a lock boundary.
- The only nested framework-lock order is topology gate then one executor attachment monitor during nonthrowing publication. No path may request the topology gate while retaining an executor monitor.
- Preserve scalar generated fast paths. Structural generated setters must create or reuse their executor even while detached.
- Use reference equality for subjects. Never use an occurrence index or dictionary key as edge identity.
- Never store a raw structural property value in `OwnershipGraph`.
- Never silently suppress a committed terminal revision. It must commit topology, be superseded by a later terminal revision, or leave a sticky lifecycle fault.
- Preserve uncontended hand-written normalizers that introduce an unattached substitute. Register the pending descriptor before the raw store, reserve introduced actual subjects after authoritative capture, and fault rather than publish if a competing foreign reservation wins.
- Treat active structural leases and same-context ownership reservations as release protectors. A topology publication must freeze lease and reservation admission on every affected executor before final validation.
- Keep lifecycle callbacks synchronous for their originating operation and outside framework locks. Add entity revisions and immutable per-property projections to concurrent callback payloads; do not use one context-global watermark to discard unrelated work.
- Install notification holds atomically with the topology state and journal they protect. Never acquire a hold only after releasing the topology gate.
- No hardcoded sleeps or delays in tests. Use `ManualResetEventSlim`, `Barrier`, `CountdownEvent`, or `AsyncTestHelpers.WaitUntilAsync` and bounded joins.
- Use the repository test naming and Arrange, Act, Assert conventions.
- Do not update Verify snapshots until the semantic diff has been inspected.
- Do not run the long benchmark comparison until the user agrees to it. Read `docs/benchmarking.md` immediately before running it.
- Do not mix generator constructor mirroring, connector feature fixes, or unrelated public API cleanup into these commits.
- Do not commit a task until its focused tests are green and `git diff --check` passes.
- Treat production-code simplification as an acceptance gate. Production means C# under `src` excluding test, benchmark, sample, and snapshot paths. PR head `c5079c6f` is already about 3,100 net production lines above master under that classification. Core plus Tracking account for +2,585 lines, while Core, Tracking, Generator, Registry, and Dynamic together account for +3,094. Completion requires Core plus Tracking to finish at +2,300 or less and the five-project lifecycle scope at +2,800 or less versus master, unless the user explicitly accepts a measured per-invariant exception after reviewing the local branch. The completed branch must report its remaining delta by product-semantic responsibility rather than calling protocol overhead a feature.
- Record the production-line delta against both `c5079c6f` and master after every task. Intermediate migration duplication is allowed only when a later task names its deletion. Task 10 must leave one snapshot builder, one reachability and topology engine, one terminal protocol, and one notification path, with no compatibility path that duplicates a new mechanism.
- The seven superseded helper files named by the design currently contain 1,063 physical lines at `c5079c6f`: `StructuralReconciler`, `AttachTraversal`, `ReleaseTraversal`, `ReachabilityWalk`, `CallbackReentrancyGuard`, `StructuralValueScanner`, and `LifecycleScratch`. Replacement components must earn their size by deleting those responsibilities, not wrap them.
- The lifecycle scope is currently +12 net production files over master. It may not exceed that file delta. A new protocol file must replace an obsolete owner or be folded into an existing focused owner before Task 10 completes.

---

## Task 1: Establish the acceptance inventory and assign each assertion to its owning task

This task is a planning gate, not a red test commit. To preserve TDD and keep every implementation commit green, do not create or modify test files here. Each acceptance assertion is written in its owning task, observed failing for the expected reason, and committed only with the implementation that makes it pass. This avoids carrying uncommitted failing tests through Task 2's complete Tracking and Registry runs.

**Files assigned to later owning tasks:**

- Add: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/TerminalBoundaryCoordinatorTests.cs`
- Add: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/StructuralAttachRaceTests.cs`
- Add: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleLockCalloutTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/ReentrantStructuralWriteTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/CrossContextGateDeadlockTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/TopologyTransactionTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/WriteProtocolAcceptance.cs`

- [ ] Assign the deterministic downstream worker-wait test to Task 5. The interceptor must start a worker that writes a different subject in the same context and synchronously wait before forwarding the outer write. Assert both assignments commit and both children are attached.

```csharp
[Fact]
[Trait("Category", "Concurrency")]
public void WhenADownstreamInterceptorWaitsForASameContextStructuralWrite_ThenBothWritesComplete()
{
    // Arrange
    using var workerStarted = new ManualResetEventSlim();
    using var workerFinished = new ManualResetEventSlim();
    // Register a one-shot interceptor that starts the worker and waits with JoinTimeout.

    // Act
    var exception = Record.Exception(() => outer.Mother = outerChild);

    // Assert
    Assert.Null(exception);
    Assert.True(workerStarted.IsSet);
    Assert.True(workerFinished.IsSet);
    Assert.Same(context, outerChild.TryGetContext());
    Assert.Same(context, workerChild.TryGetContext());
}
```

- [ ] Assign `WhenAStructuralWriteRacesAttachCapture_ThenAttachCannotCommitAStaleSnapshot` to Task 8. It replaces `WhenAUserEnumerableWritesTheRootWhileTheAttachSeedsIt_ThenTheWritePassesThroughAndTheAttachCompletes` and asserts that either the write commits before attach capture and the latest child is owned, or the write receives the new retryable transition conflict before its terminal. Never accept a field child that is unattached after both operations settle.
- [ ] Assign ordinary downstream cross-context behavior and terminal callout probes to Task 5, topology publication assertions to Task 6, callback-originated cross-context behavior plus lifecycle handler, subject event, and property lifecycle handler probes to Task 7, stale attach assertions to Task 8, and metadata-input probes to Task 9. Assign immutable enumeration and dictionary-key equality assertions to Task 2. A nested operation may fail only on the same explicit lease, transition, reservation, or terminal contract conflicts as a top-level operation.
- [ ] Require every owning task to make each worker background-owned and bounded. Include phase guards so a test cannot pass without entering the intended production window.
- [ ] Record the clean current-PR baseline before implementation. The owning task must then record the expected RED result immediately before writing production code and the GREEN result after implementation.

Run:

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore --filter "FullyQualifiedName~TerminalBoundaryCoordinatorTests|FullyQualifiedName~StructuralAttachRaceTests|FullyQualifiedName~LifecycleLockCalloutTests|FullyQualifiedName~CrossContextGateDeadlockTests|FullyQualifiedName~TopologyTransactionTests"
```

Expected at this gate: the existing current-PR suite passes. Each later owning task proves its new assertion fails without hanging before it changes production code.

- [ ] Do not commit a red assertion alone. Write, observe, implement, and commit it entirely inside its owning task. Task 2 owns immutable-snapshot callouts, Task 5 owns terminal-boundary and downstream worker-wait assertions, Task 6 owns topology publication assertions, Task 7 owns callback callout assertions, Task 8 owns stale-attach assertions, and Task 9 owns metadata-admission callouts.

## Task 2: Replace raw baselines with immutable occurrence snapshots

**Files:**

- Add: `src/Namotion.Interceptor.Tracking/Lifecycle/StructuralSnapshot.cs`
- Rename or replace: `src/Namotion.Interceptor.Tracking/Lifecycle/StructuralValueScanner.cs` to `src/Namotion.Interceptor.Tracking/Lifecycle/StructuralSnapshotBuilder.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/OwnershipGraph.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectOwnership.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/StructuralReconciler.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/AttachTraversal.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/ReleaseTraversal.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/PropertyAdmission.cs`
- Add: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/StructuralSnapshotTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/ReentrantStructuralWriteTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/TerminalStoreContractTests.cs`

- [ ] Add a test proving that a mutable list changed directly after assignment does not change the committed outgoing topology used by release.

```csharp
[Fact]
public void WhenACommittedMutableValueChangesWithoutASetter_ThenReleaseUsesTheCommittedSnapshot()
{
    // Arrange
    var list = new List<Person> { committedChild };
    holder.Children = list;
    list.Clear();
    list.Add(uncommittedChild);

    // Act
    holder.DetachFromContext(context);

    // Assert
    Assert.Null(committedChild.TryGetContext());
    Assert.Null(uncommittedChild.TryGetContext());
}
```

- [ ] Add direct, ordinal collection, generic dictionary, non-generic dictionary, and read-only dictionary snapshot tests. Assert duplicate ordinals are per child identity, not global positions.
- [ ] Add a key type whose `Equals` throws. Assert reconcile, parent lookup, rekey, and release complete without calling it. Allow equality only in test assertions outside lifecycle locks.
- [ ] Implement immutable snapshot types. Keep the shape minimal:

```csharp
internal readonly record struct StructuralOccurrence(
    IInterceptorSubject Subject,
    int SubjectOrdinal,
    object? Index);

internal sealed record StructuralSnapshot(
    long SourceRevision,
    ImmutableArray<StructuralOccurrence> Occurrences)
{
    internal static readonly StructuralSnapshot Empty = new(0, []);
}
```

- [ ] Make `StructuralSnapshotBuilder` the only user-value interpreter. It returns a complete immutable array and mutates no graph state.
- [ ] Change `_baselines` to `_snapshots` and remove `GetBaseline`, `SetBaseline`, `CommitsEdgeTo`, and every scan of a committed raw value.
- [ ] Change `IncomingEdge` identity to property plus child-specific ordinal. Store index as payload. Ensure `SubjectOwnership` uses reference identity and integer ordinal only.
- [ ] Adapt the current attach, reconcile, and release paths temporarily to consume snapshots. Do not yet add the new topology transaction in this task.
- [ ] Convert raw-baseline white-box assertions to snapshot assertions. Replace the old reentrant-enumeration test with an assertion that committed values are not re-enumerated.
- [ ] Run Tracking and Registry completely because the throwaway spike demonstrated this conversion can stay behaviorally isolated.

Run:

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore
git diff --check
```

- [ ] Inspect every changed lifecycle event and Verify snapshot before accepting it. At this stage preserve old event order except where an assertion depended on re-enumerating raw values.
- [ ] Commit.

```bash
git add src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests src/Namotion.Interceptor.Registry src/Namotion.Interceptor.Registry.Tests
git commit -m "refactor: store immutable structural edge snapshots"
```

## Task 3: Add executor structural leases and generated detached-write coordination

**Files:**

- Add: `src/Namotion.Interceptor/Interceptors/StructuralWriteLease.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/IInterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`
- Add: `src/Namotion.Interceptor.Tests/Interceptors/StructuralWriteLeaseTests.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/InterceptorSubjectTests.cs`
- Modify: `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt` only if the generated or public seam necessarily changes

- [ ] Add deterministic executor tests for shared structural leases, exclusive transition conflict, lease release after veto and exception, reentrant same-subject writes outside the raw terminal, and attachment state remaining coherent for the lease lifetime.
- [ ] Add a generated-code test proving a detached structural setter initializes the executor, reaches its terminal lock, and consumes a terminal revision. Add a scalar counterpart proving `_executor` remains null on the direct fast path.
- [ ] Introduce one immutable attachment-state publication that carries context, anchor, attachment revision, phase, and structural lease count. Give each returned lease token an executor-local identity and idempotent disposal, and track active identities in private executor state under the attachment monitor so Task 6 can register exact pending-release protectors. Keep ownership reservations separate until Task 4.
- [ ] Implement nonblocking acquisition. Do not use `Monitor.Wait`, `SemaphoreSlim.Wait`, spin-until-free, or retry around the interceptor chain.

```csharp
internal StructuralWriteLease TryAcquireStructuralWriteLease()
{
    lock (_attachmentLock)
    {
        if (_attachment.Phase is AttachmentPhase.Attaching or AttachmentPhase.Detaching)
        {
            throw LifecycleConflictException.Retryable(_subject);
        }

        var next = _attachment.WithStructuralLeaseCount(_attachment.StructuralLeaseCount + 1);
        _attachment = next;
        return new StructuralWriteLease(this, next.Context, next.AttachmentRevision);
    }
}
```

- [ ] Make attach, detach, and anchor promotion acquire an exclusive transition and fail promptly when a structural lease is active. Release the exclusive state on every exception path.
- [ ] Change generated helpers so structural property calls always route through `InterceptorExecutor.GetOrCreate`; keep scalar direct reads and writes unchanged. Introduce the generated structural setter entry with trusted raw reader and writer delegates here so no generated backing-field read occurs before `SyncRoot`. Task 5 adds its terminal-coordinator behavior. Use generated code or a JIT-folded per-type trait, not a runtime reflection check per assignment.
- [ ] Lock every generated structural raw read and terminal store with the executor `SyncRoot`, including the first detached access. Both structural accessors must create the executor unconditionally. Capture `PropertyWriteContext.CurrentValue` through the trusted reader under `SyncRoot`, not by evaluating the backing field as a method argument. Do not change scalar detached read behavior.
- [ ] Run Core, Generator, and attachment tests.

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --no-restore --filter "FullyQualifiedName~StructuralWriteLease|FullyQualifiedName~AttachmentState|FullyQualifiedName~HandWrittenSubjectWrite|FullyQualifiedName~VerifyChecksTests.PublicApi"
dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj --no-restore --filter "FullyQualifiedName~InterceptorSubject|FullyQualifiedName~Constructor"
git diff --check
```

- [ ] Commit.

```bash
git add src/Namotion.Interceptor src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Generator src/Namotion.Interceptor.Generator.Tests
git commit -m "fix: coordinate detached structural writes with attachment"
```

## Task 4: Add reference-counted context reservations

**Files:**

- Add: `src/Namotion.Interceptor/Interceptors/OwnershipReservation.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Add: `src/Namotion.Interceptor.Tests/Interceptors/OwnershipReservationTests.cs`
- Add: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipReservationProtocolTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/OwnershipGraph.cs`

- [ ] Add the seven deterministic cases proven by the reservation spike: same-context shared child, one participant releasing while another remains, foreign competition, exclusive attach conflict, exclusive transition reentry by its own token, provisional-to-explicit promotion, and reservation context remaining invisible through `TryGetContext()`.
- [ ] Add a three-thread case where two same-context parent writes reserve one detached child while a foreign context tries to attach it. Assert both same-context tokens can coexist, the foreign attach fails before its terminal, and releasing either one cannot open a claim gap.
- [ ] Add a model test where write A reserves an already-owned child and write B removes its last committed edge before A commits. Assert B cannot detach or expose the child to a foreign claim; Task 6 will route this retained reservation through a pending-release group.
- [ ] Implement one executor-local reservation group keyed by exact context identity. Use participant tokens rather than a boolean claim.

```csharp
internal sealed class OwnershipReservation
{
    internal required InterceptorSubjectContext Context { get; init; }
    internal required long Generation { get; init; }
    internal required ReservationMode Mode { get; init; }
    internal int ParticipantCount;
}
```

- [ ] Join a shareable reservation only for the same exact context. Reject a foreign context immediately. An exclusive attach reservation cannot be joined by another operation.
- [ ] Make token disposal idempotent. The last token may clear only the reservation generation it joined. If the subject committed to the same context, disposal must not detach it.
- [ ] Give reservation tokens stable identities that can protect and finalize Task 6 pending-release groups exactly like structural lease identities.
- [ ] Add `OwnershipGraph.ReleaseUnusedReservation` using committed immutable snapshots and a short topology gate. It must call no getter, enumerator, equality implementation, or callback under the gate.
- [ ] Keep raw public `TryUpdateAttachment` from bypassing an active reservation or transition. If compatibility requires the method to remain, route it through the same checks.
- [ ] Run Core and focused Tracking concurrency tests repeatedly.

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --no-restore --filter "FullyQualifiedName~OwnershipReservation"
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore --filter "FullyQualifiedName~OwnershipReservationProtocol|FullyQualifiedName~ForeignClaim|FullyQualifiedName~ReconcileRetentionWindow" --repeat 20
git diff --check
```

If the installed test runner does not support `--repeat`, run the filtered command from a bounded shell loop outside the committed test code.

- [ ] Commit.

```bash
git add src/Namotion.Interceptor src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests
git commit -m "fix: reserve structural ownership without claim gaps"
```

## Task 5: Move lifecycle preparation to the raw terminal boundary

**Files:**

- Add: `src/Namotion.Interceptor/Interceptors/IWriteTerminalCoordinator.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/IWriteInterceptor.cs`
- Modify: `src/Namotion.Interceptor/Cache/WriteInterceptorFactory.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/IInterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/ILifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/GeneratedExecutorTests.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/UnifiedSetterEmissionTests.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/Snapshots/*.verified.txt` files containing generated `SetPropertyValue` calls
- Modify: `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Add: `src/Namotion.Interceptor.Tracking/Lifecycle/PendingTerminalRegistry.cs`
- Add: `src/Namotion.Interceptor.Tracking/Lifecycle/PendingStructuralWrite.cs`
- Add: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/TerminalRevisionTests.cs`
- Add or modify: Task 1 terminal-boundary tests assigned to this task
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/TerminalStoreContractTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/DownstreamWriteInterceptorReleaseTests.cs`

- [ ] Add tests that a downstream interceptor can rewrite `context.NewValue`, veto, start and wait for another structural write, and throw after `next`. Assert preparation sees the final pre-terminal value, veto allocates no reservation, worker writes make progress, and a post-`next` exception cannot leave committed field topology unreconciled.
- [ ] Add A/B and reentrant A/B/C terminal-revision tests. Park A after its terminal, let B commit and reconcile, release A, and assert A cannot overwrite B's snapshot or notifications.
- [ ] Add two overlapping generated writes that capture the same entry value and then terminalize in a controlled order. Assert each after-`next` interceptor and `SubjectPropertyChange` receives the exact value its terminal replaced, while a before-`next` observation is documented as the coherent entry snapshot and the final revision wins topology.
- [ ] Add normalizing terminal tests for exact proposal, reordered subset, dropped subjects, introduced unattached subject, introduced same-context subject, and introduced foreign subject. The uncontended unattached and same-context substitutions must commit. A foreign subject or a substitute lost to a racing foreign reservation leaves the backing value visible but publishes sticky fault state and no inconsistent graph.
- [ ] Define the coordinator internally in Core so ordinary consumers cannot install it. Tracking implements it through existing friend access.

```csharp
internal interface IWriteTerminalCoordinator
{
    void BeforeTerminal<TProperty>(ref PropertyWriteContext<TProperty> context);
    void AfterTerminal<TProperty>(ref PropertyWriteContext<TProperty> context);
    void TerminalFailed<TProperty>(ref PropertyWriteContext<TProperty> context, Exception exception);
}
```

- [ ] Let `LifecycleInterceptor.WriteProperty` validate structural classification, install the coordinator, and call `next` once. Remove callback-depth rejection, any gate entry from `InterceptorExecutor.SetStructuralPropertyValue`, and the lifecycle wrapper around `next`.
- [ ] In `WriteInterceptorFactory`, call `BeforeTerminal` before `SyncRoot`; under `SyncRoot`, assign the revision and current property slot, run the trusted generated raw read when available, invoke the raw store, and update origin, timestamp, write state, and descriptor state; call `AfterTerminal` after releasing it. Catch a raw-terminal exception, release `SyncRoot`, call `TerminalFailed` to finish protocol cleanup, then rethrow the original exception with its stack.
- [ ] Allocate `PendingStructuralWrite` after proposal capture in `Preparing` state and link each proposal reservation participant to it, but do not replace the property's current slot yet. Publish only an untrusted manual terminal in the context's immutable `PendingTerminalRegistry`, before its raw terminal, because it alone can expose a substitute that has no exact reservation yet. Under `SyncRoot`, assign the terminal revision, publish this descriptor as the property's current slot, advance it to `Storing`, re-linearize generated `CurrentValue`, invoke the raw store, and advance to `Pending`. Add the A-prepares, B-prepares-and-stores, A-stores-last permutation and assert A owns the larger revision. Add an allocation assertion that the generated faithful path never updates the context registry.
- [ ] Add a generated-code executor entry with the explicit signature `SetGeneratedPropertyValue<TProperty>(string propertyName, TProperty newValue, Func<IInterceptorSubject, TProperty> readValue, Action<IInterceptorSubject, TProperty> writeValue)`. It acquires `SyncRoot` for a short initial raw read before the chain and repeats that trusted read immediately before the terminal store to re-linearize `PropertyWriteContext.CurrentValue`. Preserve the existing public `SetPropertyValue` signature and binary behavior for hand-written and dynamic subjects; it always takes the untrusted authoritative-getter path and retains the caller-supplied current-value contract. Hide the generated entry from ordinary API discovery, never infer faithfulness from metadata, and inspect the Core public API snapshot before accepting the addition.
- [ ] Give `PropertyWriteContext.CurrentValue` an internal mutation path used only by the generated terminal entry. Entry-side interceptors see the coherent initial capture; after-`next` interceptors and `PropertyChangeInterceptor` see the exact value replaced at terminal linearization. No topology decision may depend on the entry-side value.
- [ ] Validate actual occurrences by subject reference. Reuse proposal reservations and acquire same-context reservations after the terminal for actual subjects introduced by a normalizer. Never steal a foreign subject after the backing field changed; publish sticky fault state if actual reservation fails.
- [ ] Implement `Preparing -> Storing -> Pending -> Committed | Superseded | Faulted`, including `Storing -> Superseded | Faulted` after terminal failure, plus an atomic `RegisterOrRun` completion operation. Terminal transition detaches continuations without invoking them under the topology gate. `TerminalFailed` retains the original exception, releases `SyncRoot`, then either supersedes an already replaced descriptor or performs best-effort authoritative capture and publishes sticky fault state before rethrowing. Every path removes the optional registry entry, unlinks reservation participants, releases unneeded reservations, and dispatches detached continuations outside locks. If ordinary finalization fails after successful authoritative capture, retain only same-context reservations required by the actual snapshot and release known dropped proposals. If capture failed, conservatively retain proposals. Surface sticky fault through structural reads and graph-sensitive operations.
- [ ] Add recovery tests. A later successful structural setter must supersede `Faulted`, commit a consistent snapshot, clear the fault, and release old fault reservations. Explicit detach must also clear the fault. A foreign subject revealed by a normalizer remains foreign and keeps the property faulted until recovery.
- [ ] Preserve pending origin and timestamp semantics by executing the interceptor chain and terminal exactly once. Do not reroute after a terminal.
- [ ] Make the Task 1 terminal-boundary and downstream interceptor worker-wait assertions green. Leave topology publication assertions for Task 6, callback and event callout assertions for Task 7, and stale-attach assertions for Task 8; none of those later assertions may be weakened to make this task pass.

Run:

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore --filter "FullyQualifiedName~TerminalBoundaryCoordinatorTests|FullyQualifiedName~TerminalRevisionTests|FullyQualifiedName~TerminalStoreContractTests|FullyQualifiedName~DownstreamWriteInterceptorReleaseTests"
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --no-restore --filter "FullyQualifiedName~StructuralWrite|FullyQualifiedName~PropertyWrite|FullyQualifiedName~Origin|FullyQualifiedName~Timestamp"
dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj --no-restore --filter "FullyQualifiedName~GeneratedExecutor|FullyQualifiedName~UnifiedSetterEmission"
git diff --check
```

- [ ] Commit only terminal-boundary acceptance tests written RED and made GREEN inside this task. No later task's acceptance test may be present in the working tree.

```bash
git add src/Namotion.Interceptor src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Generator src/Namotion.Interceptor.Generator.Tests src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests
git commit -m "fix: coordinate lifecycle at the structural write terminal"
```

## Task 6: Replace recursive reconciliation with one pure topology transaction

**Files:**

- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/StructuralWriteLease.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/OwnershipReservation.cs`
- Modify: `src/Namotion.Interceptor.Tests/Interceptors/StructuralWriteLeaseTests.cs`
- Modify: `src/Namotion.Interceptor.Tests/Interceptors/OwnershipReservationTests.cs`
- Add: `src/Namotion.Interceptor.Tracking/Lifecycle/TopologyTransaction.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/OwnershipGraph.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectOwnership.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleNotifier.cs`
- Delete after cutover: `src/Namotion.Interceptor.Tracking/Lifecycle/StructuralReconciler.cs`
- Delete after cutover: `src/Namotion.Interceptor.Tracking/Lifecycle/AttachTraversal.cs`
- Delete after cutover: `src/Namotion.Interceptor.Tracking/Lifecycle/ReleaseTraversal.cs`
- Delete after cutover: `src/Namotion.Interceptor.Tracking/Lifecycle/ReachabilityWalk.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/GraphOwnershipTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/ReparentCascadeTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipChangeStreamTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleEventsTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipReservationProtocolTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/GraphBehavior/OccurrenceProjectionTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/GraphBehavior/CycleTests.cs`

- [ ] Add or update tests for final forward reachability: orphan cycle, anchored cycle, shared DAG, multiple roots, duplicate occurrences, rekey, reorder, replacement, and already-owned reparent target with a grandchild.
- [ ] Add a competing-context reparent test. Park after new support is staged and before old support is removed. Assert the retained target remains attached to the original context and the foreign attach fails.
- [ ] Add publication-sequence tests. Park a writer between two per-subject publications, run a public one-subject read and an internal multi-subject walk, and assert the public result is one immutable per-subject snapshot while the internal walk retries until it observes one even unchanged sequence.
- [ ] Add an admission-freeze race. Let a transaction stage release from `leaseCount == 0`, then attempt an ordinary parent lease and a proposed-child reservation before final publication. Assert each acquisition either linearizes before freeze and appears as an exact pending-release protector, or receives a prompt retryable conflict before returning its token. The parent lease conflict is before the interceptor chain; the proposed-child reservation conflict is after downstream interceptors but before the terminal store. Neither may return a token for an attachment epoch the transaction clears.
- [ ] Add a leased-descendant release case: park `child.Children` while its parent removes the last incoming edge. Give `child` an unleased `grandchild` and assert the parent write does not wait, both descendants remain attached and foreign-unclaimable in one `ReleasePending` group, and release plus detach notification occurs only when the child's final structural lease exits unless new support arrived.
- [ ] Add two overlapping pending closures protected by different leases. Assert the groups merge, disposing one lease cannot release any member still protected by the other, and only the final protector triggers recomputation. Acquire another same-context lease after the group forms and assert it joins the protector set. Add new same-context support before final disposal and assert reachable members survive without detach or attach churn.
- [ ] Add deterministic provisional-anchor cases for `B -> A` where A has the earlier context-wide attachment ordinal, two provisional subjects in a cycle, several provisional roots with one later explicit ancestor, self-edge, and back-edge. Allocate a unique context-wide `AttachmentOrdinal` when an attachment epoch first publishes context. After consuming roots reachable from explicit anchors, condense reachability between remaining provisional subjects into strongly connected components and retain the lowest-ordinal representative from every source component. Assert the result is independent of traversal and callback order.
- [ ] Update the reparent event assertion to the truthful stream:

```text
child new edge added, reference count 2
stepchild detached
child old edge removed, reference count 1
```

Do not emit context detach or attach for the retained child or grandchild.

- [ ] Implement `TopologyTransaction` as a staged delta over immutable snapshots. All identity comparisons are subject reference plus child ordinal.
- [ ] Under the topology gate, stage the candidate delta, then install a transaction-specific topology freeze on every executor whose attachment state may change, one monitor at a time. Retain each exact prior attachment-state reference. Revalidate exact lease identities, reservation identities, attachment revisions, and graph revisions after every freeze is installed. On every failure, restore all prior states in `finally` and retry staging without replaying the interceptor chain. Preallocate every unfrozen success state before marking the context publication sequence odd, then perform only nonthrowing swaps and mark the sequence even. Add fault-injection tests between freeze installations and before odd publication to prove no freeze is stranded.
- [ ] Mark every member of an unreachable closure protected by a structural lease or same-context ownership reservation as one `ReleasePending` group instead of clearing any member's context. Merge overlapping pending groups and their exact lease and reservation protector sets. A new same-context lease or reservation on a pending member joins the group before acquisition returns. Complete or cancel the group from the final protector's disposal after recomputing reachability.
- [ ] Implement pending-subject lease and reservation acquisition as revalidated slow paths in topology-gate-then-executor-monitor order. Never increment under the executor monitor and then request the topology gate. If finalization wins before the slow path, retry acquisition from the subject's newly published attachment state before running arbitrary code.
- [ ] Keep monitor-only token disposal only for a subject that is neither topology-frozen nor pending release. When either marker is observed, leave state unchanged, release the monitor, and complete disposal through the topology-gate-then-monitor slow path. Add a disposal race against the post-staging freeze.
- [ ] Ensure lease and reservation disposal drop the executor monitor before opening a deferred-release topology transaction. If disposal creates a later journal on the same thread, append it after the current transaction's journal and invoke both outside locks in local topology-revision order.
- [ ] Increment one context topology revision for each committed transaction and put it on every journal entry.
- [ ] Assert by code structure that transaction methods accept only snapshots, graph records, tokens, and library collections. They must not accept raw values or user delegates.
- [ ] Move callback invocation after gate release. Keep only journal construction under the gate.
- [ ] Include every journal's notification holds in the same staged attachment-state publication as the journal. Add an overlap test where a later detach commits before an earlier attach callback starts and assert exact context remains until both journals release their published holds.
- [ ] Publish `DetachCompleted` for the exact attachment epoch when the detach journal's final hold exits. Any hold that later decrements the epoch count to zero, including an older attach journal that finishes last, must clear context when that marker is present. Complete all holds through topology-gate-then-executor-monitor order, using only a pure decrement, marker update, and optional final context clear. Add a race against a frozen staged attachment publication and assert neither side overwrites the other's immutable attachment state.
- [ ] In this task, make `LifecycleNotifier` continue all handlers and entries after callback exceptions and release every published hold in `finally`. Ordinary journals aggregate back to their originating operation. A deferred journal created by final lease or reservation disposal releases every hold before tracing collected failures with context, revision, subject, and exception. Catch trace-listener failures so neither callbacks nor diagnostics can throw from token disposal, including when another exception is unwinding.
- [ ] Cut over explicit structural writes first. Then cut over attach, detach, and admission in Tasks 8 and 9 before deleting the old helpers. Delete each old helper only after `rg` finds no production reference.
- [ ] Run the independent ownership oracle and all graph behavior tests.

Run:

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore --filter "FullyQualifiedName~GraphOwnership|FullyQualifiedName~OwnershipOracle|FullyQualifiedName~ReparentCascade|FullyQualifiedName~OwnershipChangeStream|FullyQualifiedName~LifecycleEvents"
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore --filter "FullyQualifiedName~OccurrenceProjection|FullyQualifiedName~Cycle|FullyQualifiedName~Dag|FullyQualifiedName~Dictionary"
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --no-restore --filter "FullyQualifiedName~StructuralWriteLease|FullyQualifiedName~OwnershipReservation"
git diff --check
```

- [ ] Commit.

```bash
git add src/Namotion.Interceptor src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests src/Namotion.Interceptor.Registry.Tests
git commit -m "refactor: commit lifecycle topology from final reachability"
```

## Task 7: Publish revisioned callbacks outside framework locks

**Files:**

- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectLifecycleChange.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectPropertyLifecycleChange.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleNotifier.cs`
- Retain until Task 10: `src/Namotion.Interceptor.Tracking/Lifecycle/CallbackReentrancyGuard.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/IPropertyLifecycleHandler.cs`
- Modify: `src/Namotion.Interceptor.Registry/SubjectRegistry.cs`
- Modify: `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubject.cs`
- Modify: `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubjectProperty.cs`
- Add: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleNotificationConcurrencyTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleHandlerOrderTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/RegistryHandlerOrderTests.cs`
- Modify public API snapshots for Tracking and Registry

- [ ] Add overlap tests with two topology commits parked in callbacks. Assert no framework deadlock, each originating operation receives its own callback exception or aggregate after its complete journal, and built-in Registry applies the latest revision for each affected property even when an older same-property callback reaches Registry last. Include unrelated subjects to prove one context-global watermark is not used.
- [ ] Add notifier-level overlap coverage for notification holds and entity revisions. Task 8 owns the end-to-end detach-context-lifetime test because explicit detach is not cut over yet.
- [ ] Add context topology revision plus subject, property, and stable-edge revisions where applicable. Publish an immutable per-property occurrence projection containing child reference, child ordinal, and index payload. Do not expose mutable graph state.
- [ ] Resolve handler arrays and build journals under ordinary service snapshots, but invoke every handler and event after releasing the topology gate.
- [ ] Preserve within-journal ordering from the spec. Permit different threads' journals to overlap.
- [ ] Extend Task 6's per-subject active lifecycle-notification count with the entity revisions required for concurrent consumers. A detaching subject cannot be claimed by another context. The detach journal marks its exact epoch completed; whichever notification scope reduces that completed epoch's count to zero clears context without waiting.
- [ ] Add immutable property projection publication and make Registry atomically replace a property projection at the newest revision for that property. Retain the legacy `RefreshCollectionProperty(PropertyReference, object?)` adapter only for the old admission and reconcile callers through Task 9; it must call no user equality under Registry locks and Task 10 deletes it with the final old caller.
- [ ] Audit every built-in `ILifecycleHandler` and `IPropertyLifecycleHandler` for concurrent invocation. Add local revision suppression where it projects state; document thread safety where it only performs idempotent initialization.
- [ ] Remove callback-depth rejection only from callback paths already cut over to unlocked revisioned journals. Add same-thread callback tests for structural writes here. Task 8 owns explicit attach and detach callback reentry, Task 9 owns `AddProperties` and property-lifecycle callback reentry, and Task 10 deletes `CallbackReentrancyGuard` after `rg` proves no caller remains. Guard every test against accidental unbounded recursion.
- [ ] Stress Task 6's atomically published notification holds and exception draining under overlapping revisioned journals. Assert no subject remains `Detaching`, each ordinary operation receives only its own aggregate, and deferred final-protector notification still cannot throw from token disposal. Do not add a new public diagnostic event for the deferred contract-violation path.
- [ ] Run callback, registry order, connector source-monitor order, and public API tests.

Run:

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore --filter "FullyQualifiedName~LifecycleNotificationConcurrency|FullyQualifiedName~LifecycleHandlerOrder|FullyQualifiedName~CallbackContract|FullyQualifiedName~VerifyChecksTests.PublicApi"
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore --filter "FullyQualifiedName~RegistryHandlerOrder|FullyQualifiedName~VerifyChecksTests.PublicApi"
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --no-restore --filter "FullyQualifiedName~SourceMonitorHandlerOrder|FullyQualifiedName~SubjectUpdateEmissionOrder"
git diff --check
```

- [ ] Commit.

```bash
git add src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests src/Namotion.Interceptor.Registry src/Namotion.Interceptor.Registry.Tests src/Namotion.Interceptor.Connectors.Tests
git commit -m "fix: publish revisioned lifecycle callbacks outside locks"
```

## Task 8: Rebuild explicit attach and detach on the common transaction

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

- [ ] Add exclusive attach conflict tests for a structural lease, another attach, a detach, and a foreign component reservation. Assert prompt conflict and no partial publication.
- [ ] Add capture-version tests that mutate a detached structural property through its coordinated setter before and after the getter capture. Assert attach either retries capture and owns the latest child or fails before publication. It must never publish the stale child.
- [ ] Add provisional anchor tests for self-edge, back-edge, independent parent adoption, promotion to explicit, and concurrent failed promotion.
- [ ] Implement attach as exclusive reservation, unlocked snapshot capture, revision validation, pure topology transaction, then notification.
- [ ] Implement detach as exclusive transition, anchor removal and final reachability under the topology gate, detaching phase publication, unlocked notification, then final context clear.
- [ ] Remove claimed-but-unowned pass-through branches, `RollbackRejectedAttach`, seeded-baseline markers, and release-time getter scans.
- [ ] Preserve the exact context throughout all overlapping notification holds for a detaching subject and clear it when the final hold exits, including aggregate-exception paths.
- [ ] Add the end-to-end detach-context-lifetime test: park an earlier attach callback across a later detach callback, assert `GetContext()` remains the exact context throughout both callback bodies, and assert it becomes null only after the completed detach epoch's notification count reaches zero.
- [ ] Run all lifecycle attach, detach, anchor, and graph tests.

Run:

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore --filter "FullyQualifiedName~Attach|FullyQualifiedName~Detach|FullyQualifiedName~Anchor|FullyQualifiedName~AttachmentState|FullyQualifiedName~GraphOwnership"
git diff --check
```

- [ ] Commit.

```bash
git add src/Namotion.Interceptor src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests
git commit -m "fix: attach and detach through immutable topology transactions"
```

## Task 9: Rebuild `AddProperties` and derived validation on the common protocol

**Files:**

- Modify: `src/Namotion.Interceptor/Interceptors/SubjectPropertyRegistration.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/PropertyAdmission.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptorExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Change/DerivedPropertyData.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/AddPropertiesLifecycleTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/DetachCallbackAdmissionTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/NormalizingSetterDerivedRaceTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/ConcurrentPublicationVerdictTests.cs`
- Add: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/DerivedPendingTopologyTests.cs`

- [ ] Add `AddProperties` races for metadata generation change, attach and detach conflict, two same-name batches, shared same-context child reservation, foreign child reservation, callback reentry, and structural getter worker-wait. Assert input materializes once and each accepted structural getter is called once per capture attempt.
- [ ] Add immutable executor-owned authoritative metadata state while retaining the existing generated and `DynamicSubject` projection fields. Capture the initial `Subject.Properties` seed outside framework locks on first admission. Revalidate concurrent batches against executor metadata generation and merge from the executor snapshot under the short gate. Preserve `SubjectPropertyRegistration` and both existing `AddProperties` signatures so Connectors, Hosting, Registry, Tracking, third-party subjects, tests, and existing binaries keep compiling without a new public executor accessor.
- [ ] Preserve public `SubjectPropertyRegistration.Publish()` for alternative lifecycle implementations and add an internal friend-visible `PublishPrepared(mergedProperties)` path that invokes the existing continuation without rebuilding from `Subject.Properties`. Invoke that prepared publisher as the first post-commit admission-journal entry outside framework locks, then continue property and lifecycle callbacks in caller order. Generated and `DynamicSubject` publishers must retain their exception-free exact-assignment contract. If a deliberately invalid third-party publisher throws, retain executor-authoritative metadata, continue all entries, release every hold, include the exception in the originating operation's final aggregate, and do not claim that the framework repaired the third party's implementation-owned `IInterceptorSubject.Properties` projection.
- [ ] Capture batch and getters outside the topology gate, reserve prospective components, then validate names, executor metadata generation, attachment phase, property revisions, and reservations under the gate before publishing metadata and snapshots.
- [ ] Invoke property callbacks after commit in caller input order. A detaching subject may publish metadata only and never ownership edges.
- [ ] Remove the remaining property-callback scopes from `LifecycleInterceptorExtensions` only after admission uses unlocked journals. Migrate the final legacy raw refresh caller to immutable projection publication; leave removal of the now-unreferenced adapter and `CallbackReentrancyGuard` file to Task 10.
- [ ] Replace context-wide transaction-count inference in derived validation with the exact per-property pending descriptor when dependency capture identifies it. For an orphan read through an uninstrumented alias, first read exact descriptors from the observed subject's same-context reservation participants. Only if no participant explains it, snapshot Task 5's immutable untrusted-terminal registry and register an all-completed continuation against that exact descriptor set. Never wait for a later descriptor or for context-wide quiescence.
- [ ] Add no-lost-wakeup tests around `RegisterOrRun`: park a derived evaluation between observing and registering, finish the terminal transaction, and assert exactly one retry runs before outer derived and property-change publication. Cover the exact property descriptor, the reservation-participant path, and the untrusted-terminal alias fallback. Prove that a descriptor registered after the orphan read cannot delay or excuse it and that generated faithful writes never touch the context registry.
- [ ] Add a genuine orphan test where the retry fails. Store a sticky derived lifecycle fault and assert the next caller receives it. Do not trace and forget it.
- [ ] Remove `_transactionsInFlight`, `_withheldLock`, `_withheldRecalculations`, and `TryRunWhenTransactionEnds` after all references move to pending descriptors.
- [ ] Run admission and derived suites.

Run:

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore --filter "FullyQualifiedName~AddPropertiesLifecycle|FullyQualifiedName~DetachCallbackAdmission|FullyQualifiedName~NormalizingSetterDerivedRace|FullyQualifiedName~ConcurrentPublicationVerdict|FullyQualifiedName~DerivedPendingTopology|FullyQualifiedName~CallbackContract"
dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj --no-restore --filter "FullyQualifiedName~GeneratedExecutor"
dotnet test src/Namotion.Interceptor.Dynamic.Tests/Namotion.Interceptor.Dynamic.Tests.csproj --no-restore --filter "FullyQualifiedName~DynamicSubject"
git diff --check
```

- [ ] Commit.

```bash
git add src/Namotion.Interceptor src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests
git commit -m "fix: admit properties and derived values through pending topology"
```

## Task 10: Delete obsolete protocol code and review the public surface

**Files:**

- Delete if still present: `src/Namotion.Interceptor.Tracking/Lifecycle/StructuralReconciler.cs`
- Delete if still present: `src/Namotion.Interceptor.Tracking/Lifecycle/AttachTraversal.cs`
- Delete if still present: `src/Namotion.Interceptor.Tracking/Lifecycle/ReleaseTraversal.cs`
- Delete if still present: `src/Namotion.Interceptor.Tracking/Lifecycle/ReachabilityWalk.cs`
- Delete if still present: `src/Namotion.Interceptor.Tracking/Lifecycle/CallbackReentrancyGuard.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleScratch.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleNotifier.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/IPropertyLifecycleHandler.cs`
- Modify: `src/Namotion.Interceptor.Registry/SubjectRegistry.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/ILifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/IInterceptorExecutor.cs`
- Modify public API snapshots for Core, Tracking, Registry, Connectors, Hosting, MQTT, and OPC UA only where the protocol changed them
- Modify: `docs/design/tracking-lifecycle.md`
- Modify: `docs/tracking.md`

- [ ] Use `rg` to prove the whole-chain gate, raw baseline, recursive release, stale-edge validation, and context-wide transaction-deferral paths are gone.

Run:

```bash
rg -n "EnterStructuralWriteGate|ExitStructuralWriteGate|GetBaseline|SetBaseline|CommitsEdgeTo|_transactionsInFlight|_withheldRecalculations|RollbackRejectedAttach|CallbackReentrancyGuard|RefreshCollectionProperty" src
```

Expected: no production match. Test or migration comments may name removed behavior only when explaining the revised assertion.

- [ ] Verify `EnterStructuralWriteGate` and `ExitStructuralWriteGate` were removed in Task 5. Keep public attach, detach, events, handler interfaces, `SubjectPropertyRegistration`, and both existing `AddProperties` signatures. Remove only temporary migration seams that are now unreferenced.
- [ ] Reduce `LifecycleScratch` to collections still used by snapshot capture and pure transactions. Do not pool immutable published objects.
- [ ] Compare production line counts and dependency direction against both `c5079c6f` and master. The branch must be net-negative against `c5079c6f`; target a material reduction rather than a token deletion. Partition the remaining master delta into the PR's product semantics and the minimum synchronization protocol. If protocol code has merely moved or a legacy path remains beside its replacement, this task is not complete. Do not merge components that still own distinct invariants merely to reduce file count.
- [ ] Enforce the Global Constraints budgets: Core plus Tracking at +2,300 production lines or less over master, the five-project lifecycle scope at +2,800 or less, and no more than +12 net production files. Missing a budget is a simplification failure for local review, not a reason to hide code in fewer files.
- [ ] Update lifecycle documentation with the terminal sequence, lease conflict behavior, immutable snapshot contract, callback concurrency, normalizing-terminal boundary, direct mutable collection limitation, and sticky fault behavior.
- [ ] Run public API checks and inspect every received snapshot.

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --no-restore --filter "FullyQualifiedName~VerifyChecksTests.PublicApi"
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore --filter "FullyQualifiedName~VerifyChecksTests.PublicApi"
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore --filter "FullyQualifiedName~VerifyChecksTests.PublicApi"
git diff --check
```

- [ ] Commit.

```bash
git add src docs/design/tracking-lifecycle.md docs/tracking.md
git commit -m "refactor: remove obsolete lifecycle protocol phases"
```

## Task 11: Verify consumers and connector ordering

**Files:**

- Modify only if tests demonstrate a required migration: `src/Namotion.Interceptor.Registry/**`
- Modify only if tests demonstrate a required migration: `src/Namotion.Interceptor.Connectors/**`
- Modify only if tests demonstrate a required migration: `src/Namotion.Interceptor.Hosting/**`
- Modify only if tests demonstrate a required migration: `src/Namotion.Interceptor.Dynamic/**`
- Modify only if tests demonstrate a required migration: `src/Namotion.Interceptor.Mqtt/**`
- Modify only if tests demonstrate a required migration: `src/Namotion.Interceptor.OpcUa/**`
- Modify only if tests demonstrate a required migration: `src/HomeBlaze/**`

- [ ] Run focused consumer tests that depend on lifecycle ordering, exact context, parent projections, insert-before-populate, partial apply, and root membership.

Run:

```bash
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --no-restore --filter "FullyQualifiedName~DefaultSubjectFactory|FullyQualifiedName~SubjectUpdateInsertedItem|FullyQualifiedName~SubjectUpdateEmissionOrder|FullyQualifiedName~PartialApplyGraphState|FullyQualifiedName~SourceMonitorHandlerOrder"
dotnet test src/Namotion.Interceptor.Mqtt.Tests/Namotion.Interceptor.Mqtt.Tests.csproj --no-restore --filter "FullyQualifiedName~MqttTopicCache"
```

- [ ] For each failure, first classify it as a correctness defect, an explicit behavior delta from the spec, or an unrelated regression. Do not restore a stale intermediate callback state merely to make a snapshot green.
- [ ] Keep consumer fixes separated by package. Do not run connector integration tests unless a connector implementation changes.
- [ ] Commit only if a consumer code migration was required.

Suggested commit per affected package:

```bash
git commit -m "fix: consume revisioned lifecycle topology"
```

## Task 12: Final verification, performance decision, and PR update

For the local implementation phase requested by the user, stop after complete verification and the independent whole-branch review against the current PR head. Do not push, update the PR description, post review replies, or run the long benchmark until the user has reviewed the local branch and explicitly chooses those follow-up actions.

**Files:**

- Modify: PR description and review replies after code verification
- Modify benchmark baselines or documentation only when results require it

- [ ] Run a clean status and inspect all commits for unrelated files and prohibited attribution trailers.
- [ ] Build, run every non-integration test, and pack.

Run:

```bash
git status --short
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
dotnet pack src/Namotion.Interceptor.slnx
git diff --check origin/master...HEAD
```

- [ ] Run the concurrency filters repeatedly on at least two runtime configurations available in CI. Record exact commands, repetitions, and results in the PR.
- [ ] Audit lock callouts mechanically. Search every topology-gate, attachment-monitor, and terminal-lock scope and verify that only pure state operations occur.
- [ ] Recalculate the production C# delta using the Global Constraints classification. Reject completion if it is positive against `c5079c6f`; include the exact net delta against `c5079c6f` and master plus the largest added and deleted production files in the local review report.
- [ ] Ask the user whether to run the long comparison benchmark. If approved, read `docs/benchmarking.md` and run from the required external worktree:

```powershell
pwsh scripts/benchmark.ps1 -Filter "*LifecycleOwnershipBenchmark*","*ParentProjectionBenchmark*","*RegistryBenchmark*","*ServiceOrderResolverBenchmark.LinearChain*" -LaunchCount 3 -BaseBranch origin/master
```

- [ ] Evaluate allocations before CPU time. Treat `ServiceOrderResolverBenchmark.LinearChain` as the untouched noise reference. Investigate a structural fast-path regression before accepting it.
- [ ] Request an independent correctness review focused on every spec invariant and a separate simplification review focused on deleted phases and public surface.
- [ ] Update the PR description with preserved semantics, intentional deltas, exact verification evidence, benchmark evidence or explicit deferral, and remaining terminal contract boundaries.
- [ ] Reply to the grouped lifecycle/write-protocol review thread with the commit range that implements the redesign and the deterministic tests that close each finding.
- [ ] Do not merge until every required check is green and no sticky lifecycle-fault path is trace-only or silently ignored.
