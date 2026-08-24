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
- `AddProperties` rejects duplicate names atomically (previously a silent last-wins replacement), materializes its input exactly once, and no longer takes `SyncRoot`; serialization moved to the lifecycle gate and the attachment monitor.
- Derived and non-intercepted properties never establish ownership edges; a dynamic derived subject-valued property no longer counts as a parent edge, and a derived subject-typed write also no longer passes the callback write guard, because the lifecycle exits before it.
- `AddProperties` on a subject owned by another context throws before enumeration when called from any lifecycle, subject event, or property lifecycle callback.
- `RegisteredSubject.AddProperty` with an existing name is an idempotent no-op when the shape (type and attributes) matches, keeping the first registration's accessors and running no property attach callback for that call, so `ISubjectPropertyInitializer`s do not rerun; a different shape throws. The previous behavior threw for the reattach rerun and silently replaced metadata on the subject. The synthetic initial null-to-value write is removed, so adding a dynamic property no longer emits an initial property change event.

## Stage 4's code-quality review: landed

Applied in `1776deba`. The review's verdict on substance was positive: the decomposition is real rather than nominal, allocation discipline is clean throughout (no LINQ, no closures, no boxing, no stray allocation on any lifecycle path), the two "one representation instead of two" decisions are the best in the change, and the randomised oracle is the right kind of test.

What changed, so you do not re-derive it: four stale comment or documentation sites corrected, including a staleness banner on `docs/design/tracking-lifecycle.md`, which stage 12 still owns; `HasKeyedOccurrences` no longer touches `property.Metadata` on the scalar back-reference path, which is the shape that dominates bulk graph construction; the concurrency tests now count commits and assert at least one, so they can no longer pass while rejecting every contended write; `HasAnchoredAncestor` became `IsAnchorReachable(start, excluded)` and the duplicated anchor check it hid is gone; a `LifecycleNotifier` now carries the notification surface, so the three collaborators no longer hold the whole interceptor; `ParentProjection` folded into `SubjectOwnership` and `OwnershipGraph` with its deadlock rationale intact; and the transitional `ILifecycleInterceptor` overloads became `OnContextComposed` / `OnContextDecomposed`, which makes their stage 11 deletion a pure grep.

Two consequences worth knowing. Extracting the notifier moved the events, and two connector tests reach an event's compiler-generated backing field by reflection to simulate a detach, so a test helper is pinning that field's location; it now hops through the notifier first. And that extraction is why this commit is production-positive (+113) rather than net-negative: its purpose is structure, not removal.

**The parent-order question is answered: order is not meaningful, deliberately.** `GetParents()` enumerates the inline slot then the overflow list, and removal promotes an overflow entry into the inline slot, so the sequence follows add and remove history rather than the property's occurrence order. The previous implementation returned a `HashSet`, so no consumer can have depended on an order either. The occurrence index carried in each entry is where the meaning lives, and the oracle compares those. Recorded as deliberate at the sort, in the `GetParents()` XML remarks, and in `docs/tracking.md`. If order ever must become meaningful, the storage has to change first.
