# Write Protocol Redesign: Handover

State as of 2026-08-29. Supersedes the lock-scope design for the write protocol core; everything else on the branch stands.

## Decision

**Keep the branch. Rewrite nine files.** Restarting from master was considered and rejected on measurement:

| Part | Size vs master | Status |
|---|---|---|
| Write-protocol core (9 files) | +2,017 / -474 | Rejected by external review |
| Other production (82 files) | +1,744 / -1,420 | Uncriticised |
| Tests (148 files) | +8,329 / -4,297 | Largest asset, makes the rewrite safe |

The external reviewer endorsed the architecture explicitly: one owning context per subject, explicit/provisional/inherited ownership, occurrence-aware edges, committed baselines, one ownership graph. Those are what the 82 uncriticised files were migrated onto, so a restart re-derives an endorsed design and repays the migration to reach the same place.

The nine files: `InterceptorExecutor.cs`, `WriteInterceptorFactory.cs`, and in `Tracking/Lifecycle/`: `LifecycleInterceptor.cs`, `StructuralReconciler.cs`, `StructuralValueScanner.cs`, `OwnershipGraph.cs`, `ReachabilityWalk.cs`, `ReleaseTraversal.cs`, `AttachTraversal.cs`.

**Size target: the rewrite must come in under +1,543 net lines.** Bigger is a signal the design is wrong, because the intent is to replace scattered guards with one rule.

## The spec: the external reviewer's five questions

The protocol must define:

1. The structural write's **linearization point**.
2. **Which code may execute** while topology state is locked.
3. How **incoming ownership is reserved across replacement**.
4. How **stale or reentrant scans** are detected.
5. **What state remains** after every rejected or failed operation.

Their verdict: "Keep the single-context ownership architecture, but redesign the structural-write transaction and lock scope before merging. This is not merely a collection of implementation bugs."

Also required: consolidate. Invariants are currently spread across executor, lifecycle interceptor, reconciler, ownership graph, scanner and release traversal, which makes the protocol hard to audit.

## Maintainer constraints

- Production diff **same size or smaller** than what the PR already carries.
- **No special handling in connectors.** The provisional-root cleanup added at three connector sites is a symptom: it forces consumers to know that provisional anchors exist, that an unconsumed one leaks, and how to release one. If a fix needs connector code, the fix is wrong.
- Guaranteed correct, no race windows. Constraining public contracts is a fallback only, with impossibility argued from code.
- Behavior and public API changes already approved are listed in the lock-scope design's delta list.

## Working hypothesis (unverified)

The four open problems share one cause: user-invocable code (getters, enumerators, dictionary-key `Equals`, stored setters) runs while topology state is partly mutated. One rule may answer all five questions:

> **Capture outside, validate inside, commit atomically. The locked section contains no user code and cannot fail.**

`PropertyAdmission` already implements this shape (materialize, invoke each getter once, capture, discover and claim, then publish) and is the one lifecycle area with no reported defect. Generalizing it to the write path is likely the smallest path, and is what would let the ad-hoc guards be deleted rather than added to.

If assignment is what attaches, connectors need no provisional dance at all. Check first whether the early attach is load-bearing for the OPC UA source path.

## Process loop

1. Reproduce all external findings as failing tests, committed before design. Record what does not reproduce.
2. Write the invariants answering the five questions.
3. Derive the minimal protocol. **Every mechanism must name the failing test that forces it.** No test, no mechanism.
4. Adversarial review, revise, repeat until a round finds nothing.
5. Send the design to the external reviewer before implementing.
6. Implement, review, benchmark, Connector Tester.

Exit: every repro green, an adversarial round finding nothing new, benchmarks inside the noise floor.

## Carry-over items not to lose

- **Conformance review of the current lock-scope implementation: CONFORMS**, all deviations neutral or better. One non-blocking gap it found: `ValidateWriteChainOrdering`'s `case IWriteInterceptor` arm (`InterceptorSubjectContext.cs:216-232`) checks only the registering interceptor's own `[RunsAfter]`; it does not scan registered lifecycles for `[RunsBefore]` naming it. Needs one symmetric loop. Unreachable in-repo (the built-in lifecycle declares no ordering attributes).
- Three points the lock-scope design underspecified and the implementer had to resolve: the throwing `PropertyReference.Metadata` accessor (resolved with `TryGetValue`, treating a missing name as scalar, confirmed correct and unreachable); a terminal-level substitute test for the expected-null overwrite because no seam exists to race it; and a **third** `TryClaimDiscovered` caller the design never named, `PropertyAdmission.ClaimCapturedComponents`. Carry all three into the new design.
- Benchmarks have not been run since 2026-08-22, and that recorded comparison was void (stale binaries, unpinned CPU) and is deleted. Re-derive against `04fab84a` (master plus benchmark scaffold, still reachable).
- Two deadlock repros exist and pass under the current implementation in ~51 ms: `GateChainDeadlockRepro.cs`, `MonitorAbbaRepro.cs`.

## Defect review of the lock-scope implementation (2026-08-29)

Found by a fresh reviewer after the conformance review returned CONFORMS. The conformance review could not see finding 1, because the implementation faithfully matches a design that has the hole. Fold all of these into the repro set; they are behaviour requirements regardless of implementation.

**1. Major, and the design's own field rules cause it.** The write-through arm sets `ExpectedAttachedContext = null` (`LifecycleInterceptor.cs:153`) so the terminal will re-route on a re-attach. But the terminal only evaluates the predicate when `IsStructuralRoute` is set (`WriteInterceptorFactory.cs:57`), and the design states the attached scalar arm never sets it (`IWriteInterceptor.cs:72`, `InterceptorExecutor.cs:244-252`). So a narrowed write (declared type structural, `TProperty` scalar) that saw the subject attached at routing and released by the time the lifecycle looks commits with **no gate and no monitor**. Scenario: T1 starts `SetValueNarrowed(42)` on a holder attached to C; T2 detaches the holder; T1 reaches the write-through arm and is descheduled; T3 attaches the holder to D, whose seeding reads the old child and registers it under D; T1 commits `42`, and the child is left attached to D through an edge that no longer exists. The existing test covers only the executor-observed-unattached arm.

**2.** `TryClaimDiscovered` now invokes user getters after pass 1 has claimed (`OwnershipGraph.cs:517`) and has no `try/finally`, so a throwing getter leaks claims on the `ClaimComponentForRoot` path (`LifecycleInterceptor.cs:384-402`). The write path and admission are covered by their own finallys.

**3.** `ValidateWriteChainOrdering` is asymmetric (see carry-over above), and neither it nor `PartitionLifecycleLast` considers `[RunsLast]`, which `ServiceOrderResolver` honours. A `[RunsLast]` write interceptor is silently moved upstream of the lifecycle while the equivalent `[RunsAfter]` is loudly rejected.

**4.** `GetServices<IWriteInterceptor>()` no longer reports execution order, deliberately, and no public doc says so.

**5.** The retry re-runs `ValidationInterceptor`'s validators every attempt, and `ClaimProposedComponent` can throw on attempt 2 where attempt 1 would have succeeded, surfacing a transient race as an exception that "only a persistent conflict throws" does not cover. First-party dispatch and transaction enlistment are correctly guarded: no double dispatch, no double enlistment.

**6. Performance added, identified not measured.** Every unattached scalar write now does a `Properties.TryGetValue` plus a metadata struct copy; `WriteUnattachedScalarValue` is not `NoInlining` unlike its sibling cold path; `PropertyWriteContext<TProperty>` gained four fields zero-initialised per attempt; one extra load and branch on the scalar return; the terminal lambda lost `static` and captures `chainHasLifecycle`; a monitor acquire/release per structural write that the terminal did not have. Also unconfirmed and needing `JitDisasmDiffable` rather than reasoning: both `ExecuteTerminal` and `Commit` now contain a `lock`, so their EH regions may block inlining and make the `AggressiveInlining` on `Commit` a no-op.

**7. The two deadlock repro tests can pass without proving anything.** Both wait on the opposite thread's rendezvous with a timeout and never assert the result. If the rendezvous times out the writes serialise, no deadlock is possible, and the test passes green. Fix before relying on them. They also sit at the test-project root rather than under `Lifecycle/` and are named `*Repro` rather than by the suite convention.

**8.** `HandWrittenSubjectWriteTests.WhenHandWrittenSetterAssignsAScalarProperty_...` asserts `WrittenPropertyTypes == [typeof(int)]`, which holds under either route since `TProperty` is fixed by the test's own setter. The old `GateEnterCount == 0` did distinguish them.

**Where it found nothing**, after enumerating rather than sampling: no lock-order inversion in library code (every attachment-monitor acquisition traced); no missed commit path other than finding 1 (structural, attached scalar, narrowed, unattached, cascade, `SetValueFromSource`, dynamic, transaction commit all walked); no non-terminating loop that is new; no memory-ordering defect; no pooled-buffer double-return or leak.
