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
| 3 | The additive attachment mechanism: exact context, `SubjectAttachmentAnchorKind`, attachment revision with lock-free reads, the `TryUpdateAttachment` compare-and-swap seam, `TryGetContext`/`GetContext`/`AttachToContext`/`DetachFromContext`, the structural write route with its own terminal cache, and fail-closed generator routing |
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

- **Parent-projection benchmark rows: done.** `ParentProjectionBenchmark` exists byte-identically on this branch and on the comparison base `bench/scl-base` at `4be50401` (worktree `/home/rico/GitHub/nib-scl-base`). The setup invokes `WithParents` by reflection where it exists, fully qualified because the enclosing-namespace rule otherwise binds to Core's class of the same name and the lookup silently misses; both arms were probe-verified (active child reports its parents, toggle rows run steady state). The comparison base moved from `7a5d2ace` to `4be50401` for exactly this file.
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

## Final verification status

| Suite | Tests | Failures |
|---|---:|---:|
| Unit, whole solution | 3,409 | 0 |
| OPC UA, with integration | 353 | 0 |
| MQTT, with integration | 104 | 0 |
| WebSocket, with integration | 161 | 0 |
| HomeBlaze Services | 221 | 0 |
| HomeBlaze E2E | 23 | 20, unresolved, see below |

All three connector changes this work flagged for integration verification are clean: the OPC UA loader's provisional attach (which replaced the temporary-construction protocol), MQTT's root-mapping caching, and MQTT's Registry-membership ownership checks.

One test fix was needed and only the integration gate could have found it: `MqttServerLivenessTests` reflected on a private `SubjectDetaching` field of `LifecycleInterceptor`, which moved onto `LifecycleNotifier` when the notification surface was extracted. Two Connectors tests were corrected at the time; this third one is integration-only, so every earlier gate skipped it. Both remaining reflection sites now take the two-hop form.

**HomeBlaze E2E is unresolved and deliberately parked.** Playwright's Chromium was never installed on this machine, so the suite had never run at all; installing it took the failures from 23 to 20. All 20 remaining failures are the same call, `NavigateToDemoFolderAsync` timing out on `GetByText("demo")`, which is the fixture's first navigation step, while all 14 Markdown, Navigation, PageEdit and PluginLoading tests pass. That shape points at missing seed data rather than at this change, but an A/B against master was started and stopped before it finished, so **this is not established either way**. Anyone finishing this work should run that A/B before claiming the suite is unaffected.

## Still to run

- **Connector Tester.** Hours, does not run in CI. Note `HeapMB` trends upward on master too, about 0.08 MB per cycle on mqtt-chaos, so a single-arm upward trend is never on its own a regression finding.
- **Benchmarks: done**, see "Benchmark results" below. Both rounds ran, the control held, the noise floor was measured at about 6 percent, and the one unexplained delta was closed by JIT disassembly. Re-run only if the code changes.

## Multi-agent review, 2026-08-24

Five independent reviewers, none of which wrote the code, against `ea1a81d0`. Two used reproduction probes, one used mutation testing, one built an independent differential oracle. Findings below are stated with the verification that backs them.

**The ownership model itself is sound.** An independently written randomized differential oracle (400 seeds x 50 mutations over shapes the shipped oracle does not cover, including dictionaries with colliding keys and interleaved explicit attach and detach) plus 6 threads x 400 mutations x 40 rounds found no divergence. Reachability over occurrence-aware committed edges, baselines-as-outgoing-truth, the `CommitsEdgeTo` rule, the single backward walk and the provisional-anchor independent-support rule all held under adversarial reading. Every defect below sits at a seam, not in the model.

### Blockers

1. **`PropertyAdmission.Admit` had no `AreBaselinesSeeded` guard.** `AreBaselinesSeeded` answers from whichever structural property enumerates first, on the stated assumption that baselines are present or absent together. An edge-driven attach records ownership before the descent seeds, so a handler adding properties in that window commits one baseline and decides the pending seeding by name: either seeding is skipped and the subject's own children never attach, or it re-runs and duplicates this batch's edges (`GetReferenceCount() == 2` for one occurrence). Both reproduced. `AdmitUnowned` already had the guard. **Fixed**, verified by build plus 911 tests; a regression test is still owed because the defect is invisible to the current suite.

2. **The removal loop's released-parent early exit strands the unprocessed old occurrences.** When a callback releases the writing parent mid-removal, `StructuralReconciler` stops, but occurrences below the current index keep their incoming edges, and the parent's own release collects children from the already-committed new baseline, which no longer contains them. Reproduced: subjects stay attached forever, still registered, hosted services still running, reporting a detached parent. Re-attaching compounds rather than heals. Open.

3. **`AttachTraversal.Publish` has no released-subject guard.** If an exempt `AttachProperty` callback releases the subject mid-fan-out, the next iteration calls `GetContext()` on a detached subject and `InvalidOperationException` escapes through an ordinary generated setter, after the backing field was written and the baseline committed. `ReleaseTraversal` has the same unguarded shape. Reproduced, and found independently by two reviewers. Open.

4. **Explicit attach and detach bypass the reentrancy guard entirely.** `AttachSubjectToContext` and `DetachSubjectFromContext` take the gate with no `ThrowIfInsideCallback`, while `WriteProperty` and `TryAddProperties` both guard. A cross-lifecycle deadlock was reproduced: two threads each hold their own gate inside a callback and attach into the other's context. The design doc claims this case is detected, which is what made it easy to miss. Open.

5. **The property-callback exemption does not apply where documented.** `ThrowIfInsideCallback` tests `_callbackDepth` only; `EnterPropertyCallbackScope` bumps a separate counter whose own comment says it feeds only `IsInsideAnyCallback`. The descent runs inside the notifier's scope, so property callbacks below the top level throw. `DerivedPropertyChangeHandler` swallows the exception in `catch (Exception) { }`, so a documented-supported shape silently fails to initialize by graph depth, in Release. Two counters cannot express "the innermost scope is a property callback"; the cheapest fix is for the property scope to record the `_callbackDepth` it was entered at and exempt only when they match. Open, needs a design decision.

6. **HomeBlaze is a regression.** The A/B that was never finished came back: base passes 23 of 23, treatment fails. Failures are all content that renders only if the root subject is attached. Under investigation at the time of writing, with an explicit `Root.AttachToContext` in `RootManager` as the confirmation patch. The likely mechanism is the provisional anchor: a root constructed with a context that is later adopted into an already-rooted graph loses its anchor. If confirmed, this is the first real consumer put through the new anchor semantics and it broke on its ordinary startup path.

### Test coverage: the load-bearing mechanisms are unpinned

Mutation testing, 20 single-edit deletions against 911 tests (Tracking 593, Registry 169, Core 149), baseline green. Each of these passed 911 of 911 with the mechanism deleted:

- the `CommitsEdgeTo` rejection in `ReachabilityWalk`, which the design doc calls "the single most load-bearing invariant in the area", and the deliberate placement of `visited.Add(parent)` after it
- `ReleaseUnusedClaims` on the structural write path (the admission-path counterpart is covered; only the write path is blind)
- all six released-parent early exits in `StructuralReconciler`, including under the test named for them, whose data shape never reaches an exit
- the lock-free parent and reference-count read contract, which could start taking the gate with every test still green
- the detach handler fan-out order

New reachability finding: `ReleaseUnusedClaims` is reachable with first-party components today. `ValidationInterceptor` carries no ordering relation to `LifecycleInterceptor` and resolves behind it, so a failing validator on a subject-typed property runs that compensation path now. The comment at `OwnershipGraph.cs` asserting "First-party interceptors all order before the lifecycle" is wrong.

Oracle limits worth fixing, cheaply: it seeds `MarkForward` from the implementation's own `executor.Anchor`, so under-consumption of anchors is structurally invisible; its budget (8 seeds, 60 steps, 10 subjects) is 20-35x below where a known defect first appears, and the stressed run costs 6 seconds; and its model has no dictionary property, no `AddProperties`, no second context and no lifecycle handler, so keyed reconciliation, admission, reentrancy and callback ordering are all outside it.

### Concurrency

The total order holds within one context. Across contexts it does not, because there is one gate per lifecycle with no order among gates, and the contract that was supposed to prevent a thread holding two is the one blocker 4 shows is unenforced. Also open: a lifecycle registered between the routing decision and the chain resolution ends up in the chain but not the routing, taking the gate while the attachment monitor is held (medium, derived not reproduced); and the retry loops contradict spec decision 5, which chose ordering over retry precisely because retry can starve.

### Documentation

About 20 corrections, several in text written during this work: the callback-contract sentence in `tracking-lifecycle.md` (see blocker 4), the dynamic-proxy classification claim, one decomposition row, and an unconditional `SyncRoot` torn-read claim that does not hold when a context has zero read interceptors. Shipped docs carry two measurably false behavioural claims (`registry.md` on derived properties creating reference edges, `connectors-monitoring.md` on cross-tree sharing), two samples that no longer compile (`blazor.md`, `connectors-opcua-client.md`), and a generator diagnostics surface still naming the removed `Context` and `SyncRoot` throughout.

Convention violations introduced by this branch: hard wrapping in five `docs/tracking.md` paragraphs, em dashes in five test comments, and an issue reference in `ContextServiceResolutionTests.cs`.

### Simplification

`+2,132` production lines is `+1,078` executable, `+743` XML doc, `+130` rationale comments, `+170` blank. About 200 lines are uncontroversially removable (interface docs restating the design doc, a dead `AttachmentRevision` getter, an orphaned public overload, `ExceptionAggregation`, `ClearProvisionalAnchor` folding into `SetAnchor`). 255 lines of explicitly performance-only mechanism remain unpriced (`LifecycleScratch` 190, `StructuralValueScanner.Contains` 65); both can be priced with a compile-time toggle against rows that already exist. Two cost-table rows close: the `Enter`/`ExitStructuralWriteGate` seam is validated by writes at -8 percent, and inline single-edge storage passes on measured per-subject footprint. One row is stale: there is no `#if DEBUG` read-terminal duplication in the tree, and no `[Conditional("DEBUG")]` in production at all.

## Benchmark results, measured 2026-08-24

Treatment `rewrite/single-context-impl` at `0c8d2fdf` against `bench/scl-base` at `4be50401`, which is master `0418410c` plus two benchmark-only commits so both arms compile identical benchmark source, verified by md5 on all files and by an empty `git diff master..HEAD -- ':!*Benchmark*'` on the base arm. Both arms share merge-base `0418410c`. Each arm ran separately, never concurrently, CPU pinned throughout.

**The noise floor is about 6 percent.** The control ran twice on the identical treatment binary, once per round, and `MixedDependencies` moved 5.6 percent between the two. Cross-arm control spread was -3.4 to +3.3 percent, with control allocations identical to the byte on every row in every run. Treat timed deltas below 6 percent as nothing. Allocation counts are deterministic and are not subject to this.

### Round 1, lifecycle ownership and parent projection

| Row | Δ time | Δ alloc |
|---|---:|---:|
| SetScalarUnattached | -0.9% | 0 B both |
| SetStructuralUnattached | +4.8% (noise) | 0 B both |
| SetScalarAttached | +2.0% (noise) | 0 B both |
| ReplaceSingleChildReference | -2.3% | -16% |
| ReplaceCollectionUniqueChildren | -8.4% | -10% |
| ReplaceCollectionDuplicateChildren | +6.0% (noise) | +9% |
| ReorderCollection | +113% | +30% |
| ReplaceCyclicChildGraph | +455% | +210% |
| AttachAndReleaseSubtree | -20.5% | -13% |
| ReleaseSmallSubtreeFromLargeContext | -2.6% | -16% |
| RemoveOneParentOfSharedChild | +39% | -92.5% |
| RemoveOneParentOfSharedChildInLargeContext | +43% | -92.5% |
| ReleaseOrphanedCycle | +477% | +338% |
| RemoveSharedEdgesInBatch | +46% | -63% |
| EdgeToggleParentsInactive | +3.3% (noise) | -82.5% |
| EdgeToggleParentsActive | +10.3% | -45% |

### Round 2, registry

Nothing regressed. Every row is faster or flat.

| Row | Δ time | Δ alloc |
|---|---:|---:|
| AddLotsOfPreviousCars | -18.6% | -7.3% |
| IncrementDerivedAverage | -4.0% | = |
| WriteNoOp | -8.7% | = |
| Write | -8.0% | = |
| WriteWithTimestampScope | -4.8% | = |
| Read | -13.0% | = |
| DerivedAverage | -9.8% | = |
| ChangeAllTires | -3.4% | -5.7% |
| GetOrAddSubjectId | -1.6% | = |
| GenerateSubjectId | +0.6% | = |
| KnownSubjectsSnapshot | +0.04% | = |
| ReadParents | +1.9% | = |

### Three of the four large round-1 regressions are master doing less because it is wrong

Verified by probe against both arms, with master's opt-in `WithParents()` enabled so the comparison is not against a disabled feature:

| Scenario | master `4be50401` | rewrite `0c8d2fdf` | correct |
|---|---|---|---|
| orphaned 3-node cycle released | 4 subjects to 4, leaks all 3 | 4 to 1 | 1 |
| parent index after reversing an 8-item collection | `Items#0`, stale | `Items#7` | `Items#7` |
| parent entries for `[a, a, b]` | 1 | 2 | 2 |

So `ReleaseOrphanedCycle` and `ReplaceCyclicChildGraph` compare releasing a cycle against not releasing it: reference counting cannot collect cycles, so master leaks the ring permanently. `ReorderCollection` compares updating occurrence indices against leaving them stale. `ReplaceCollectionDuplicateChildren` is inside the noise floor and is not a cost at all.

The genuine same-work costs are `RemoveOneParentOfSharedChild` at +39 percent and `RemoveSharedEdgesInBatch` at +46 percent, both acyclic shapes where master's refcount decrement gets the right answer more cheaply than a backward reachability walk. Both come with large allocation wins, and the walk is bounded: 1,795 ns in a 500-subject context against 1,772 ns in a small one, so it is not scanning the graph.

### The read and write gains are attributable, confirmed by machine code

Chain composition is identical on both arms, 1 `IReadInterceptor` and 4 `IWriteInterceptor` of the same types in the same order, so the gain is not a shorter chain. The structural difference is that master gives every subject its own `InterceptorExecutor` acting as its context, while the rewrite shares one `InterceptorSubjectContext` across the graph.

JIT disassembly of the read path, driven identically on both arms:

| Method | master | rewrite |
|---|---|---|
| generated `get_Value():int` | 37 instruction lines | identical |
| `Node:GetPropertyValue[int]` | 39 instruction lines | identical |
| `InterceptorExecutor:GetPropertyValue[int]` | 80 instruction lines | 70 |

The whole difference sits in the executor: master executes a delegation-validity guard with dependent loads and a conditional out-of-line call to `InterceptorSubjectContext:ResolveDelegationChain` on every property read. The rewrite deleted the mechanism, so the instructions are gone. The guard's first test is whether the context has a delegation target, null for a root and non-null for a child, which is why every child-touching row moved and every root-only or subject-free row stayed flat.

### Verdict against the plan's cost table

Nothing in these numbers argues for deleting any of the five performance-only mechanisms.

- **Lazy parent activation**: validated by an ablation within one arm, 56 B inactive against 176 B active, and 56 B against master's 320 B. Keep.
- **Opaque `Enter`/`ExitStructuralWriteGate` seam**: does not register. Attached structural writes came out faster than master despite doing strictly more work. Keep.
- **`GetOrAddSubjectId` 15.4 percent from the spike**: does not reproduce, measured -1.6 percent. Closed with no mechanism needed, which is evidence for the re-derive rather than port rule.
- **`LifecycleScratch` pooling, inline single-edge storage, separate `Contains` and `CollectOccurrences` paths**: still unpriced as individual mechanisms. Cross-arm runs price the design, not its parts, and no intra-arm ablation was run for these three. The design-level result is strongly positive, so none is a deletion candidate on current evidence.

## Breaking changes, consolidated for the pull request

Assembled from the stage commit messages; the PR description draws from this list rather than re-deriving it.

**Removed APIs and capabilities**

1. Subtree-scoped subject-local services are removed. A service registered on a context applies to every subject attached to that context; there is no per-subject or subtree scoping. If subtree scoping returns, it is through the separately designed mechanism listed under deferred extensions.
2. `IInterceptorSubject.Context` and `IInterceptorSubject.SyncRoot` are removed. The interface is `Executor`, `Data`, `Properties`, `AddProperties`. The executor's terminal lock is internal.
3. `AddFallbackContext` and `RemoveFallbackContext` are removed, along with the whole fallback graph: delegation resolution, cycle detection and cross-context invalidation.
4. `WithParents()` and `WithContextInheritance()` are removed with no obsolete aliases. Parent tracking and context inheritance are intrinsic to `WithLifecycle()`, which `WithFullPropertyTracking()` and `WithRegistry()` both install.
5. `ContextInheritanceHandler` and `ParentTrackingHandler` are deleted. `[RunsBefore(typeof(ContextInheritanceHandler))]` and the equivalent `RunsAfter` migrate to `typeof(LifecycleInterceptor)`, which implements `ILifecycleHandler` at the former descent slot. Both positions keep their measured orders.

**Sealed and non-implementable surface**

6. `InterceptorSubjectContext` and `LifecycleInterceptor` are sealed. `IInterceptorSubjectContext` and `IInterceptorExecutor` are not independently implementable: attaching a subject to a foreign context implementation throws with a message naming the constraint, rather than silently attaching with zero interception.
7. `ILifecycleInterceptor` gained `EnterStructuralWriteGate`/`ExitStructuralWriteGate` and `TryAddProperties`, and its transitional composition hooks are gone; third-party implementations must update. The lifecycle gate is exposed as an opaque enter/exit pair rather than a lockable object.

**Behavioural changes**

8. Detach callback order is top-down for handlers behind the old descent (was bottom-up). Attach order is unchanged. Master had two detach orders; one traversal remains, so stop order is now the reverse of start order.
9. A subject constructed with a context is a provisional root: it stops being a root once it is attached into a graph that is already rooted somewhere else, and from then on it follows that graph. Anchored roots legitimately sit at reference count zero; `ReferenceCount` is never an ownership predicate.
10. Duplicate occurrences count: `[a, a, b]` is two edges for `a`, `GetReferenceCount()` answers 2, and removing one occurrence does not detach the subject.
11. Orphaned cycles are released. Master leaked a cycle whose last external reference was removed; the reachability model releases it deterministically.
12. `GetParents()` and `GetReferenceCount()` answer from the lifecycle on any `WithLifecycle()` context and return empty for unattached subjects. During a final-release detach callback, `GetParents()` on the releasing subject returns empty.
13. Composing two contexts that each configure Tracking throws at subject construction. Note `WithSourceMonitoring()` reaches a lifecycle through the former `WithParents()` path.
14. A second instance of any singleton-contract service on one context throws, including the same instance re-registered and a subclass beside the default. This covers the lifecycle, `ISubjectRegistry`, `ISubjectIdRegistry`, `ITransactionWriter`, `SourceMonitor`, `SubjectTransactionInterceptor`, `PropertyChangeInterceptor`, `HostedServiceHandler`, `PropertyValueEqualityCheckHandler`, `DerivedPropertyChangeHandler`, `ReadPropertyRecorder`, `ValidationInterceptor` and `DataAnnotationsValidator`. A custom `ITransactionWriter` or `ISubjectRegistry` alongside the corresponding configuration extension now throws where it was previously silently skipped or doubled.
15. Duplicate-name `AddProperties` throws atomically before any getter runs (was silent last-wins). Derived and non-intercepted properties never establish ownership edges. `AddProperties` on another context's subject from inside a lifecycle callback throws before enumeration. Registry `AddProperty` with an existing name and identical shape is an idempotent no-op that runs no property attach callback; a different shape throws.
16. Structural writes, explicit attach or detach, and cross-context `AddProperties` from inside a lifecycle callback are contract violations that throw in every build configuration. Property lifecycle callbacks (`AttachProperty`/`DetachProperty`) are a documented exemption.
17. The generator base contract gained `Executor` and `SetStructuralPropertyValue`, so every base assembly built by the released generator fails the contract check and takes the NI0012 root-mode fallback until rebuilt.
18. Adding a dynamic property no longer emits an initial property change event, and Registry's synthetic null-to-value initial write is gone.
19. MQTT connector ownership checks use Registry membership instead of `ReferenceCount <= 0`, so anchored roots at zero references stay cached, and MQTT caches connector root property mappings for the first time.

**Migration hazard**

20. A consumer whose configuration reads `.WithLifecycle().WithService(handler).WithContextInheritance()` must migrate to `.WithService(handler).WithLifecycle()`. Deleting the trailing call instead silently moves the handler from ahead of the descent to behind it.

**Pre-existing defects fixed by this work**

- `ContextInheritanceHandler` composed the parent context on a subject's first attach but decomposed the parent of the last detach, so for a subject with two or more distinct parents the composition survived the detach: a fully detached subject kept resolving the graph's lifecycle handlers and write pipeline, and the stale fallback could later close a delegation cycle that made every read and write throw, unrecoverable through the object model. Order-dependent; reproduces on the v0.9.1 tag; on v0.8.0 and earlier, which have no cycle detection, the same shape dies on an uncatchable StackOverflowException.
- `ISubjectRegistry` carried no singleton contract (only the concrete `SubjectRegistry` did), so a custom registry implementation plus `WithRegistry()` silently installed a second registry that broke resolution at first use.

## Open question: derived properties with a setter are stores, and this branch stops tracking them

Raised on PR 494 as a review comment on `docs/registry.md:120`: "a derived property with setter/getter can also be a store of a subject and might need to participate in graph tracking?"

It is a real regression, confirmed by reading master rather than reasoning.

**What master did.** `LifecycleInterceptor` on master tests `entry.Value is { IsIntercepted: true } && entry.Value.Type.CanContainSubjects()` at lines 180 and 225. There is no `IsDerived` exclusion, so master established ownership edges for derived properties, including derived-with-setter stores.

**What this branch does.** `OwnershipGraph.IsStructural` requires `IsDerived: false`, and `LifecycleInterceptor.WriteProperty` bails on `metadata.IsDerived` before any reconcile. The code comment justifies it as "a derived value is a projection of edges the stored properties already own".

**Why the justification is incomplete.** It holds for a computed projection such as `Current => Tires[0]`, where excluding it correctly avoids double-counting a subject the collection already owns. It is false for a generated partial property with a setter. `DerivedSetterPerson` in the test models is exactly that shape: `[Derived] public partial string? Nickname { get; set; }` has a real backing field. Substitute a subject type and there is no stored property owning the value: the derived property IS the store.

**The consequence is loud, not silent.** Decision 4's orphan check evaluates derived getters at attach, finds a subject the graph does not own, and throws `LifecycleContractViolationException`. So a consumer using `[Derived] partial Child? Current { get; set; }` goes from working on master to an exception here.

**Two candidate fixes.**

1. *Heuristic, available today.* `metadata.SetValue is not null` distinguishes the shapes, and `DerivedPropertyChangeHandler` already uses exactly that test for its "derived-with-setter" case. The rule becomes structural when `IsIntercepted && CanContainSubjects && (!IsDerived || SetValue is not null)`. Failure mode: a derived property whose setter decomposes into other stored properties (`set { Other.Child = value; }`) would create an edge and so would the underlying store, double-counting.
2. *Precise, needs a metadata addition.* The generator knows whether a property has a backing field: `partial X { get; set; }` does, an expression-bodied `X => ...` does not. Carrying that in `SubjectPropertyMetadata` distinguishes store from projection exactly, with no heuristic.

**Not yet done.** The three shapes (computed projection, derived partial with setter, decomposing setter) have not been probed on either arm. Establish the actual behaviour by experiment before choosing, because the first analysis of this area was wrong by treating all derived properties as projections.

**Coverage gap this exposes.** No test covers a subject-typed derived property with a setter. That is why the exclusion landed without anything failing.
