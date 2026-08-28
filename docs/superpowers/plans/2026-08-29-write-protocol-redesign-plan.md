# Write Protocol Redesign: Campaign Plan

Process plan for the redesign decided in `2026-08-29-write-protocol-redesign-handover.md`. That document holds the evidence, the findings and the decision to keep the branch. This one holds the sequence, the gates and the budgets.

This is not an implementation plan. The implementation plan gets written in Phase 5, once a design exists to write steps against. Writing bite-sized TDD steps now would be placeholders, which is the one thing an implementation plan may not contain.

## The bar

Ordered. When these conflict, the higher one wins.

1. **Guaranteed correct.** No race windows. Impossibility argued from code, not from test runs.
2. **Master parity.** No new constraint, no behavior change and no API change relative to master unless it is *absolutely necessary* for correctness or for a materially simpler design. The default answer is revert, not justify. Features master had are given back where that costs no net code.
3. **Simple for consumers.** A connector author, a HomeBlaze author or an external consumer must not need to know library internals. Measured, not asserted: see the concept ledger.
4. **No more code than the contract requires.** Production diff at or below what the branch carries today, and every mechanism names the test that forces it.
5. **Performance same or better** than master. Not "inside the noise floor as an excuse": regressions need a mechanism and a decision, improvements are welcome.

### What changed from the branch's current assumptions

Two standing assumptions are now void and must be re-opened, not inherited:

- **The approved delta list is no longer approved.** `2026-08-28-write-protocol-lock-scope-design.md:255-268` lists API and behavior changes signed off on 2026-08-28. Under bar item 2 every row returns to unapproved and must re-earn its place against "absolutely necessary". This includes the removal of `ILifecycleInterceptor.EnterStructuralWriteGate`/`ExitStructuralWriteGate`, the removal of `CanContainSubjects<TProperty>`, the narrowed-write behavior changes and the registration-time throw on ordering attributes.
- **Behavior deltas the whole rewrite carries versus master have never been enumerated in one place.** They are scattered across four design docs and the PR body. Phase 0 builds that ledger, because bar item 2 cannot be checked against a list that does not exist.

## Budgets

Measured at `9aa506f3` versus `master`. Phase 0 re-derives these exactly and freezes them; the handover's numbers were taken at a different commit and disagree slightly.

| Budget | Today (net vs master) | Target |
|---|---|---|
| Production, all (100 files) | **+2731** | At or below. |
| Write-protocol core (17 files) | **+2111** | Near +800. This is the part being replaced by one rule. |
| Production, everything else (83 files) | **+620** | Unchanged. Uncriticised, and reverting parity rows may reduce it. |
| Tests (148 files) | +4032 | Grows. Tests are the asset, not the cost. |
| Public API snapshots (4 files) | **+19** | Toward zero. Every surviving line needs a written argument. |
| Consumer concepts | To be counted in Phase 0 | At or below master's count. |
| Connector special handling | 3 sites | **Zero.** Enforced by deletion in Phase 0. |

The decomposition is the finding, and it reframes the whole budget. The write-protocol core is +2111 of the +2731; the other 83 production files come to +620 between them. **The part under redesign is the diff.** A core landing near +800 puts production near +1400, about half of today. That is the target; +2731 is merely the ceiling.

Exact commands and the frozen measurement live beside this plan in the campaign scratch, so any later claim of "no growth" can be re-derived rather than believed.

### The concept ledger

Bar item 3 needs a number. Count every concept a consumer must hold in their head to use the lifecycle correctly, on master and on the branch: anchor kinds, attachment revisions, provisional roots, gate entry, ownership claims, occurrence indices, and so on. Each row records who must know it (external consumer / connector author / library-internal) and what breaks if they do not. A concept that only library code needs is free. A concept a connector author must know is a cost that must be paid for.

`ReleaseUnconsumedRoot` at `SubjectUpdateApplier.cs:190`, `SubjectItemsUpdateApplier.cs:244` and `OpcUaSubjectLoader.cs:316` is the current worked example of a concept leaking outward: it forces a connector author to know provisional anchors exist, that an unconsumed one leaks, and how to release one.

## Phases

Each phase ends in a gate. A gate that does not pass sends the phase back, not forward.

---

### Phase 0: Ground truth

Nothing is designed until the evidence base is committed. Three workstreams, parallelizable.

**0a. Make the deadlock repros able to fail.** `GateChainDeadlockRepro.cs:31` and `MonitorAbbaRepro.cs:28` wait on the opposite thread's rendezvous and never assert the result, so a rendezvous timeout serializes the writes and the test passes green having proven nothing. Assert the rendezvous, move under `Lifecycle/`, rename to the `When<Condition>_Then<Expected>` convention. Verify each still fails on the pre-fix commit and passes on the current one.

**0b. Reproduce every finding as a failing test.** Eleven inputs: the ten external review findings plus finding 1 from the internal defect review (the narrowed write that commits with no gate and no monitor). For each: a test that fails on `9aa506f3`, or a written record of why it does not reproduce, with the mechanism that makes it unreachable. A finding that does not reproduce is a finding that does not get a mechanism in the design.

**0c. Delete the connector special handling, and keep it deleted.** Remove `ReleaseUnconsumedRoot` from all three sites and `IsAnchoredRoot` from the public surface if nothing else needs it. Whatever now fails becomes a repro test at the library level, where the defect actually lives. This turns bar item 3 from a wish into a forcing function: any candidate design that needs this code back has failed.

**0d. Build the master-parity ledger.** Every behavior, contract and public API difference between `master` and `9aa506f3`, one row each: what changed, where (`file:line`), why it changed, and the disposition. Dispositions are `revert`, `necessary-for-correctness`, `necessary-for-simplicity`, or `give-back-from-master`. Sources to sweep: the four design docs, the PR body, both `PublicApi.verified.txt` snapshots, and the diff itself. This is a research fan-out; run it as a subagent sweep with the doc-derived rows cross-checked against the diff rather than trusted.

**0e. Freeze the budgets.** Re-derive the table above exactly at the Phase 0 head commit and record the command that produced each number.

**Gate 0.** Every finding has a failing test or a written non-reproduction. The connector sites are deleted and the suite's failures are understood. The ledger covers every row of both API snapshots. Budgets frozen. A reviewer agent that did not write the repros confirms each one fails for the stated reason and not incidentally.

---

### Phase 1: Invariants

Write the normative spec, answering the external reviewer's five questions:

1. The structural write's linearization point.
2. Which code may execute while topology state is locked.
3. How incoming ownership is reserved across replacement.
4. How stale or reentrant scans are detected.
5. What state remains after every rejected or failed operation.

Rules for this document: each invariant names the Phase 0 test that would catch its violation; each invariant is stated so a reader can check code against it without reading six types; nothing about implementation.

**Gate 1.** An adversarial reviewer, given only the invariants and the repro suite, cannot construct an interleaving that satisfies every invariant and still breaks a test. Loop until a round finds nothing.

---

### Phase 2: Variant spikes

The handover's working hypothesis is one candidate, not the answer:

> Capture outside, validate inside, commit atomically. The locked section contains no user code and cannot fail.

Spike it against alternatives before committing. Each spike is one agent in its own worktree, throwaway, judged on identical criteria. Candidates to include:

- **A. Generalized admission.** Extend `PropertyAdmission`'s capture/claim/publish shape to the write path. The one lifecycle area with no reported defect.
- **B. Assignment-attaches.** No provisional anchor at all; a subject becomes owned when a write commits it. If this holds, Phase 0c's deletion is permanent by construction and the concept ledger loses a row. Requires checking first whether the OPC UA source path's early attach is load-bearing.
- **C. Narrowed lock scope.** The spike already on `spike/write-protocol-lock-scope`, retained as the measured baseline to beat, not as a candidate. It is known to have the finding-1 hole.
- **D.** Whatever Phase 1 suggests that this list does not anticipate.

Judged on, in order: repro suite green; can it be stated in one rule; net production lines; consumer concepts added; measured performance on the gate-sensitive benchmark classes.

**Gate 2.** One variant wins on stated criteria, with the losing variants' failure modes written down so they are not re-proposed later.

---

### Phase 3: Design and simplification loop

Derive the minimal protocol from the winning spike. Three agent roles, looping:

- **Designer** writes the protocol.
- **Adversary** attacks it. Charter: find an interleaving, a reentrancy, a user-code callout or a failure path that breaks an invariant. Cite `file:line`. Verdict SOUND or NOT SOUND.
- **Reducer** deletes. Charter: for every mechanism, name the failing test that forces it. No test, no mechanism, delete it and prove the suite stays green. Also: which master feature can be given back for free, and which ledger row can flip to `revert`.

The loop runs until an adversary round finds nothing **and** a reducer round removes nothing. Both, not either.

**Gate 3.** Clean adversary round, clean reducer round, budgets met on paper, ledger rows all dispositioned with the `revert` default applied wherever the "absolutely necessary" test does not clearly pass.

---

### Phase 4: External review

Send the design to the external reviewer before implementing, with the five questions answered point by point, the ledger, and the budget table. Their earlier verdict was that this is a design problem, so the design is what goes back to them.

**Gate 4.** Reviewer satisfied, or their objections folded back into Phase 3.

---

### Phase 5: Implementation

Now write the implementation plan proper, in `writing-plans` form, and execute it subagent-driven with TDD. The Phase 0 repro suite is the acceptance test, and it was written before the design, so it cannot have been written to fit it.

**Gate 5.** Full suite green including integration. Budgets met in fact, not on paper. Independent reviewers, none of whom wrote the code, confirm merge-readiness. Review gates measurement: no benchmarks start before this passes.

---

### Phase 6: Measurement

- Benchmarks against `04fab84a` (master plus benchmark scaffold), CPU pinned to 3.6GHz no-turbo, per `docs/benchmarking.md`. Run each A/B twice; a single run decides nothing.
- Where a delta is small and the mechanism is unclear, settle it by JIT disassembly rather than by more runs.
- Connector Tester arm, with a master arm alongside it, because the heap trend rises on master too and a one-arm trend is never a finding.

**Gate 6.** Performance at or above master. Any regression has a named mechanism and an explicit decision.

---

## Scope: the two derived-property follow-ups

Issues #497 and #496 were deferred out of PR #494 on the reasoning that the pull request was otherwise finished. It is not finished any more, and both issues turn on mechanisms this campaign is redesigning, so both are re-examined here. They fold in differently, and the campaign's own admission rule decides which way.

### #497 folds in as a repro, not as extra work

"Derived recalculation can convict a subject stored by a normalizing setter before the reconcile attaches it." `LifecycleInterceptor.WriteProperty` claims `context.NewValue`, `next(ref context)` stores whatever the terminal decided, and `Reconcile` attaches afterwards. When a normalizing setter or a dynamic subject's authoritative getter reread stores something other than the proposed value, that subject was never claimed, and between the store and the reconcile the backing field holds a subject attached to nothing. A derived recalculation on another thread reads it and convicts.

This is the campaign's root cause verbatim: user code runs while topology state is partly mutated. It is a **master defect**, not a branch regression, in a file the branch already modifies, which puts it under the standing rule to fix pre-existing defects in code being touched.

It is also the sharpest **design discriminator** available. The candidate rule, capture outside and commit atomically, means the stored value is captured and then claimed, so no store-to-reconcile window exists to observe. A candidate design that does not close #497 has not eliminated the window it claims to eliminate. The issue itself asks for exactly this treatment: "designed against the ownership model rather than bolted on."

**Disposition: Phase 0b repro, Phase 2 judging criterion. No mechanism is added for it. The protocol must make it unreachable.**

### #496 folds in as a decision, not an implementation

"Replace the derived-orphan retry bound with topology gate quiescence." Three of its premises are things this campaign changes:

- Its mechanism is defined over `LifecycleInterceptor._gate` acquired in exactly five places, one of which is `EnterStructuralWriteGate`, a member the redesign may remove or reshape. Designing quiescence against a gate that is being replaced is designing against a moving target.
- Its open question 2, whether `ApplyRootAnchor` bypassing the gate makes the signal incomplete, is a restatement of Phase 1 questions 1 and 2.
- Most importantly, it states that `MaxStabilizationIterations = 100` "is absorbing a real defect (filed separately)". That defect is #497. **If the campaign kills #497 structurally, the bound stops absorbing anything and #496's problem changes shape.** The bound may then be removable at zero net code, which is a give-back rather than an addition.

But its stated problem, that a genuine orphan takes 100 getter evaluations to report and the constant reads as arbitrary, has **no failing test**. It is an ergonomics complaint, and two principled attempts to fix it already crashed correct code. Under this campaign's own rule, no test, no mechanism, a new quiescence mechanism with thread-local depth accounting is inadmissible here.

**Disposition: the campaign must state what happens to the bound under the new protocol, and record it. If the bound becomes removable for free once #497 is dead, it lands as a simplification. If it needs new machinery, it stays a separate pull request with its own design review and measurement, and #496 is updated with what the redesign settled so the analysis is not re-derived a third time.**

### The rule this establishes

A deferred issue folds in when the redesign changes its premises or when its defect shares the root cause. It stays out when it needs new machinery for a problem with no failing test. Apply the same test to anything else that surfaces.

### Harvest from the deferred derived-orphan plan

A prior agent pair produced a spec and a seven-task implementation plan for #496 and #497 (`2026-08-28-derived-orphan-gate-quiescence`, held outside this repository). It is not executed as written, because two of its load-bearing assumptions are things this campaign changes. But it contains work that must not be re-derived:

**Reuse directly.**

- `SubstitutingDevice`, a hand-written `IInterceptorSubject` whose terminal substitutes a different subject for the proposed value. This is the #497 harness, ready-made. Phase 0b uses it rather than building another.
- `ReorderingDevice`, whose terminal drops one proposed subject and stores the rest reordered in a new list instance. This pins the **legality** side, and it is the more important of the two: a normalizing setter that stores a reordered subset must stay legal under any new protocol. It is a master-parity guard, not a new requirement, so it belongs in Phase 0 regardless of which design wins.
- `GatedOrphanDerivedSubject` and `ParkingWriteInterceptor`, an event-based two-thread harness that parks a writer inside a structural write with the gate held. Useful for any protocol, since every candidate needs to be probed while a topology operation is in flight.

**Adopt as a free simplification.** Its Task 3 observes that `MaxStabilizationIterations = 100` is shared by three unrelated loops, which is most of why the constant reads as arbitrary. Splitting it into `MaxRecalculationIterations` and `MaxDependencyStabilizationIterations` changes no behavior, costs no net lines, and removes a false implication. Take it.

**Treat as a variant to beat, not a fix to apply.** Its Task 1 adds `ValidateStoredComponent`, a second occurrence scan after the terminal returns, rejecting any stored subject not already carrying this context. That is a bolt-on: it detects the store-to-reconcile window rather than removing it, it adds a scan to the write path, and it converts a previously-working shape into a throw, which is a behavior change requiring justification under bar item 2. The campaign's own candidate rule should make it unnecessary, because a protocol that captures what was actually stored and then claims it has no window to detect. **If a candidate design still needs `ValidateStoredComponent`, that candidate has not solved #497, it has instrumented it.** Its three tests stay either way; only the mechanism is in question.

**Blocked by this campaign.** Its Task 4 enters and exits `ILifecycleInterceptor.EnterStructuralWriteGate`/`ExitStructuralWriteGate` from the derived handler. Those are exactly the members the lock-scope delta list removes, and the redesign may remove or reshape the gate entirely. This is the concrete proof that #496 cannot be implemented before the protocol settles.

**One inherited claim to re-verify rather than trust.** The plan asserts the gate is "the single choke point for every structural write, attach, detach and property admission in the context", while issue #496's own open question 2 says `InterceptorSubjectExtensions.ApplyRootAnchor` does not take the gate. Both cannot be true. Phase 1 must settle it, since it is the same question as "which code may execute while topology state is locked."

## Orchestration discipline

**Every phase's work is done by subagents. The orchestrator coordinates and decides, and does not do the work.** Two reasons, both binding:

- **Bias.** An agent that wrote a design cannot review it, and an agent that wrote a repro cannot confirm it fails for the right reason. Independence is the only thing that makes a green gate mean anything, and this campaign exists because two reviews of a design its own author had internalized both returned SOUND on something that had a hole.
- **Context.** The orchestrator's context is a shared resource spent on sequencing, gate decisions and the budgets. Reading diffs, sweeping docs and drafting protocol text into it destroys the ability to hold the campaign. Subagents return conclusions; the orchestrator does not read what they read.

Concretely: repros, the ledger sweep, spikes, design drafts, adversary rounds, reducer rounds, implementation and review are all delegated. The orchestrator reads reports, checks claims that are load-bearing, decides gates, and holds the budgets and the ledger. When a subagent's report contradicts another's, the orchestrator resolves it by dispatching a third rather than by adjudicating from memory.

Fresh agent per unit of work. No agent inherits the orchestrator's history; each gets exactly the context its charter needs, constructed for it.

## Agent roster

| Role | When | Charter |
|---|---|---|
| Repro | 0a, 0b, 0c | Write a test that fails for the stated reason. Report non-reproductions honestly. |
| Ledger | 0d | Enumerate master-vs-branch deltas from the diff and snapshots. Doc claims are leads, not evidence. |
| Adversary | 1, 3, 5 | Break it. Cite `file:line`. Never wrote the thing being reviewed. |
| Spiker | 2 | One variant, own worktree, identical judging criteria, throwaway. |
| Designer | 3 | Write the protocol. |
| Reducer | 3 | Delete. Every mechanism names its forcing test or dies. |
| Implementer | 5 | TDD against the frozen repro suite. |

Standing rules: a reviewer never reviews their own work; every load-bearing claim carries a `file:line`; a subagent reporting a mechanism is checked before it is designed on.

## Exit criteria

- Every Phase 0 repro green.
- An adversary round and a reducer round that both find nothing.
- Production net lines below the frozen baseline.
- Public API delta versus master empty, minus rows that passed the "absolutely necessary" test with a written argument.
- Zero connector special handling.
- Benchmarks at or above master, twice.
- External reviewer satisfied.
