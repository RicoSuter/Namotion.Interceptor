# Single-Context Lifecycle Simplification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace subject-local and fallback contexts with one nullable exact context per subject while preserving arbitrary object graphs, lifecycle callback behavior, Registry projection, and the scalar interception hot path.

**Architecture:** Core owns a lazy per-subject executor, exact attachment state, flat context services, singleton-service enforcement, interceptor execution, and a small public lifecycle seam. Tracking's single built-in `LifecycleInterceptor` owns explicit roots, occurrence-aware structural edges, authoritative parents, reachability across DAGs and cycles, callback ordering, and one reentrant lock per lifecycle/context. Registry remains an optional projection over committed Tracking notifications. Structural metadata admission uses a lazy, once-materialized continuation protocol so callback admission can fail before enumerating user input.

**Tech Stack:** C# 13 preview, .NET Standard 2.0 Core, .NET 9 feature libraries, xUnit, Verify/PublicApiGenerator, BenchmarkDotNet, PowerShell benchmark tooling.

**Design reference:** `docs/superpowers/specs/2026-08-21-single-context-lifecycle-simplification-design.md`

## Plan rewritten after the implementation spike (2026-08-23)

A full spike was executed against the previous plan. What follows replaces its task sequence. The spike branch is `spike/single-context-lifecycle` and its findings are in `docs/spike/SPIKE-FINDINGS.md`; the reachability variants are parked on `spike/reach-v1-backward`, `spike/reach-v2-cachedmark` and `spike/reach-v3-incremental`.

**Port the tests, re-derive the code.** The spike's production code is green, reviewed and benchmarked, but it was written against a specification that has since been reversed in five places, and it carries workarounds whose reasons have expired: in-flight bookkeeping that exists because backward search was retrofitted onto an edge-commit order it was not designed for, a throw on transient races that becomes ordering, eager parent publication that becomes lazy, a structural setter that skips the guard, and transitional dual-model scaffolding. Inheriting that means inheriting the workarounds, and code that already passes its tests does not get re-examined. The decomposition the previous plan specified also never happened: one class reached 1604 lines against master's 551.

The tests are the asset. They encode cases that were found the hard way and will not be re-derived from a specification: the duplicate occurrence whose new index collides with a retained one, which 3347 tests missed; the orphaned self-cycle master leaks with no coverage at all; a release descent re-entered from a removal callback; a competing context claiming a subject between validation and claim; and an attachment read taken from inside a caller's lock, which deadlocked against the first implementation and returns instantly against the second.

So the split is three ways:

- **Take as-is**, because they are decision-independent and landed clean: the benchmark scaffold, including the shared-parent matched pair and batch rows the original lacked, and the singleton context service contracts.
- **Port verbatim as the specification of correct behaviour**: every test written or rewritten during the spike, ported before the code they cover exists. A re-derived implementation must satisfy them.
- **Re-derive**: the lifecycle production code, under the corrected design, with the decomposition done up front rather than deferred.

Tests-first re-derivation is the safer option here, not the more expensive one. Reused code hides expired workarounds behind a green suite; ported tests make the hard-won cases mandatory for whatever replaces them.

### Sequence

Each stage must leave the tree building with zero warnings and the full unit suite green, verified by **per-project** counts diffed against a recorded baseline, never by the summary line alone.

1. **Benchmark base.** Cut a branch from master carrying only `LifecycleOwnershipBenchmark`, so both arms share benchmark source. The spike's fourteen rows already include the shared-parent matched pair and the batch row that the original scaffold lacked. Done: `bench/scl-base` at `7a5d2ace`, master `0418410c` plus the benchmark file only, worktree `/home/rico/GitHub/nib-scl-base`, outside the repository so BenchmarkDotNet does not abort on a second project file.
2. **Singleton context service contracts.** Landed cleanly in the spike, 266 lines, no behaviour change. Take as-is.

The work happens on `rewrite/single-context-impl`, worktree `/home/rico/GitHub/nib-single-context`, branched from `rewrite/single-context-lifecycle`. Both that worktree and the benchmark base sit outside the repository, so BenchmarkDotNet does not abort on a second project file and either can run an arm.

3. **Attachment mechanism, additive.** Exact context, anchors, attachment revision, lock-free reads, and the structural write route with its own terminal cache. The structural setter keeps the unattached short circuit here and gains the guard in stage 5; see design decision 6 for why closing that hole in this stage costs the whole construction path and buys no correctness while the exact context is not yet authoritative. Keep the executor inheriting the context for now: that is what lets every later stage stay green.
4. **Lifecycle ownership**, decomposed from the first commit. Occurrence-aware edges, anchors, deterministic release traversal, and backward-search reachability from the outset rather than a scan to be replaced later. Parent snapshots activate lazily. One unified `HasAnchoredAncestor(start, excluded)` serves both the release decision and anchor consumption; the forward mark does not exist in production and the oracle lives in the test assembly. The decomposition is fixed before any code lands, see below.
5. **Concurrency contracts.** Lock order is **lifecycle gate, then attachment monitor, then `SyncRoot`**, and stage 5 is production-negative. See "Stage 5 lock ordering, corrected" below; the earlier wording said the attachment monitor is taken before chain resolution, which is true but must not be read as before the gate. Persistent cross-context conflicts throw, transient races order. The unattached fast path enters the attachment monitor alone, rechecks that the subject is still unattached, and writes directly. Callback reentrancy rules, with a `[Conditional("DEBUG")]` guard for the two contract violations (a getter that writes a subject-typed property, and a structural write from a lifecycle callback) so Release pays nothing.
6. **Generator and Dynamic routing.** Absorbed into stage 3 and done there, because correction 3a lists the fail-closed classification among the additive mechanism and the structural helper cannot be emitted without it. Landed: scalar route only for provably subject-free declared types, `Executor` as an explicit implementation on generated and Dynamic subjects, and the enlarged base contract with its diagnostic for every base assembly built by the released generator. That last one belongs in the breaking-changes list.
7. **`AddProperties` atomicity.**
8. **Handler merge.** Delete the inheritance and parent-tracking handlers, remove their configuration extensions, migrate the three ordering attributes. The merged lifecycle takes the descent's place as the public ordering seam, so `[RunsBefore(typeof(ContextInheritanceHandler))]` becomes `[RunsBefore(typeof(LifecycleInterceptor))]` and both handler positions keep their current meaning and their measured orders. The merged lifecycle must implement `ILifecycleHandler` or those attributes will not bind.
9. **Singleton authorities**, and delete the multi-instance machinery that becomes unreachable.
10. **Consumer migration**, roughly 124 files. The largest stage. Categories and per-project counts are in the findings.
11. **Removal.** Fallback APIs, executor-as-context, `Context`, `SyncRoot`. Purely subtractive, so a compile error here is a missed call site rather than a design problem. Subtree-scoped services are no longer part of this stage: they were removed in stage 4, because the composition that produced them is the same mechanism that leaked a detached subject's context, deepened resolution linearly with the graph, and closed a resolution loop on mutual references. See the composition-target commit for the measurements.
12. **Docs and snapshots.** Sixteen generator snapshots and eight public API snapshots, plus the ordering section of `docs/design/tracking-lifecycle.md`, which is written entirely in terms of the deleted descent handler.
13. **Verification, full.** Every integration suite plus the Connector Tester, not a targeted subset, because this is a foundational change and two specific items force it: MQTT begins caching connector root property mappings for the first time, and every base assembly built by the released generator rebuilds. Benchmarks run per arm directly rather than through the comparison script, against `bench/scl-base` at `7a5d2ace`.

### Stage 5 lock ordering, corrected

An external review of `dc2020a2` found that the obvious implementation deadlocks. Taking the subject attachment monitor and then executing the interceptor chain inverts against release:

1. A structural write on a child holds that child's attachment monitor and waits for the lifecycle topology gate.
2. A parent removal holds the topology gate, reaches `ReleaseClaim(child)`, and waits for that same child attachment monitor.

The requirement to take the attachment monitor before chain resolution does not require taking it before the gate. The safe total order for an attached structural write is **lifecycle gate, then attachment monitor, then `SyncRoot`**, which Core must expose as a pre-chain synchronization seam for the built-in lifecycle:

1. Read the exact attached context and obtain its lifecycle synchronization gate.
2. Enter the lifecycle gate.
3. Enter the subject attachment monitor.
4. Revalidate that the exact attached context is unchanged; release and retry if it moved.
5. Resolve and execute the write chain through the terminal.

The unattached fast path enters only the attachment monitor, rechecks that the subject is still unattached, and writes directly; if an attachment landed, it releases and restarts through the attached path.

### Stage 5 must be production-negative

Once the attachment monitor spans chain resolution through the terminal, the write-path revision machinery is dead and must be deleted rather than layered over: `StructuralWriteInterceptorFactory` and its duplicated terminal bodies, the separate structural-write delegate cache and resolver methods on `InterceptorSubjectContext` and `ContextState`, `PropertyWriteContext.AttachmentRevisionAtEntry` with its extra constructor, `EnterAttachmentGuard`/`ExitAttachmentGuard` and the stale-write exception, and the narrow attachment-guard tolerance in `ConcurrentStructuralWriteLeakTests`. The ordinary cached write chain is reused inside the new synchronization scope. The generated structural helper stays, because it still selects the guarded route and implements the unattached fast path.

The attachment revision itself remains, solely for the public attachment-transition compare-and-swap. That is a separate concern from carrying a revision through every structural write, and removing it would require redesigning the public raw transition seam and reasoning about ABA across detach and reattach lifetimes.

Stage 5 also drops the accommodations for callback reentrancy it is about to forbid: the inexact same-property fallback search in `SubjectOwnership.RemoveIncoming`, the repeated `graph.IsOwned(parent)` checks after edge notifications in `StructuralReconciler`, comments stating that callbacks may write another structural property, and cleanup branches justified only by a downstream interceptor suppressing `next`, since the lifecycle is terminal. Removing the inexact edge fallback is a risky simplification and carries a debug oracle. `ReleaseUnusedClaims` cannot go wholesale: terminal and getter exceptions and a supported normalizing setter can still leave provisional claims unused. Only its downstream-suppression rationale expires.

The scheduled "in-flight edge bookkeeping" reduction candidate is already discharged by the re-derived stage 4: there is no overlay or edge ledger to delete, the old and new occurrence lists are real reconciliation inputs, and committed-outgoing validation is load-bearing.

### Correctness findings open against stage 4

Verified against `dc2020a2`, to be fixed in stage 5:

- **Ownership collections use default equality.** `OwnershipGraph._owned` and the three pooled collections in `LifecycleScratch` (visited sets, subject counters, index groups) key on `IInterceptorSubject` with default equality. Graph membership is identity everywhere else, and a hand-written subject may override `Equals`/`GetHashCode`, which can merge distinct nodes or make a mutable-hash subject unreachable. Use `ReferenceEqualityComparer.Instance` consistently; Registry carries the same pre-existing pattern and should agree with lifecycle identity.
- **`SubjectOwnership.IncomingCount` is read without synchronization.** Writes happen under the per-subject monitor; `GetReferenceCount` reads the plain `int` outside it. The query must stay lock-free, so make the field volatile rather than atomic: an interlocked read prevents tearing but does not give the publication semantics intended.
- **The scratch retention bound is misstated.** `LifecycleScratch` claims buffers are bounded by the widest structural value, but discovery and reachability sets can grow to a whole component or ancestor closure. Consider discarding oversized buffers on return instead of retaining a graph-sized buffer per thread. Measurement candidate, not a blocker.

### Decomposition, fixed before stage 4 begins

The spike reached 1748 lines in one file against master's 551, which is what the previous plan deferred and never did. The split is decided up front, target 300 to 500 lines per class, extracted as cohesive collaborators rather than by splitting one class across files:

| Class | Holds | Target |
|---|---:|---:|
| `LifecycleInterceptor` | write protocol shell, gate, attach and detach entry points, events, handler fan-out | 400-500 |
| `SubjectOwnership` | per-subject record: anchor, occurrence-aware incoming and outgoing edges, inline-to-list promotion | 300-400 |
| `OwnershipGraph` | the owned-subject map and its edge primitives, committed-edge queries, claim and release of candidates | 400-500 |
| `StructuralReconciler` | the write-time diff and reconcile for scalar, collection and dictionary properties | 400-500 |
| `ReachabilityWalk` | the single `HasAnchoredAncestor(start, excluded)` backward search, with candidate validation against committed outgoing edges | 200-300 |
| `ReleaseTraversal` | deterministic first-visit release from the removed edge, cycle drain | 250-350 |
| `ParentProjection` | lazy-activated parent snapshots behind `GetParents()` | 150-250 |

A class that exceeds 500 lines is a signal to split it further, not to accept it.

### Two cost questions stage 4 has to answer, not defer

- **`GetOrAddSubjectId` was measured 15.4 percent slower** on the spike, consistently and identically across all three reachability variants, so it belongs to the ownership model rather than to any algorithm choice. The error bars were 0.027 and 0.064 nanoseconds against a 4.4 nanosecond difference, so it is not within-run noise, and the one proposed mechanism (an extra `Data` entry from unconditional parent tracking) was probed and falsified twice. This repository's guidance says a movement of that size with no known mechanism gets settled by diffing JIT output rather than by more runs. Do that while the ownership state is still being shaped, not at the end when the layout is fixed.
- **Per-subject ownership state was measured at roughly 195 bytes**, about 975 kilobytes per operation across the 5000 subjects of `AddLotsOfPreviousCars`. The working decision is to accept it, keep `SubjectOwnership` as compact as correctness allows without contorting the design, and report the figure measured on the re-derived code rather than carrying the spike's forward. The spike's number includes transitional dual-model state that the re-derivation does not have, so the real figure should be lower. If it is not, that is the point to revisit, with a number rather than a budget guess.

**Answered in stage 4** (commit `feat: own graph lifecycle in one context`).

- **Per-subject ownership state: about 112 to 120 bytes, against the spike's roughly 195.** `SubjectOwnership` is 64 bytes: object header 16, an inline `PropertyReference` 16 and inline occurrence index 8 for the single-edge case, a list reference 8 for the rest, the count 4, the published parent snapshot reference 8 and the activation flag. The owned-subject map adds a concurrent-dictionary node at about 48 plus an amortized bucket slot. Nothing further is allocated for the common single-parent subject: an occurrence list appears only from the second incoming edge, and a `SubjectParent[]` only for subjects a consumer actually asked about. Across the 5000 subjects of `AddLotsOfPreviousCars` that is roughly 560 to 600 kilobytes per operation against the spike's 975. Two decisions account for the difference and both are structural rather than budgetary: committed outgoing edges are the property baselines rather than a second per-subject representation, and the anchor is read from the executor rather than mirrored into the graph state. Master is about 116 bytes per subject without `WithParents()` and over 270 with it, so this is at or below master in every configuration.

- **`GetOrAddSubjectId`: shaped by removing `Data` entries, and the residual movement is bounded to the attachment stage.** The measured path is `subject.Data.TryGetValue((null, SubjectIdKey), ...)`, whose key hashes a 39-character string on every call, so the only mechanism by which the ownership model can touch it is occupancy of that dictionary. Stage 4 therefore puts *nothing* in `subject.Data`: all ownership state lives in lifecycle-owned structures. It also removes master's per-subject reference-count entry along with the `AddOrUpdate` master performed on every edge change, and parents are no longer a `Data` entry under `WithParents()` either, so every subject in a tracking context now carries strictly fewer entries than on master. Since the spike measured the same 15.4 percent across all three reachability variants, the movement is invariant to every choice stage 4 makes, and stage 4 only removes entries. The remaining candidate is the attachment stage's interface widening: `IInterceptorSubject.Executor` reshapes the interface method table and `Data` is read through that interface on exactly this path. That is settled by a JIT disassembly diff of `GetOrAddSubjectId` between master and the attachment-stage commit, not by more runs, and it is outside the ownership stage's blast radius.

### Costs to validate at the final benchmark

Two different bars decide whether code stays. Code that buys **correctness** is kept, with no size argument. Code that buys **performance** is kept only when the measured win justifies its line count, and that is decided against the final numbers rather than up front: 3,000 lines for some percentage is not worth it, 200 lines for 5 percent is.

That rule is only applicable at the end if each performance-only mechanism is named and priced **as it lands**. A required row that never gets written silently converts a measurable decision into an opinion. So this list is maintained per stage, and a stage that adds a performance-only mechanism adds its row here.

| Mechanism | Stage | Lines | Priced by | Status |
|---|---|---:|---|---|
| `LifecycleScratch` pooling | 4 | ~190 | bulk construction and removal rows | row exists, unmeasured |
| Lazy parent activation | 4 | part of `SubjectOwnership` / `OwnershipGraph` | a row that never calls `GetParents()` and one that does | **row missing on both arms** |
| Inline single-edge storage before list promotion | 4 | part of `SubjectOwnership` | single-parent attach and removal rows | row exists, unmeasured |
| Separate `Contains` and `CollectOccurrences` paths | 4 | part of `StructuralValueScanner` | shared-parent removal, which drives reachability | row exists, unmeasured |
| `#if DEBUG` read-terminal duplication | 5 | 27 | read rows; Release codegen is byte-identical by construction | verified structurally, not timed |
| Opaque `Enter`/`ExitStructuralWriteGate` seam | 5 | ~22 | attached structural write rows | **accepted deliberately, to be validated** |

The last one is a cost taken for API hygiene rather than performance: it adds one interface dispatch per attached structural write, against a path that already resolves a service, discovers a subtree, claims, reconciles and fans out callbacks. The expectation is that it does not register. It was kept on that basis, and the final numbers decide whether that was right. If it registers, the alternative is exposing the gate object again, which is cheap to do because nobody implements `ILifecycleInterceptor` and breaking an implementer-facing seam costs little.

Also open from stage 4, and unresolved: `GetOrAddSubjectId` measured 15.4 percent slower on the spike, identically across all three reachability variants. Stage 4 only removes `Data` entries, so the remaining candidate is the stage 3 interface widening, and repository guidance says a movement that size with no known mechanism is settled by diffing JIT output rather than by more runs.

### Benchmark rows this plan adds

- Parent projection: a row that never calls `GetParents()` and a row that does, so lazy activation is priced against master rather than asserted. `RegistryBenchmark.ReadParents` is the existing control on the read side.
- The unattached structural write pair already exists and is the control that stage 3 left the construction path at master's cost. It starts measuring the guard in stage 5, when the short circuit moves inside the attachment monitor.

### Simplification rounds

The spike landed at roughly +2,241 production lines through stage 9, and the projected end state is about +1,700 net after the removal stages. That is a lot of code for a change whose headline is removing capabilities, and it warrants deliberate reduction rather than hoping the removal stages absorb it.

Two scheduled rounds, plus a standing rule.

**Round one, after the concurrency stage.** By then the write protocol, the attachment state and the reachability algorithm all exist and their interactions are known. Two candidates remain open; the other two were decided up front and moved into stage 4:

- **The attachment revision may be entirely redundant.** It was designed to detect a chain resolved before an attach landed mid-write. The corrected design takes the attachment monitor before resolving the chain and holds it through the terminal, so no such window exists. If that holds, this deletes the revision field, the extra field on the write context, the enter/exit guard pair, and possibly the second per-property terminal cache, whose body is a deliberate duplicate of the scalar terminal. One mechanism instead of three.
- **In-flight edge bookkeeping.** The backward search needs auxiliary lists only because it was retrofitted onto an order that commits outgoing edges before incoming records. Designing that order deliberately may remove them.

**Round one is discharged, verified after stage 5.** Both candidates are closed, neither needing a dedicated pass:

- *The attachment revision.* Stage 5 deleted it from the write path entirely, along with `StructuralWriteInterceptorFactory`, the second per-property terminal cache, `PropertyWriteContext.AttachmentRevisionAtEntry` and the enter/exit guard pair. Zero references to any of them remain. It survives only on `IInterceptorExecutor` and `InterceptorExecutor`, for the public transition compare-and-swap, which is a separate concern: removing it there would mean redesigning the public raw transition seam and reasoning about ABA across detach and reattach lifetimes. One mechanism instead of three, as the round predicted.
- *In-flight edge bookkeeping.* Never existed in the re-derived implementation. The spike needed auxiliary lists only because backward search was retrofitted onto an edge-commit order it was not designed for; deriving the order deliberately removed the need. Confirmed by an external review and by search: no overlay, ledger or pending-edge structure anywhere in `Lifecycle/`. The old and new occurrence lists are genuine reconciliation inputs, and committed-outgoing validation is load-bearing rather than compensatory.

**Round two, after the removal stage.** With the fallback graph, executor-as-context and the transitional members gone, re-examine what the surrounding machinery was compensating for. In particular the context service snapshot, its caching and its invalidation were shaped by a fallback graph that no longer exists, and the delegation-era rationale comments must be rewritten rather than carried over.

**Composition target, moved earlier.** Design decision 7's removal of subtree-scoped subject-local services landed in stage 4 rather than stage 11. `ContextInheritanceHandler` composes the exact context a subject was claimed for instead of the executor of the parent that first referenced it. Measured: the reachability oracle's 21 delegation-cycle seeds per 700 go to zero, resolution depth on a 20-level graph goes from 21 hops to 1 with service arrays still reference-identical, and the change is net negative in production code. It also fixes a defect shipped in 0.9.x, where a subject with more than one parent kept resolving the graph's services after it detached.

**Anchors: decided, no fourth variant.** The alternative framing was evaluated against master's measured behaviour and rejected: a constructor that only records the context without attaching breaks the documented headline pattern, since `new Person(context)` would then track nothing. Master gives the constructor no durable anchor at all, which is why it silently half-detaches the caller's own root as soon as a back-reference exists. The provisional anchor stays, stated for consumers as "a subject constructed with a context is a root; it stops being a root once it is attached into a graph that is already rooted somewhere else, and from then on it follows that graph". The reduction that was actually available is not a new rule but a unification: `EdgeProvidesIndependentSupport` and the backward reachability search collapse into one `HasAnchoredAncestor(start, excluded)`, and with the forward mark gone the anchored-root set goes with it. Both are folded into stage 4 rather than deferred to a later round. See design decisions 9 and 10.

**Standing rule.** No stage commits without deleting what it made dead, and any stage whose purpose is removal must show a net-negative production diff. Measure with `pwsh scripts/diff-composition.ps1 -PerProject`, and record the number in the stage's commit message so growth is visible as it happens rather than at the end.

### Review gates

The plan was reviewed four times before implementation; the implementation was not. Stages that change semantics rather than add surface get an independent review before the next stage builds on them, performed by agents that did not write the code, reading the diff against the stage's stated intent rather than against the tests. A stage whose tests pass is not evidence the stage is right, which is the failure mode "code that already passes its tests does not get re-examined" names.

- **After stages 4, 5, 8 and 11.** Ownership, concurrency, the handler merge and the removal. Each either changes an observable contract or deletes a mechanism something else depended on. Review the diff for contract drift, lock order, and behaviour the tests do not pin.
- **Before stage 13 measures anything.** No benchmark, Connector Tester or integration run starts until independent agents confirm merge-readiness. Measuring an unreviewed tree produces numbers that have to be thrown away when the code changes, and this repository has burned two complete, internally consistent, invalid benchmark comparisons already.
- **Reviewers get the stage intent, not the diff alone.** The corrected design's decisions are the specification; a reviewer who only reads the diff cannot tell an expired workaround from a deliberate one.

### Standing rules for every stage

- Reconcile per-project test counts against a recorded baseline. Two separate mechanisms have been observed reporting success while coverage shrank.
- Never pipe a long test run through `tail`; capture to a file and grep it.
- Any risky replacement carries a `[Conditional("DEBUG")]` oracle that recomputes the previous answer and asserts agreement.
- Do not trust a benchmark comparison whose arms agree on a row where the algorithm changed. Validate against an independently measured mechanism.
- Any algorithm reading incoming edges must validate candidates against committed outgoing edges, because reconcile commits outgoing first.
- Delete what the stage made dead before committing it.

### Effort

The spike reached the equivalent of stage 8 of 13 and consumed a full working session with parallel agents throughout. Stages 10 and 11 are the bulk of the remaining volume, and stage 13's full connector and integration verification does not run in CI and takes hours by itself. Plan accordingly; the previous estimate was wrong because it assumed the ownership nucleus could be replaced without touching the surrounding machinery, and every stage above touches it.

## Corrections after implementation review (authoritative, override the task text below)

Four independent verification passes checked this plan against the code. Findings and evidence are in `docs/spike/SPIKE-FINDINGS.md`. The corrections below take precedence wherever they disagree with a task.

**Ordering.** The original Tasks 3 and 4 cannot be executed as written. Task 4 removed `AddFallbackContext`/`RemoveFallbackContext` while six production callers outside its file list still used them, so the tree did not compile from Task 4 through Task 9 and Task 9's own cross-project gate was unreachable. The Task 3 shim could not bridge the gap either, because `Context => Executor.GetContext()` throws on unattached subjects while the canonical attach idiom is `subject.Context.AddFallbackContext(ctx)` on an unattached subject, 108 occurrences across 29 files.

There is no source-compatible shim for `subject.Context` once the executor stops being a context, because an unattached subject then has nothing to return. Naively that forces one atomic commit across all 124 affected files. It is avoidable, and the avoidance is the single most useful structural change to this plan: **keep `InterceptorExecutor : InterceptorSubjectContext` until the very end**. The old fallback path and the new exact-context state then coexist, and the cutover stages cleanly:

- **3a. Mechanism, additive.** Add `IInterceptorSubject.Executor` as an explicit interface implementation, the nullable exact-context field, the explicit and provisional anchor bits, the attachment revision, the `GetContext`/`TryGetContext`/`AttachToContext`/`DetachFromContext` extensions, `SetStructuralPropertyValue` with its own terminal cache, and the fail-closed generator classification. The executor is still a context, fallbacks still work, every existing path is untouched. The tree compiles and the whole suite passes.
- **3b. Authority switch.** Point the built-in lifecycle at the new exact-context state instead of fallback composition. Behaviour changes here; the fallback APIs still exist but the lifecycle no longer drives them.
- **3c. Consumer migration.** Move all 124 files to `GetContext`/`TryGetContext`/`Executor`. Still compiling at every step because the old members remain.
- **3d. Removal.** Delete the fallback APIs, `InterceptorExecutor : InterceptorSubjectContext`, `Context`, and `SyncRoot`. This commit is large but purely subtractive, so a compile error in it is a missed call site rather than a design problem.

Subsequent task numbers shift accordingly.

**C1. `GetParents()` must not take the lifecycle lock.** It reads an immutable per-subject snapshot published by the lifecycle. `SourceMonitor` holds its own lock across a graph walk that calls `GetParents()`, and is also called from inside the lifecycle lock, so a locking `GetParents()` deadlocks. Occurrence-stable indices are new work with new tests, not a carry-over.

**C2. No mirrored Roslyn classifier.** Delete that work item. The generator emits the scalar route only for provably non-subject declared types and the structural route for everything else. A symbol classifier cannot match the runtime one, and a false negative silently skips the guard.

**C3. Context-taking constructors create provisional roots**, cleared by the first inherited edge that provides independent support (the edge's parent has an anchored ancestor other than the subject itself). Clearing on the first edge of any kind is unsound, proven during implementation: `child.Parent = root` would consume the root's own anchor and the next removal would release the whole graph. `AttachToContext` stays strict. This removes the temporary-construction try/finally protocol from every loader and applier, which was the plan's hardest migration item. It also fixes an otherwise fatal collision: the context is a dependency-injection singleton, so `ActivatorUtilities` picks the context-taking constructor for every deserialized subject, and strict roots would make `RootManager`'s attach throw at startup and leak the entire device graph.

**C4. The lifecycle lock is held across the terminal write.** Terminal ordering is the precondition, not the cause. Do not delete the concurrent-baseline repair before the lock scope changes. Keep the authoritative getter reread; it also serves normalizing setters.

**C5.** The executor monitor covers metadata publication as well.

**C6.** Superseded by design decision 6 as rewritten: measuring it showed the cost falls on the whole construction path rather than on one allocation, and that the guard protects nothing until the exact context is authoritative. The short circuit stays in stage 3 and the hole closes in stage 5 under the attachment monitor. The benchmark row still measures the unattached structural write, now as the control that the construction path did not regress.

**C9.** A second per-`TProperty` terminal cache carries the structural route so the scalar terminal is untouched. One shared `PropertyWriteContext` and one interceptor-set-keyed terminal cannot otherwise keep the scalar path free of the check.

**Files missing from every task, all of which break the build when reached:** `Tests/InterceptorTests.cs`, `Tests/Context/ContextStateReflection.cs`, `Tracking.Tests/Lifecycle/PropertyReferenceSetTests.cs`, `Generator.Tests/SubjectBaseDiagnosticsTests.cs` and all 16 generator Verify snapshots, `Tracking.Tests/WriteTimestampTests.cs`, `ConnectorTester/Hosting/ConnectorTesterHost.cs` and nine ConnectorTester test files, `HomeBlaze.Services/ConfigurableSubjectSerializer.cs`, `SubjectFactory.cs`, `ServiceCollectionExtensions.cs`, `Connectors/DefaultSubjectFactory.cs`, `Registry/SubjectRegistryExtensions.cs` nullability at the two hot lookups, `Namotion.Interceptor.Testing/ObjectExtensions.cs` (delete rather than migrate).

**Simplification gate, added to every task.** Before committing, delete what the change made dead and confirm the diff is net-negative wherever the task's purpose was removal. Named opportunities: the multi-`SourceMonitor` paths become unreachable once the singleton contract lands (the count-throw branch, `WaitForAllMonitorsAsync`, the composite disposable, and `SubjectSourceBase`'s monitor array with its unwind path); `ContextInheritanceHandler` and `ParentTrackingHandler` collapse into the lifecycle along with their configuration extensions and the ordering-cycle hazard they created; `PropertyReferenceSet` and its tests go with the reconciliation rewrite. Anti-simplifications that must survive: the authoritative getter reread, the per-frame `ServiceOrderResolver` call, instance dedup by reference, and `ContextState.IsEmpty` reduced to the service array rather than deleted.

## Non-negotiable implementation constraints

- Preserve correctness first, then allocations/CPU, then style.
- Do not add or expand `InternalsVisibleTo`. Exercise lifecycle graph behavior through public subject, context, and Tracking APIs. Remove existing friend access only when this work naturally makes an entry unused.
- Do not add obsolete aliases for removed APIs. Temporary source-compatible members are allowed only inside intermediate tasks and must be deleted before Task 11 is committed.
- Do not add a guard for repeated interceptor continuations. Zero or one `next` call is a documented high-performance contract.
- Do not add a process-wide topology lock. The built-in implementation has one reentrant lock per lifecycle/context plus each executor's private monitor.
- Do not add rollback for exceptions thrown by lifecycle callbacks, backing writers, or metadata publishers. Those callbacks are synchronous and exception-free by contract; violations propagate.
- Do not use `ReferenceCount` as an ownership predicate. Ownership is `subject.TryGetContext() != null`; Registry membership is queried through Registry.
- Keep scalar setters on the existing direct/inlined route. Only properties whose declared type can contain subjects use structural attachment-revision capture and validation.
- Treat every collection or dictionary occurrence as an edge. Repeated references such as `[a, a, b]` count as two edges for `a`.
- Preserve existing callback and handler order, except for the approved change that authoritative `GetParents()` state is visible before the first handler.

## Task 1: Add a benchmark scaffold before changing runtime behavior

**Files:**

- Create: `src/Namotion.Interceptor.Benchmark/LifecycleOwnershipBenchmark.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/Program.cs` only if the local debug entry point must recognize the new benchmark
- Test: benchmark project build

- [ ] Add generated benchmark subjects with a scalar property, a subject reference, a subject list, and a small cyclic shape.
- [ ] Add stable benchmark methods that compile against both the current and final public APIs: unattached scalar set, lifecycle-attached scalar set, structural reference replacement, duplicate-list replacement, and cyclic structural replacement. Set up attachment through context-taking constructors, which remain supported in both versions.
- [ ] Add `[MemoryDiagnoser]`; amortize sub-nanosecond scalar operations with `OperationsPerInvoke` where necessary.
- [ ] Do not benchmark removed fallback APIs or new attach/detach method names in this scaffold, because the file must remain source-identical at the comparison base.
- [ ] Run `dotnet build src/Namotion.Interceptor.Benchmark/Namotion.Interceptor.Benchmark.csproj -c Release`.
- [ ] Commit with `test: scaffold lifecycle ownership benchmarks` and record that commit hash as `LIFECYCLE_BENCHMARK_BASE` in the implementation notes or PR checklist. Final comparisons use this exact hash, not a moving branch.

## Task 2: Enforce generic singleton context-service contracts

**Files:**

- Create: `src/Namotion.Interceptor/ISingletonContextService.cs`
- Modify: `src/Namotion.Interceptor/InterceptorSubjectContext.cs`
- Modify: `src/Namotion.Interceptor/IInterceptorSubjectContext.cs` to document singleton validation on `AddService` and `TryAddService`
- Create: `src/Namotion.Interceptor.Tests/Context/SingletonContextServiceTests.cs`
- Modify: `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] Write public-API tests for `ISingletonContextService<TContract>` and behavior tests named `WhenSecondSingletonContractIsAdded_ThenThrows`, `WhenSameSingletonInstanceIsAddedTwice_ThenThrows`, `WhenServiceImplementsTwoSingletonContracts_ThenBothAreReserved`, `WhenTryAddPredicateMatches_ThenFactoryIsNotCalled`, `WhenTryAddFactoryConflicts_ThenThrows`, `WhenTryAddFactoryReentrantlyAddsContract_ThenRevalidationThrows`, and `WhenSubjectsAreOwned_ThenSingletonCanStillBeAdded`.
- [ ] Run `dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~SingletonContextServiceTests"` and confirm the new tests fail for the intended missing contract/validation behavior.
- [ ] Add the empty public marker:

```csharp
public interface ISingletonContextService<TContract>
{
}
```

- [ ] Cache the closed singleton-contract interfaces per implementation `Type`. Validate only the context's directly registered immutable service snapshot. Do not inspect fallback contexts and do not put reflection on interceptor lookup or execution paths.
- [ ] Under the existing context mutation lock, validate `AddService` before publication. In `TryAddService`, preserve the predicate-before-factory behavior, invoke the factory at most once, re-read the state after a reentrant factory, validate the produced service against that latest state, then publish.
- [ ] Ensure a duplicate throws even when the same instance or a different registration generic type is used.
- [ ] Run the focused tests, then `dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~Context"`.
- [ ] Accept the Core public API snapshot and inspect the diff to ensure only `ISingletonContextService<TContract>` was added in this task.
- [ ] Commit with `feat: enforce singleton context service contracts`.

## Task 3: Introduce exact executor attachment state and the structural write route

**Files:**

- Create: `src/Namotion.Interceptor/Interceptors/InterceptorSubjectAttachment.cs`
- Create: `src/Namotion.Interceptor/Interceptors/SubjectPropertyRegistrationContext.cs`
- Move and modify: `src/Namotion.Interceptor.Tracking/SubjectPropertyTypeExtensions.cs` to `src/Namotion.Interceptor/SubjectPropertyTypeExtensions.cs`, retaining the `Namotion.Interceptor.Tracking` namespace so the public source API stays stable while the implementation moves into Core
- Modify: `src/Namotion.Interceptor/IInterceptorSubject.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/IInterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/IWriteInterceptor.cs`
- Modify: `src/Namotion.Interceptor/InterceptorSubjectExtensions.cs`
- Modify: `src/Namotion.Interceptor/PropertyReferenceExtensions.cs`
- Modify: `src/Namotion.Interceptor/SubjectPropertyMetadata.cs`
- Modify: `src/Namotion.Interceptor/Cache/ReadInterceptorFactory.cs`
- Modify: `src/Namotion.Interceptor/Cache/WriteInterceptorFactory.cs`
- Modify: `src/Namotion.Interceptor.Generator/Models/PropertyMetadata.cs`
- Create: `src/Namotion.Interceptor.Generator/SubjectTypeClassifier.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`
- Modify: `src/Namotion.Interceptor.Generator/GeneratedMemberTable.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectBaseContract.cs`
- Modify: `src/Namotion.Interceptor.Dynamic/DynamicSubject.cs`
- Modify: `src/Namotion.Interceptor.Dynamic/DynamicSubjectFactory.cs`
- Test: `src/Namotion.Interceptor.Tests/ExecutorPublicationTests.cs`
- Create: `src/Namotion.Interceptor.Tests/SubjectContextAttachmentTests.cs`
- Create: `src/Namotion.Interceptor.Tests/StructuralWriteAttachmentRevisionTests.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/SubjectBaseShapeTests.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/GeneratedMemberTableTests.cs`
- Create: `src/Namotion.Interceptor.Generator.Tests/SubjectTypeClassifierTests.cs`
- Modify: `src/Namotion.Interceptor.Dynamic.Tests/DynamicSubjectExecutorPublicationTests.cs`

- [ ] Write tests proving the executor is published once under a first-access race, an unattached subject reports null, `GetContext()` throws while `TryGetContext()` returns null, and an attachment snapshot cannot be used to mutate another executor.
- [ ] Write transition tests for unattached to inherited/explicit, inherited to explicit, explicit to inherited, attached to unattached, stale compare-and-swap failure, invalid null-explicit state, and prohibited direct context swap.
- [ ] Write generator shape tests proving `IInterceptorSubject` exposes `Executor`, generated subjects use `_executor`, and context-taking constructors call `AttachToContext(context)` after ordinary constructor chaining.
- [ ] Write generator classifier tests mirroring `SubjectPropertyTypeExtensionsTests` for direct subjects, `object`, plain interfaces, scalar values, subject collections, subject dictionaries, non-subject generic collections, non-generic collections, and hybrid subject/enumerable types.
- [ ] Write generated-output tests proving scalar properties emit `SetPropertyValue` and subject-capable properties emit `SetStructuralPropertyValue`. Write equivalent Dynamic tests proving reflection metadata chooses the structural route once during proxy construction, not on every write.
- [ ] Run the focused Core, Generator, and Dynamic tests and confirm they fail before implementation.
- [ ] Add `IInterceptorExecutor Executor { get; }` to `IInterceptorSubject`. Keep `Data`, `Properties`, and `AddProperties`. Retain `Context` and `SyncRoot` only as unannotated `TODO(single-context-cutover)` transition members so downstream projects compile until Task 11; do not use them in new code.
- [ ] Change `IInterceptorExecutor` so it no longer inherits `IInterceptorSubjectContext`. Expose nullable `Context`, `IsExplicitlyAttached`, ordinary read/write/invoke methods, `SetStructuralPropertyValue`, and the public raw attachment transition seam.
- [ ] Implement `InterceptorSubjectAttachment` as an allocation-free snapshot containing public context/explicit/revision values and an internal executor identity. Implement `TryUpdateAttachment(expected, context, isExplicit, out current)` under the executor's private monitor. Enforce exact reference identity, legal state shapes, and no direct non-null context swap.
- [ ] Keep the transition revision separate from the existing committed-property revision. Increment attachment revision on every successful context or explicit-bit transition.
- [ ] Implement `AttachToContext(context)` by applying the strict state table, then delegating to the target context's zero-or-one `ILifecycleInterceptor`; when none exists, perform the explicit raw transition for the root only. Implement `DetachFromContext(context)` against the executor's current exact context and its zero-or-one lifecycle, with the same strict validation before mutation.
- [ ] Move declared-type subject-shape classification into Core because Dynamic and the executor entry path need it without referencing Tracking. Cache runtime `Type` classification as today. Add the equivalent Roslyn symbol classifier so generated scalar setters contain no runtime type test.
- [ ] Make `SetStructuralPropertyValue` capture the attachment snapshot before resolving the context's interceptor chain. Store the required snapshot in `PropertyWriteContext<TProperty>`. At the terminal, take the private executor monitor, compare the attachment revision/context, throw on staleness before the backing delegate, then perform the existing write/timestamp/property-revision work.
- [ ] Leave scalar `SetPropertyValue` free of attachment snapshot capture/comparison. It may finish using a pinned old or new interceptor snapshot during a concurrent attachment change because it cannot change topology.
- [ ] Keep zero-interceptor reads and scalar writes direct/inlinable. Move terminal synchronization from public `subject.SyncRoot` to the executor monitor.
- [ ] Implement generated and Dynamic executor publication with `Interlocked.CompareExchange`; do not allocate an executor on scalar get/set until interception or structural revision safety requires it.
- [ ] Emit temporary `Context` and `SyncRoot` forwarding members in generated, Dynamic, and manual subjects so downstream projects compile during Tasks 4 through 10. `Context` forwards to `Executor.GetContext()` and therefore throws while unattached; `SyncRoot` forwards to a transition-only public monitor accessor. Mark the declarations with `TODO(single-context-cutover)`, do not mark them obsolete, do not use them in new implementation code, and delete both plus the public monitor accessor in Task 11.
- [ ] Run `dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj`, `dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj`, and `dotnet test src/Namotion.Interceptor.Dynamic.Tests/Namotion.Interceptor.Dynamic.Tests.csproj`.
- [ ] Inspect generated code for scalar and structural models and confirm scalar setters contain neither a declared-type lookup nor an attachment-revision comparison.
- [ ] Commit with `refactor: add exact subject attachment state`.

## Task 4: Flatten context service resolution and route execution through the exact context

**Files:**

- Modify: `src/Namotion.Interceptor/IInterceptorSubjectContext.cs`
- Rewrite: `src/Namotion.Interceptor/InterceptorSubjectContext.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Delete: fallback-only helpers nested in `src/Namotion.Interceptor/InterceptorSubjectContext.cs`
- Delete: `src/Namotion.Interceptor.Tests/Context/ContextDeepGraphTests.cs`
- Delete: `src/Namotion.Interceptor.Tests/Context/ContextDelegationCycleTests.cs`
- Delete: `src/Namotion.Interceptor.Tests/Context/ContextServiceWalkOrderTests.cs`
- Delete: `src/Namotion.Interceptor.Tests/Context/ContextSubtreeServiceTests.cs`
- Delete or rewrite: `src/Namotion.Interceptor.Tests/Context/ContextConcurrencyFuzzTests.cs`
- Modify: `src/Namotion.Interceptor.Tests/Context/ContextConcurrencyTests.cs`
- Modify: `src/Namotion.Interceptor.Tests/Context/ContextFunctionCacheTests.cs`
- Modify: `src/Namotion.Interceptor.Tests/Context/ContextServiceResolutionTests.cs`

- [ ] Replace fallback/delegation tests with flat-context tests for registration order, assignability, cached interceptor-chain invalidation after late service publication, in-flight pinned snapshots, and concurrent reads plus service additions.
- [ ] Confirm the rewritten tests fail or do not compile while fallback APIs still define the old contract.
- [ ] Remove `AddFallbackContext` and `RemoveFallbackContext` from `IInterceptorSubjectContext` and `InterceptorSubjectContext` without obsolete aliases.
- [ ] Reduce context state to the directly registered immutable service array and local interceptor/function caches. Remove reverse-using-context tracking, fallback walk/cycle detection, delegation targets, cross-context invalidation, and their lock-order machinery.
- [ ] Resolve every executor read/write/invoke chain directly from the executor's current exact context snapshot; use empty cached chains while unattached.
- [ ] Preserve service ordering attributes and local cache invalidation when services are added. Late stateful services receive no subject replay.
- [ ] Run the full Core test project and the benchmark project build.
- [ ] Commit with `refactor: flatten interceptor subject contexts`.

## Task 5: Implement occurrence-aware built-in lifecycle ownership

**Files:**

- Rewrite: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Create: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleGraphState.cs`
- Create: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectEdge.cs`
- Create: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectEdgeCollection.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptorExtensions.cs`
- Modify or delete when unused: `src/Namotion.Interceptor.Tracking/Lifecycle/PropertyReferenceSet.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/ILifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs`
- Rewrite: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/RecursiveAttachTests.cs`
- Rewrite: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleEventsTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/LifecycleInterceptorTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/SingleContextOwnershipTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OccurrenceEdgeTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/CycleReachabilityTests.cs`

- [ ] Through public APIs, write failing tests for strict repeated explicit attach, inherited-to-explicit promotion, strict detach, exact-context conflict, constructor explicit roots, no-lifecycle root-only behavior, assignment inheritance, unassignment release, and an explicit child surviving parent unassignment.
- [ ] Through public APIs, write failing DAG/cycle tests for multiple parents, self-cycle, multi-node cycle, cycle orphan release, cycle retained by another explicit root, and cross-context assignment rejected before the backing field changes.
- [ ] Write occurrence tests for `[a, a, b]`, duplicate removal, duplicate reorder, dictionary keys, and general enumerable ordinals. Assert `a.GetReferenceCount() == 2`, occurrence-aware parents, every edge callback, and only first/last subject attach/detach callbacks.
- [ ] Run the focused Tracking tests and confirm the current implementation fails the new strict attach, duplicate-count, and orphan-cycle cases.
- [ ] Evolve Core `ILifecycleInterceptor` to inherit `IWriteInterceptor` and `ISingletonContextService<ILifecycleInterceptor>`, with explicit attach, explicit detach, and metadata registration methods. Keep all members public enough for a third-party lifecycle package; do not use friend access.
- [ ] Construct one `LifecycleInterceptor(context)` per context. Store one context reference, one private reentrant lock, explicit roots, owned subjects, committed structural property baselines, incoming occurrence-aware edges, and outgoing edges.
- [ ] Implement inline storage for zero/one incoming edge and allocate/pool expanded occurrence storage only when multiplicity requires it. Preserve distinct property/index/key identity while matching retained duplicates deterministically in enumeration order.
- [ ] On explicit attach, validate strict rules, discover the current direct-readable structural component with visited sets, claim executors through the public Core compare-and-swap seam, then emit callbacks/handlers in existing order. Promote an inherited same-context subject without attach callbacks.
- [ ] On structural write, reject conflicts and claim the proposed new component before `next`, call `next` exactly once, then reconcile committed edges. Publish removals before additions and reverse old-removal/forward new-addition order as on master.
- [ ] On edge or explicit-root removal, compute reachability from every explicit root over committed outgoing occurrence edges. Release all unmarked subjects, including closed cycles, once each in deterministic first-visit order.
- [ ] Update `GetParents()` and `GetReferenceCount()` to query the built-in lifecycle state through the exact context. Return empty for unattached subjects or contexts using another lifecycle implementation.
- [ ] Ensure authoritative parent edges are visible before the first attach/detach handler. Keep callback behavior otherwise identical: context handlers, subject handlers, `SubjectAttached`/`SubjectDetaching`, property handlers, and recursive descent boundaries.
- [ ] Run `dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~Lifecycle"` and then the full Tracking test project.
- [ ] Commit with `feat: own graph lifecycle in one context`.

## Task 6: Enforce lifecycle concurrency and reentrancy contracts

**Files:**

- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleGraphState.cs`
- Rewrite: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/ConcurrentWriteLifecycleTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleReentrancyTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Change/WritePipelineOrderTests.cs`

- [ ] Add synchronization-based tests, without `Task.Delay` or `Thread.Sleep`, for serialized same-context structural writes, parallel independent-context structural writes, two contexts racing to adopt one unattached subject, and an unattached/old-context structural chain racing with attach or reattach.
- [ ] Add callback tests proving scalar reads/writes are allowed, structural setters throw before backing mutation, explicit attach/detach throw, same-lifecycle `AddProperties` is reserved for Task 7, and an unsupported repeated `next` call is not guarded by Core.
- [ ] Add pipeline-order tests proving equality/veto interceptors can suppress before lifecycle, lifecycle is closest to the backing writer, and outer change/derived/transaction interceptors see lifecycle reconciliation completed after a successful terminal write.
- [ ] Run the focused tests and confirm intended failures.
- [ ] Use a thread-static or equivalent allocation-free callback phase marker associated with the active built-in lifecycle. Fail fast on forbidden structural/explicit operations before provisional claims, callbacks, or backing writes.
- [ ] Serialize all built-in structural topology changes through the lifecycle's private reentrant monitor. Never hold two lifecycle locks. Take the lifecycle lock before any executor monitor when both are required.
- [ ] At context write-chain construction only, place the singleton `ILifecycleInterceptor` closest to the terminal after ordinary ordering resolution. Do not annotate the class with a global `RunsLast` attribute because the same instance also participates in lifecycle-handler ordering.
- [ ] Ensure stale structural attachment snapshots fail at the executor terminal before mutation and provisional lifecycle state is released. Scalar writes retain the existing snapshot-pinning semantics and no attachment-revision guard.
- [ ] Run full Core and Tracking tests repeatedly enough to exercise the race tests deterministically.
- [ ] Commit with `fix: enforce lifecycle topology concurrency contracts`.

## Task 7: Make `AddProperties` atomic and topology-aware

**Files:**

- Modify: `src/Namotion.Interceptor/IInterceptorSubject.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/SubjectPropertyRegistrationContext.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/ILifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`
- Modify: `src/Namotion.Interceptor.Dynamic/DynamicSubject.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/AddPropertiesLifecycleTests.cs`
- Modify: `src/Namotion.Interceptor.Dynamic.Tests/DynamicSubjectTests.cs`

- [ ] Write failing tests for once-only input enumeration, atomic duplicate-name rejection, one getter call per qualifying structural property, atomic scalar/structural batches, provisional-claim release on conflict, same-lifecycle callback success, unattached callback success, cross-context callback rejection before enumeration, derived structural exclusion, and a publisher that is invoked zero or one time by contract.
- [ ] Document that the input metadata enumerable must be synchronous, stable, and free of topology/metadata side effects. It is materialized exactly once after callback admission; unsupported iterator reentrancy receives no replay or rollback.
- [ ] Implement `SubjectPropertyRegistrationContext` with the subject and a lazy once-materialized metadata sequence. Core owns duplicate-name validation and constructs the complete immutable lookup before calling its synchronous publication continuation.
- [ ] For an owned subject, let lifecycle inspect callback state and reject a cross-context callback before forcing enumeration. For an unattached subject, publish metadata directly without ownership work.
- [ ] Classify initial ownership only for `IsIntercepted && !IsDerived && Type.CanContainSubjects() && GetValue != null`. Invoke each qualifying getter exactly once, capture the result, validate/claim its complete prospective subgraph, publish metadata atomically, invoke property handlers in input order, then commit captured edges and baselines as ordinary assignments.
- [ ] If enumeration, duplicate validation, getter, context validation, or claiming fails, publish no metadata, Registry state, lifecycle edges, or owned state. Release provisional executor claims. Keep the exception-free publisher contract and do not attempt rollback after a violating publisher mutates and throws.
- [ ] Ensure subsequent stored dynamic structural writes route through `SetStructuralPropertyValue`; derived/computed properties never establish edges.
- [ ] Run focused Core, Dynamic, and Tracking tests, then all three projects.
- [ ] Commit with `feat: make dynamic property admission lifecycle aware`.

## Task 8: Merge parent/context handlers into lifecycle and update Registry projection

**Files:**

- Delete: `src/Namotion.Interceptor.Tracking/Lifecycle/ContextInheritanceHandler.cs`
- Delete: `src/Namotion.Interceptor.Tracking/Parent/ParentTrackingHandler.cs`
- Modify: `src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Parent/ParentsHandlerExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Delete: `src/Namotion.Interceptor.Tracking.Tests/ContextInheritanceHandlerTests.cs`
- Delete: `src/Namotion.Interceptor.Tracking.Tests/ParentTrackingHandlerTests.cs`
- Rewrite: `src/Namotion.Interceptor.Tracking.Tests/Parent/ParentAccessDuringLifecycleTests.cs`
- Modify: `src/Namotion.Interceptor.Registry/SubjectRegistry.cs`
- Modify: `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubject.cs`
- Modify: `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubjectProperty.cs`
- Modify: `src/Namotion.Interceptor.Registry/InterceptorSubjectContextExtensions.cs`
- Modify: `src/Namotion.Interceptor.Registry/SubjectRegistryExtensions.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/RegistryHandlerOrderTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/RegistryAncestorResolutionTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/DynamicPropertyLifecycleTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/DynamicPropertyWithWriteInterceptorTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/GraphBehavior/CycleTests.cs`
- Modify: `src/Namotion.Interceptor.Registry.Tests/GraphBehavior/DagTests.cs`

- [ ] Rewrite configuration tests so `WithLifecycle()` implicitly supplies parent tracking/context inheritance, `WithRegistry()` installs lifecycle first, repeated `WithRegistry().WithLifecycle()` is idempotent, and a custom lifecycle conflict fails before Registry is published.
- [ ] Rewrite ordering tests for Registry before lifecycle descent and source/hosted handlers after it. Assert `GetParents()` is already authoritative inside the first handler.
- [ ] Change the orphaned-cycle Registry snapshot from the old limitation to complete detach. Add duplicate occurrence/index/key Registry assertions.
- [ ] Add dynamic Registry tests proving the Registry projection exists before initial structural edge notifications and derived structural values do not create Registry parent edges.
- [ ] Remove `WithContextInheritance()` and `WithParents()` without obsolete aliases. Update `WithLifecycle()`, `WithFullPropertyTracking()`, and `WithRegistry()` to install the one default lifecycle through the singleton predicate.
- [ ] Make `LifecycleInterceptor` implement Tracking's `ILifecycleHandler` at the former context-inheritance descent slot. Migrate all `RunsBefore`/`RunsAfter` references from the deleted handlers to `LifecycleInterceptor`.
- [ ] Mark `SubjectRegistry` as `ISingletonContextService<ISubjectRegistry>`.
- [ ] Replace `RegisteredSubject.AddProperty`'s manual Registry insertion, explicit property attach workaround, and synthetic null-to-value write. Let `IPropertyLifecycleHandler.AttachProperty` create a missing `RegisteredSubjectProperty` before lifecycle publishes initial structural edges.
- [ ] Keep Registry parent/child snapshots as navigation projections only; no Registry state participates in lifecycle ownership or reachability.
- [ ] Run full Tracking and Registry test projects and inspect changed Verify snapshots.
- [ ] Commit with `refactor: merge ownership handlers into lifecycle`.

## Task 9: Mark first-party authorities as singleton and migrate configuration ordering

**Files:**

- Modify: `src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking/PropertyValueEqualityCheckHandler.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Recorder/ReadPropertyRecorder.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransactionInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/ITransactionWriter.cs`
- Modify: `src/Namotion.Interceptor.Registry/Abstractions/ISubjectRegistry.cs`
- Modify: `src/Namotion.Interceptor.Connectors/InterceptorSubjectContextExtensions.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Monitoring/SourceMonitor.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Monitoring/SourceMonitoringExtensions.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Transactions/SourceTransactionWriter.cs`
- Modify: `src/Namotion.Interceptor.Hosting/HostedServiceHandler.cs`
- Modify: `src/Namotion.Interceptor.Hosting/InterceptorHostingExtensions.cs`
- Modify: `src/Namotion.Interceptor.Hosting/InterceptorSubjectContextExtensions.cs`
- Modify: `src/Namotion.Interceptor.Validation/ValidationInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Validation/DataAnnotationsValidator.cs`
- Modify: `src/Namotion.Interceptor.Validation/InterceptorSubjectContextExtensions.cs`
- Test: existing configuration tests in Core, Tracking, Registry, Connectors, Hosting, and Validation

- [ ] Add tests proving each configuration extension is idempotent for its own default service, rejects a conflicting custom service for the same contract, and does not publish dependent services before a lifecycle conflict is detected.
- [ ] Mark lifecycle, `ISubjectRegistry`, `ITransactionWriter`, SourceMonitor, SubjectTransactionInterceptor, PropertyChangeInterceptor, HostedServiceHandler, PropertyValueEqualityCheckHandler, DerivedPropertyChangeHandler, ReadPropertyRecorder, ValidationInterceptor, and DataAnnotationsValidator with appropriate `ISingletonContextService<TContract>` interfaces. Use the abstraction for lifecycle, Registry, and transaction-writer authority slots; use the concrete class for one-per-default-implementation services.
- [ ] Keep ordinary ordered interceptor chains, lifecycle handlers, validators, and user services plural unless a concrete implementation owns a documented singleton authority.
- [ ] Collapse SourceMonitor's current duplicate concrete/`ILifecycleHandler` registration to one service object; rely on assignability for all roles.
- [ ] Make every dependent configuration extension establish lifecycle first, then publish its own stateful services. Late direct additions remain allowed and receive no backfill.
- [ ] Run the affected project tests and `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"` as an early cross-project compile gate.
- [ ] Commit with `refactor: declare singleton context authorities`.

## Task 10: Migrate all first-party consumers and temporary construction ownership

**Files:**

- Modify: `src/HomeBlaze/HomeBlaze.Host/Components/HomeBlazorComponentBase.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/Lifecycle/PropertyAttributeInitializer.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/RootManager.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/SubjectContextFactory.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/SubjectPathResolver.cs`
- Modify: `src/Namotion.Interceptor.AspNetCore/Extensions/SubjectRegistryJsonExtensions.cs`
- Modify: `src/Namotion.Interceptor.Connectors/ISubjectSource.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SourceOwnershipManager.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SourcePropertyExtensions.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SubjectFactoryExtensions.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateApplier.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs`
- Modify: `src/Namotion.Interceptor.ConnectorTester/Engine/Mutation/MutationEngine.cs`
- Modify: `src/Namotion.Interceptor.GraphQL/GraphQLSubscriptionSender.cs`
- Modify: `src/Namotion.Interceptor.Mcp/Tools/SearchTool.cs`
- Modify: `src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs`
- Modify: `src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServer.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectLoader.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServer.cs`
- Modify: `src/Namotion.Interceptor.Testing/ObjectExtensions.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs`
- Modify affected sample `Program.cs` files under `src/Namotion.Interceptor.*Sample*`
- Modify corresponding unit/integration tests identified by the residual search below

- [ ] Add connector update tests for a temporary child success path, assignment failure, population failure, collection/dictionary batch ownership, shared children, and cycles. Assert successful assignment leaves the child inherited/non-explicit and failures release temporary roots.
- [ ] Add OPC UA loader tests for the same explicit construction-transfer pattern around asynchronous population.
- [ ] Change long-lived roots such as HomeBlaze `RootManager` to one explicit `Root.AttachToContext(_context)`.
- [ ] For newly constructed connector/OPC children, use this exact transfer protocol:

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

- [ ] For collection/dictionary loaders, explicitly attach all newly populated temporary roots, assign the complete structural value, then detach every temporary root in `finally`. Ensure partial failure releases every temporary claim.
- [ ] Replace direct `subject.Context` usage with `GetContext()` where attachment is required and `TryGetContext()` where absence is valid. Resolve services only through the returned context.
- [ ] Replace direct executor casts from `subject.Context` with `subject.Executor`.
- [ ] Replace MQTT `RegisteredSubject.ReferenceCount <= 0` ownership checks with Registry membership or exact-context checks. Preserve the valid explicit-root case where reference count is zero.
- [ ] Change `WithSourceMonitoring()` and branch-scope consumers to use lifecycle's authoritative parents; remove `WithParents()` calls.
- [ ] Run this residual gate and classify every hit explicitly; no production hit may remain except deliberate mentions in design/plan or a type named `Context` unrelated to subjects:

```powershell
rg -n "\.Context\b|SyncRoot|AddFallbackContext|RemoveFallbackContext|WithContextInheritance|WithParents" src -g "*.cs"
```

- [ ] Run non-integration tests for Connectors, OPC UA, MQTT, WebSocket, Hosting, HomeBlaze services, and the solution.
- [ ] Commit with `refactor: migrate consumers to exact subject contexts`.

## Task 11: Remove transitional APIs and dead fallback/lifecycle code

**Files:**

- Modify: `src/Namotion.Interceptor/IInterceptorSubject.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/IInterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/InterceptorSubjectContext.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`
- Modify: `src/Namotion.Interceptor.Generator/GeneratedMemberTable.cs`
- Delete: `src/Namotion.Interceptor.Benchmark/ContextDelegationDepthBenchmark.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/DynamicSubjectBenchmark.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/SubjectSourceBenchmark.cs`
- Delete any now-unused fallback state, old parent state, `PropertyReferenceSet`, and `TODO(single-context-cutover)` forwarding members
- Modify generated/manual subject-shape tests across Core, Generator, Dynamic, Connectors, and SourceWait test fixtures

- [ ] Delete every temporary forwarding member introduced in Task 3. Do not leave obsolete APIs.
- [ ] Confirm final `IInterceptorSubject` contains `Executor`, `Data`, `Properties`, and `AddProperties`, but no `Context` or `SyncRoot`.
- [ ] Confirm final executor is not a context and contains no subject-local service registration, fallback service lookup, Registry, parent, or reachability logic.
- [ ] Replace old fallback-focused benchmarks. Keep `LifecycleOwnershipBenchmark` source-identical to its Task 1 base commit.
- [ ] Update all manual test subjects to publish one executor with `Interlocked.CompareExchange` and route scalar/structural writes correctly. Do not add friend assembly access for convenience.
- [ ] Run the residual API search from Task 10 and `rg -n "TODO\(single-context-cutover\)" src` to prove no transition shim remains. Inspect `git diff LIFECYCLE_BENCHMARK_BASE -- "src/**/*.csproj"` and confirm this work added no `InternalsVisibleTo`; remove Core's existing Tracking friend entry only if the resulting production code no longer consumes any Core internal.
- [ ] Run Core, Generator, Dynamic, Tracking, Registry, and Connectors tests.
- [ ] Commit with `refactor: remove subject-local context compatibility`.

## Task 12: Update public API snapshots and documentation

**Files:**

- Modify: `docs/interceptor.md`
- Modify: `docs/tracking.md`
- Modify: `docs/subject-guidelines.md`
- Modify: `docs/design/tracking-lifecycle.md`
- Modify: `docs/aspnetcore.md`
- Modify: `docs/connectors-websocket.md`
- Modify any sample/readme pages found by `rg` for removed APIs
- Modify public API snapshots under `src/*Tests/VerifyChecksTests.PublicApi.verified.txt` where generated `.received.txt` files prove intentional changes
- Modify Verify graph snapshots affected by cycle cleanup, duplicate occurrences, or context display

- [ ] Update Core docs for nullable exact contexts, strict explicit roots, constructor attachment, `GetContext`/`TryGetContext`, no local services/fallbacks, raw lifecycle extensibility, and singleton service contracts.
- [ ] Update Tracking docs for automatic inheritance/parents under `WithLifecycle`, arbitrary cycles/DAGs, explicit-root reachability, occurrence-aware reference counts, callback order, callback reentrancy restrictions, zero-or-one continuations, and exception-free callback contracts.
- [ ] Update Registry docs to call it an optional reflection/navigation projection and document dynamic property admission before initial edges.
- [ ] Update connector docs with temporary explicit construction ownership and the fact that Registry reference count is not ownership.
- [ ] Keep Markdown paragraphs unwrapped and use no em dashes.
- [ ] Run every affected `VerifyChecksTests.PublicApi` test, copy only intentional `.received.txt` outputs over `.verified.txt`, delete received artifacts, and inspect that removed APIs are absent rather than obsolete.
- [ ] Run `rg -n "AddFallbackContext|RemoveFallbackContext|WithContextInheritance|WithParents|\.Context\b|SyncRoot" docs src -g "*.md" -g "*.cs"` and resolve every stale public example.
- [ ] Run `dotnet build src/Namotion.Interceptor.slnx` and `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`.
- [ ] Commit with `docs: document exact context lifecycle ownership`.

## Task 13: Perform performance and connector verification, then update the draft PR

**Files:**

- Read before action: `docs/benchmarking.md`
- Read before action: `docs/connector-tester.md`
- Modify: draft PR body/checklist only after evidence exists
- Add no benchmark result files unless the repository convention explicitly requires them

- [ ] Before running hours-long verification, agree the final scope with the maintainer. Proposed scope is the default non-integration solution suite plus targeted OPC UA, MQTT, and WebSocket integration tests; Connector Tester chaos for each affected connector at 100 cycles; one 15-minute load cycle for each connector; and memory mode only if allocation tests/benchmarks indicate risk.
- [ ] In an external worktree, pin the CPU, keep the machine quiet, and compare the final branch against the exact `LIFECYCLE_BENCHMARK_BASE` hash using `*LifecycleOwnershipBenchmark*`, `*RegistryBenchmark*`, and `*ServiceOrderResolverBenchmark.LinearChain*` with `-LaunchCount 3`. Do not use `-Short` for a decision.
- [ ] Repeat any small timing delta before interpreting it. Treat allocations as primary. Quote both resolved commit hashes and the unchanged noise-reference movement in the PR.
- [ ] Diff generated scalar/structural output and JIT disassembly for `SetPropertyValue`/`SetStructuralPropertyValue` if benchmark noise cannot resolve a small scalar-path change.
- [ ] Run the agreed targeted integration tests. If Connector Tester was approved, record exact commands, connector modes, cycle counts/durations, and findings; do not claim it ran if it was deferred.
- [ ] Run the final clean-tree verification: `dotnet build src/Namotion.Interceptor.slnx`, `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`, agreed targeted integration tests, `git diff --check`, `git status --short`, and residual removed-API searches.
- [ ] Use the `superpowers:requesting-code-review` skill for a design/implementation review and resolve findings through `superpowers:receiving-code-review` before claiming completion.
- [ ] Update the existing draft PR body with final diff composition, tests, benchmark evidence, connector verification/deferment, breaking migration notes, and remaining risk. Keep it draft until the user decides it is ready.
- [ ] Commit any review/documentation corrections, push the branch, and report the final commit hash and PR URL.

## Final acceptance checklist

- [ ] A subject is unattached or owned by exactly one exact context, with at most one explicit root anchor.
- [ ] Arbitrary cycles, DAGs, multiple parents, and duplicate collection/dictionary occurrences reconcile correctly.
- [ ] Subject attach/detach callbacks remain first-entry/last-exit events; edge handlers run for every real occurrence.
- [ ] Cross-context and forbidden callback structural mutations fail before backing mutation.
- [ ] `AddProperties` is atomic, once-materialized, derived-safe, and Registry-ready before initial edges.
- [ ] Core exposes a complete public third-party lifecycle seam without new friend-assembly access.
- [ ] Executors are not contexts; subject-local services, fallbacks, `Context`, `SyncRoot`, old handlers, and old configuration methods are absent.
- [ ] Singleton service contracts fail fast, including duplicate same-instance registrations, while late registration stays allowed without backfill.
- [ ] Scalar generated writes retain their direct route and benchmarks show no material regression beyond measured noise.
- [ ] All required build, unit, public API, agreed integration, benchmark, and Connector Tester evidence is attached to the draft PR.
