# Single-Context Lifecycle Rewrite: Handover

Transient scaffolding, like the plan and spec beside it. Delete it with them when the work ships.

Continue the single-context lifecycle rewrite (PR #494). Work on branch `rewrite/single-context-impl`, worktree `/home/rico/GitHub/nib-single-context`. Run everything there; do not `cd` to the main repository.

## Read first, in this order

1. `AGENTS.md` for repository conventions, priorities and commands.
2. `docs/superpowers/plans/2026-08-21-single-context-lifecycle-simplification.md`, the section "Plan rewritten after the implementation spike (2026-08-23)" and everything under it. That section is authoritative and overrides the task text below it.
3. The spec's "Revision after implementation spike" section: `docs/superpowers/specs/2026-08-21-single-context-lifecycle-simplification-design.md`.
4. `docs/spike/SPIKE-FINDINGS.md` for the evidence behind both.

**The governing rule: port the spike's tests verbatim, re-derive the production code.** The spike branch `spike/single-context-lifecycle` is green, reviewed and benchmarked, but it was written against a specification since reversed in several places and carries workarounds whose reasons have expired. Its tests are the asset, because they encode cases found by failure rather than by reasoning. Its production code is not.

## State

Stages 1 through 4 are done and committed.

| Stage | What landed |
|---|---|
| 1 | `LifecycleOwnershipBenchmark`, byte-identical to the comparison base arm |
| 2 | `ISingletonContextService<TContract>` and singleton validation on `AddService`/`TryAddService` |
| 3 | The additive attachment mechanism: exact context, `SubjectAnchorKind`, attachment revision with lock-free reads, the `TryUpdateAttachment` compare-and-swap seam, `TryGetContext`/`GetContext`/`AttachToContext`/`DetachFromContext`, the structural write route with its own terminal cache, and fail-closed generator routing |
| 4 | Lifecycle ownership: occurrence-aware edges, backward-search reachability, deterministic release, lazy parent snapshots, decomposed into ten classes |
| 6 | Absorbed into stage 3, as correction 3a specifies. Not a separate stage. |

Stage 4 passed independent spec review twice and a code-quality review. The fixes from that last review are the final commit on the branch.

`InterceptorExecutor` still inherits `InterceptorSubjectContext`, fallback contexts still work, and `Context` and `SyncRoot` still exist. That coexistence is deliberate: it is what keeps every stage compiling until stage 11 removes them together.

## Next: stage 5, concurrency contracts

- Take the executor's attachment monitor **before** resolving the interceptor chain and hold it through the terminal, so transient races order rather than throw. Persistent cross-context conflicts still throw.
- Close the unattached structural write hole by short-circuiting **inside** that monitor when there is no attached context. This keeps master's construction cost. See design decision 6, which was rewritten after measuring; do not restore the unconditional executor publication it originally specified.
- Lock order is gate before `SyncRoot`, a total order given the getter contract.
- Add `[Conditional("DEBUG")]` guards for the two contract violations: a getter that writes a subject-typed property, and a structural write from a lifecycle callback.

Work stage 5 inherits, already identified:

- `ConcurrentStructuralWriteLeakTests` carries a tolerance filter matched on the attachment guard's own message, via a named constant. Stage 5 deletes exactly that constant and its call sites, and nothing else. The filter is deliberately narrow so that genuine single-context defects still surface.
- `LifecycleInterceptor.WriteProperty` holds the topology lock across `next`, while `SetStructuralPropertyValue` captures the attachment revision before chain resolution. That window is what makes the terminal guard throw; on a raw thread an unhandled rejection terminates the host process.
- `ClaimProposedComponent` runs under the topology lock but claims through the executor monitor, so claim and attachment ordering is still two locks deep.
- `ReleaseUnusedClaims` compensates for a suppressed or normalizing write. Once the monitor spans the terminal, most of it should become unreachable.

Simplification round one is scheduled immediately after stage 5, with two named candidates in the plan. Do not skip it.

## Review gates, mandatory

- Independent review after stages 4, 5, 8 and 11, by agents that did not write the code, reading the diff against the stage's stated intent rather than against the tests. A stage whose tests pass is not evidence the stage is right.
- Nothing is benchmarked, load-tested or run through the Connector Tester until independent agents confirm merge-readiness. Measuring an unreviewed tree produces numbers that get thrown away, and this repository has already produced two internally consistent but invalid benchmark comparisons.
- Reviewers get the stage intent, not the diff alone. A reviewer reading only a diff cannot tell an expired workaround from a deliberate one.

## Verification, and the traps already hit here

Baseline to hold: **26 test assemblies, 3393 tests, 0 failures, build with 0 warnings.**

- Build: `dotnet build src/Namotion.Interceptor.slnx`. Warnings are errors; zero warnings is the gate.
- Unit suite: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`.
- **Reconcile per-project counts keyed by assembly and target framework**, never by the summary line. Several projects are multi-targeted and emit one line per framework. In one session the harness silently omitted an assembly from its summary three times, a different assembly each time, and a fourth time emitted a summary line with the assembly name missing. Every occurrence produced a lower total that still read as success.
- Never pipe a long test run through `tail`. Capture to a file and grep it.
- `dotnet test --no-build` after a source edit runs the old binary. Rebuild on both sides of any experiment.
- Record diff composition in every commit message: `pwsh scripts/diff-composition.ps1 -PerProject`. Any stage whose purpose is removal must be net-negative in production code.
- Any risky replacement carries a `[Conditional("DEBUG")]` oracle that recomputes the previous answer and asserts agreement.

## Decisions taken, do not re-litigate

- The read path does not change. Moving getters out of `SyncRoot` would permit torn reads on 44 files' worth of wide value types. A getter that writes a subject-typed property is a contract violation, detected by a debug-only guard.
- The provisional anchor stays, and is cleared only by an edge whose parent has an anchored ancestor other than the subject itself. Clearing on the first edge of any kind is unsound and was proven so: `child.Parent = root` would consume the root's own anchor.
- Context-taking constructors create provisional anchors, not explicit ones. Dependency injection picks that constructor for every deserialized subject.
- Reachability is a backward search from the questioned subject up its committed incoming edges. A full context-local scan measured 135 times slower; a forward mark never helps the common shape; incremental maintenance measured four times slower. The forward mark exists only as an independent oracle in the test assembly.
- Any algorithm reading incoming edges must validate candidates against committed outgoing edges, because reconcile commits outgoing first. This is the most load-bearing undocumented detail in the area.
- `GetParents()` must never take the lifecycle lock. `SourceMonitor` holds its own lock across a graph walk that calls it, and is also called from inside the lifecycle lock, so a locking read deadlocks.
- Parent snapshots activate lazily: the first `GetParents()` on a subject sets a bit, and that subject publishes eagerly from then on.
- Detach callback order is top-down. Attach order is unchanged.
- Subtree-scoped subject-local services are removed. This happened early, in stage 4 rather than stage 11, because it retired four defects at once and shortened delegation from a chain of length N to one hop.

## The oracle

`OwnershipOracleTests` drives public APIs with seeded random mutations and cross-checks against an independently written forward mark. It found four real defects the 3,393-test suite did not. Keep it green at full breadth, 700 seeds with no skips, and extend it rather than working around it. If it starts skipping seeds again, that is a regression in itself.

## Open items

- **Parent-projection benchmark rows are missing.** The plan requires a row that never calls `GetParents()` and one that does, so lazy activation is priced rather than asserted. They must be added to this branch **and** to the comparison base `bench/scl-base` at `7a5d2ace` (worktree `/home/rico/GitHub/nib-scl-base`) in identical form, because the arms must share benchmark source.
- **Three tests flake under full-suite parallel load only**, and are not regressions: `ConnectorMetricsTests.WhenRestartResetIsStillRunning_…`, `SourceSubscriptionTests.WhenAnEventIsQueuedButNotYetDrainedAtDisposal_…`, `HeapSamplerTests.WhenCompactAndSampleCalled_…`. Confirm in isolation before treating any as a regression.
- **`docs/design/tracking-lifecycle.md` is stale** and stage 12 owns its rewrite.
- **The `GetOrAddSubjectId` movement is unexplained.** The spike measured it 15.4 percent slower, identically across all three reachability variants. Stage 4 only removes `Data` entries, so the remaining candidate is the stage-3 interface widening, settleable by a JIT disassembly diff rather than by more runs.

## For the PR description, which does not exist yet

Stage 4 fixes a pre-existing defect present in shipped v0.9.x: `ContextInheritanceHandler` composed the parent context on a subject's **first** attach but decomposed the parent of the **last** detach, so for a subject with two or more distinct parent subjects the composition survived the detach. A fully detached subject kept resolving the graph's lifecycle handlers and its whole write pipeline, and the stale fallback could later close a delegation cycle that made every read and write throw, unrecoverable through the object model. It is order-dependent, reproduces on the v0.9.1 tag, and on v0.8.0 and earlier, which have no cycle detection, the same shape dies on an uncatchable `StackOverflowException`. No issue was filed by request; the commit message is the only other record.

Breaking changes to list:

- Subtree-scoped subject-local services are removed. A service registered on a subject applies to that subject alone.
- Composing two contexts that each configure Tracking now throws at subject construction. Note the non-obvious case: `WithSourceMonitoring` reaches a lifecycle through `WithParents()`.
- Detach callback order flipped to top-down for handlers behind the descent.
- The generator base contract gained `Executor` and `SetStructuralPropertyValue`, so every base assembly built by the released generator rebuilds and takes the NI0012 fallback until updated.
- A second `ILifecycleInterceptor` on one context is a singleton contract conflict.

## Stage 4's code-quality review, not yet landed

The review ran against `5203d1ba` and its fixes were being applied when this branch was pushed, so they are **not** in the published history. Redo them; they are all small and each is worth doing. The review's verdict on substance was positive: the decomposition is real rather than nominal, allocation discipline is clean throughout (no LINQ, no closures, no boxing, no stray allocation on any lifecycle path), the two "one representation instead of two" decisions are the best in the change, and the oracle is the right kind of test.

Must fix:

1. **Stale documentation that reads as current.** `docs/design/tracking-lifecycle.md` is false end to end (it names deleted fields as the key data structures and states lock ordering in terms of structures that no longer exist); stage 12 owns the rewrite, so add a staleness banner rather than attempting it. `ConcurrentStructuralWriteLeakTests` class summary still narrates the old five-step concurrency model. `GraphOwnershipTests` around the promote-path test describes lazy anchor adoption that no longer exists. `docs/tracking.md` still presents `WithParents()` as what enables `GetParents()`, which a test now pins as the opposite.
2. **`StructuralValueScanner.HasKeyedOccurrences` pays a metadata lookup on every scalar structural write.** An `IInterceptorSubject` matches none of `null`, `string` or `ICollection`, so `parent.Father = child` falls through to `property.Metadata` plus a type-cache probe, twice per reconcile. Add `IInterceptorSubject` to the negative pattern and hoist `property.Metadata` in `Reconcile`, which `LifecycleInterceptor.WriteProperty` already holds.
3. **The concurrency tests can pass vacuously.** Every assertion is about the settled graph after the join, and a graph in which every racing write was rejected is equally consistent, so a regression rejecting all contended structural writes would leave the suite green. Add an `Interlocked` counter and assert at least one iteration committed.
4. **`HasAnchoredAncestor` is misnamed**: it returns true when the start node itself is anchored. The misnomer already caused a duplicated check in `ReleaseTraversal.IsStillHeld`. Rename to `IsAnchorReachable(start, excluded)` and drop the redundant pre-check.
5. **Extract a `LifecycleNotifier`.** `AttachTraversal`, `ReleaseTraversal` and `StructuralReconciler` each take the whole `LifecycleInterceptor` to reach four notification members, which makes the dependency circular and hands three internal classes the write path while the topology lock is held. Stage 5 must prove nothing re-enters chain resolution between taking the attachment monitor and the terminal; with the current shape that proof requires reading three classes instead of a constructor signature.

Also worth doing, all small: `ParentProjection` is nominal, with its activation guard implemented twice, and should fold into `SubjectOwnership` or `OwnershipGraph` (its deadlock rationale is load-bearing and must survive). The context-attach publication block is copy-pasted in `AttachTraversal`, with the snapshot-before-handlers subtlety commented in only one copy. `OwnershipGraph` repeats the structural-property filter five times in two spellings, `CollectCommittedChildren` and `SeedBaselines` are the same method apart from where the value comes from, and both rent scratch inside the per-property loop instead of outside. `AreBaselinesSeeded` returns on the first structural property, correct only because seeding is all-or-nothing under the lock, and needs one sentence saying so. `ReleaseTraversal.Release` rents outside the `try` that returns the buffer. Four tests carry two Act/Assert pairs and two of them assert the opposite of their own names. `DelegateLifecycleHandler` is duplicated byte-for-byte across two test files. Benchmark digits are baked into production comments in `ReachabilityWalk` and `ParentProjection` and will go stale silently; keep the shape of the argument in code and move the numbers to the pull request. The transitional `ILifecycleInterceptor` overloads share a name with quite different contracts and should become `OnContextComposed`/`OnContextDecomposed`, which also makes their stage-11 deletion a pure grep.

Deliberately declined, do not do: collapsing `LifecycleScratch` into a generic pool (real, but pooling-path risk, and simplification round one after stage 5 owns it), and renaming `AttachTraversal` (accurate but churns four files for a class stage 11 deletes).

One question left open rather than fixed: `OwnershipOracleTests` sorts both parent lists before comparing, so parent **order** is unverified. In an occurrence-aware model with indices that is plausibly meaningful. Decide whether it is; if so compare unsorted and pin it, and if not say why at the sort so the gap is deliberate.
