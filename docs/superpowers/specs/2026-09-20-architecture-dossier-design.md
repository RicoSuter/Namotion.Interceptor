# Architecture Dossiers: a method for simplifying this codebase

Date: 2026-09-20
Status: approved for pilot
Branch: `docs/architecture-dossier`

## Problem

The codebase has accumulated complexity from bug fixes rather than from design. Symptoms, measured on `origin/master` at `b36ec531f`:

- 66k production lines across roughly 45 projects.
- Single files carrying disproportionate weight: `InterceptorSubjectContext.cs` at 1088 lines, `SubjectTransaction.cs` at 829, `SubjectSourceBase.cs` at 647, `LifecycleInterceptor.cs` at 551, `SourceMonitor.cs` at 493, `SourceTransactionWriter.cs` at 490.
- Roughly 180 synchronization primitives (`lock`, `Interlocked`, `SemaphoreSlim`) in the shipping libraries alone.
- 63 open pull requests and 137 open issues, including five pull requests over 10k lines, two of which (#494 at +19.8k and #501 at +28.5k) are competing rewrites of the same subsystem.

Two structural observations drive the method.

**Issue clusters look like one missing concept each, not N independent bugs.** Issues #402, #403, #404, #406, #410 and #411 are all "a service or fallback edge in the context misbehaves under concurrency". Issues #338, #340, #342 and #347 are one transaction consistency epic. This shape has been confirmed before in connectors, where "the connection was replaced" turned out to have six consumers implemented five different ways.

**The backlog is itself a complexity source.** Every design decision is currently made against a moving target carrying several speculative large rewrites of the same subsystem.

## Goals

1. **Stop the edge-case bug class from regenerating**, by making the implicit concepts explicit and writing the contracts down in one place per area.
2. **Reduce the amount of code to maintain**, by identifying mechanisms that exist only for use cases that are not actually required, and removing them.

Non-goals for this effort: performance work, a public API freeze or 1.0 declaration, and any rewrite. Simplification lands as small reviewable changes, never as a big-bang replacement.

## Constraint on pruning

This is a public library, so removals cannot be decided statically from usage. Many capabilities may nevertheless be YAGNI. Therefore:

**The dossier recommends. The maintainer decides.** Every candidate carries a recommendation with its reasoning, and every ruling is recorded in the document with a date. Anything not ruled stays explicitly undecided rather than being treated as supported by default.

## Approach

One **dossier** per area, produced bottom up, each ending in a ranked list of candidates the maintainer rules on. Execution flows are a section inside each dossier rather than a separate cross-cutting effort, which keeps the work bounded: a cross-area flow map has no natural unit of completion because every flow touches every library.

Rejected alternatives:

- **Flow-first across all areas.** Better at finding cross-cutting duplication, but unbounded and leaves no durable per-area contract.
- **Invariant tests instead of prose.** Cannot go stale, but very expensive, and it records what the code does rather than why it exists, which does nothing for the pruning goal. Demoted to a column: each invariant is marked Covered or Unverified, and the Unverified list becomes later work.

## The dossier

Location: `docs/design/<area>.md`, alongside the four design documents already there. `docs/design/tracking-lifecycle.md` is already about 80% of this shape (Data Structures, Concurrency Model, Lock Ordering, Invariants, race scenarios), so the template generalizes a shape that already works in this repository. The two sections it lacks are exactly the ones that make a dossier a decision document rather than a description.

Eight fixed sections, of which the first seven are the analysis and the eighth is the backlog outcome:

1. **Supported use cases.** What the area promises, in the caller's words, numbered, each marked Supported, Best effort, Unsupported, or Undecided. This is the list the maintainer rules on.
2. **Contracts and invariants.** Testable assertions, each with `file:line` evidence and a Covered or Unverified marker.
3. **Concept census.** Each noun the area implements, where it lives, and every other place the same idea is reimplemented. This section produces the deletions.
4. **Flows.** The execution paths the area owns, as mermaid sequences, annotated with which mechanism engages at each step.
5. **Threading and shared state.** Every piece of mutable shared state, what protects it, its ordering guarantee, and every ambient channel.
6. **Gaps and limitations.** Known broken, unspecified, best effort. Each linked to its open issue.
7. **Candidates.** Ranked, each tagged `duplicate-concept`, `unreachable`, `expensive-use-case`, or `accidental-complexity`, with a size estimate, a recommendation, and the specific decision it needs.
8. **Backlog disposition.** Every open issue and pull request in the area, and what happened to it.

Above all eight sits a **Summary** block: one screen answering "what does this tell me", written last and derived only from the sections below it.

Presentation is part of the method, not decoration. Default to a table, a list or a diagram; use prose only for a why that cannot be tabulated. Diagrams are additive and never substitutive, because a diagram cannot carry a `file:line` and the verification pass works on citations.

For core, section 2 is largely harvesting rather than discovery. The invariants are already written down and merely scattered: `InterceptorSubjectContext.cs:18` states rule R1 and `:22` states the lock order, both as prose, and `PropertyWriteState.cs` explains why `Confirmed` advances the revision while `FromSource` does not, and instructs the reader not to "fix" the asymmetry in either direction. Nobody can see those as a set today.

The full section-by-section template, with authoring rules, is in `2026-09-20-architecture-dossier-template.md` next to this file.

## Process, per dossier

1. **Scope.** Name the projects and namespaces the area covers and its boundary with its neighbours.
2. **Harvest.** Collect existing inline invariants, XML documentation, existing design documents, and the area's open issues and pull requests.
3. **Churn pass.** Rank the area's files by change frequency crossed with size, to direct attention where the code actually moves.
4. **Census and flows.** Write sections 3, 4 and 5 from the code. Every load-bearing claim carries `file:line`.
5. **Independent verification.** A reader who did not write the dossier checks every claim against the code and confirms or refutes it. No fixing, only verdicts. Failed claims are corrected or dropped before the maintainer sees the document. This step is mandatory: confident derivation that was never checked has been the recurring failure mode in this repository.
6. **Candidate extraction.** Write sections 1, 6 and 7. Each candidate sized, tagged, and given a recommendation.
7. **Ruling session.** The maintainer rules keep, drop or defer on each use case and each candidate. Rulings are recorded in the document with a date.
8. **Backlog disposition.** Every open issue and pull request in the area is classified: becomes a gap entry, closes as superseded, closes as speculative, or stays open with a link to the contract it now depends on.
9. **Work items.** Each drop or simplify becomes its own issue sized to roughly 150 to 250 production lines, the reviewable-PR bar. Never one mega refactor.

## Pilot: core context

The first dossier covers `Namotion.Interceptor` (the core library, 3.5k lines across 36 files), producing `docs/design/core-context.md`.

Core is the right pilot for three reasons: it is the smallest area, everything depends on it, and it is the only area where two large pull requests are stalled on a decision this document is what makes answerable.

A shallow pass already surfaces four candidates, which is the evidence that the template produces output:

- **Multi-context topology** (`expensive-use-case`). Fallback contexts, delegation targets, a cyclic delegation marker, upward invalidation walks, a lazily allocated used-by set per context, and five thread-static traversal buffers. This is most of the 1088 line file. Both #494 and #501 are attempts to collapse it to one context. Decisions needed: does anything require more than one context per graph, and does anything require cyclic fallback graphs.
- **Ambient thread-static channels** (`duplicate-concept`). Seven slots across three purposes: `PendingOrigin._frame`, `SubjectChangeContext._current`, and the five traversal buffers. Issue #369 already proposes making origin explicit instead.
- **Origin lifecycle spread over four types** (`duplicate-concept`). `ChangeOrigin`, `AttemptedOrigin`, `PendingOrigin` and the revision fields on `PropertyWriteState` implement one three-stage state machine (pending, attempted, finalized) across four types plus a thread static.
- **Service ordering** (`accidental-complexity`). `ServiceOrderResolver` at 261 lines plus four attributes (`RunsBefore`, `RunsAfter`, `RunsFirst`, `RunsLast`), where registration order plus an explicit index may cover every real case.

## Sequence after the pilot

Extract the skill first, then let churn inform the order rather than dependency ordering alone.

Churn over the last 12 months, production files only:

| commits | lines | file |
|---|---|---|
| 39 | 742 | `OpcUa/Client/OpcUaSubjectClientSource.cs` |
| 27 | 494 | `Tracking/Change/DerivedPropertyChangeHandler.cs` |
| 24 | 533 | `OpcUa/Client/OpcUaClientConfiguration.cs` |
| 19 | 551 | `Tracking/Lifecycle/LifecycleInterceptor.cs` |
| 16 | 814 | `OpcUa/Client/Connection/SessionManager.cs` |
| 13 | 1088 | `InterceptorSubjectContext.cs` |

Churn and size point at different targets, and both are legitimate. Core context is where the **deletion** is: 1088 rarely changing lines that two large pull requests are trying to collapse. The OPC UA client and derived property handling are where the ongoing **pain** is. The pilot addresses the first; the order afterwards should favour the second, which likely puts derived properties and lifecycle ahead of connectors.

Planned areas: core context (pilot), tracking, transactions (its own dossier, since its contract is what connectors depend on), connectors, OPC UA measured against the connector contract. Registry, hosting and generator fold into their neighbours or follow later.

## Skill extraction

After the pilot, promote the template and the process that actually worked into `.claude/skills/architecture-dossier/`. Not before: writing the skill first would freeze a method that has produced zero dossiers, and sections 3 and 4 are the ones most likely to change shape on contact with real code.

Two things belong in the skill specifically because they are what quietly get skipped when context runs short, and they are what make a dossier trustworthy: the `file:line` requirement on every load-bearing claim, and the independent verification pass by a reader who did not write the document.

## Prior art

The method is assembled from established practice rather than invented.

- **arc42** supplies the overall document shape. Sections 4 and 7 correspond to its runtime view and its risks-and-technical-debt section.
- **Linux kernel `Documentation/locking/`** is the convention section 5 follows: explicit lock ordering rules written down as a set.
- **Go's compatibility promise** is what section 1 does: state precisely what is guaranteed, so that everything not on the list is free to change.
- **PEP 594, "Removing dead batteries from the standard library"**, is the pruning precedent: enumerate every candidate, state the rationale, have a named authority rule, schedule the removal.
- **Architecture decision records** (Nygard) are the form the step 7 rulings take, so that "we decided not to support that" survives the person who decided it.

What is not standard is doing this retroactively with deletion as the goal. Most projects write documents of this shape to onboard people, and those go stale within two releases. The defenses here are that the document records rulings, which stay true after the code moves, and that invariants carry a Covered or Unverified marker tying them to tests.

## Deliverables

On branch `docs/architecture-dossier`:

1. This design document and the template, in `docs/superpowers/specs/` (untracked directory on master, so staged by explicit path, never by staging `docs/`).
2. `docs/design/core-context.md`, the pilot dossier, committed once independent verification has passed and before the ruling session.
3. The maintainer's rulings, committed as an edit to that dossier.
4. Issues filed for each approved removal, sized to the reviewable-PR bar.
5. `.claude/skills/architecture-dossier/` extracted from what actually worked.

The dossier ships as its own pull request. The removals it authorizes are separate pull requests, one per candidate, so a reviewer never has to hold an analysis and a refactor in their head at once.

## Success criteria

The pilot succeeds if, at the end of it:

- Every contract and invariant claim in `docs/design/core-context.md` survived independent verification against the code.
- The maintainer ruled on every entry in sections 1 and 7, or explicitly deferred it.
- At least one mechanism is approved for removal, with an issue sized to the reviewable-PR bar.
- Every open issue and pull request labelled `area: core` has been dispositioned.
- The template needed no structural change, or the changes it needed are known before the skill is extracted.

The programme as a whole succeeds if the edge-case bug clusters stop reappearing in areas that have a dossier, and if production line count falls without a loss of a capability the maintainer ruled Supported.

## Risks

- **The documentation becomes an end in itself.** Mitigated by the requirement that every dossier ends in a ruled candidate list, and by doing one area at a time with a decision gate between.
- **Documenting a subsystem that is mid-rewrite.** Core is exactly this, with #494 and #501 in flight. Treated as an argument for rather than against: those pull requests are unresolved because the contract is not written down anywhere.
- **The dossier asserts something false and a simplification is designed on it.** Mitigated by the `file:line` rule and the mandatory independent verification pass.
- **Rulings drift out of date as requirements change.** Accepted. Rulings are dated, and a ruling can be revisited explicitly, which is still better than the current state where nothing is written down at all.
