# Architecture dossier template

Companion to `2026-09-20-architecture-dossier-design.md`. Copy the skeleton below to `docs/design/<area>.md` and fill it in. Graduates into `.claude/skills/architecture-dossier/` after the core pilot.

## Authoring rules

1. **Every load-bearing claim carries `file:line`.** A claim is load-bearing if a simplification decision could rest on it. No exceptions, and no claims recovered from memory.
2. **Describe what the code does, not what it should do.** Opinions belong in section 7 and are labelled as recommendations.
3. **Harvest before you write.** Existing XML documentation and inline comments in this repository already state many invariants. Quote and cite them rather than paraphrasing, and note where two of them disagree.
4. **Compact.** One canonical location per concept. Do not restate code in prose. If a reader would be better served by reading the code, cite it instead.
5. **Default to a table, a list or a diagram.** Use prose only for a why that cannot be tabulated. A paragraph that is really a comparison should be a table, and a paragraph that is really a sequence should be a list. Shorter is the goal, not more markup: two sentences of genuine reasoning stay two sentences.
6. **Diagrams are additive, never substitutive.** A diagram cannot carry a `file:line`, and the verification pass works on citations. Put the picture beside the cited table so a reader grasps the shape fast and checks the detail in the rows. Never replace a cited row with a picture. Keep a diagram to the mechanism: twenty-four nodes is a hairball and is worse than the table next to it. No custom colours, so both GitHub themes render.
7. **No em dashes.**
8. **Unverified is a valid answer.** An invariant nobody can find a test for is marked Unverified, not quietly assumed.
9. **The dossier recommends, the maintainer rules.** Never write a ruling the maintainer has not given.

## Skeleton

````markdown
# <Area>: Architecture Dossier

Area: <projects and namespaces covered>
Boundary: <what is explicitly out of scope, and which dossier owns it>
Written against: `<commit sha>`
Verified: <date, by whom, or "pending">

Citation convention: a bare `:123` refers to the file named in the enclosing subsection's canonical implementation. A full path is given whenever the file changes.

## Summary

One screen. A reader must be able to answer "what does this tell me" without reading the rest. Written last, derived only from what is below, never from memory.

| | |
|---|---|
| Area | <files, lines> |
| Invariants | <N>, of which <M> covered by a test |
| Concepts | <N>, of which <M> have more than one implementation |
| Contradictions | <N> |
| Candidates | <N>, of which <M> ruled |

Then the findings that matter most, as a list, each linking down to its section. Then what is still missing, so nobody mistakes an unwritten section for an empty result.

## 1. Supported use cases

What this area promises, in the caller's words.

| # | Use case | Status | Cost | Ruling |
|---|---|---|---|---|
| 1 | <what a caller can rely on> | Supported / Best effort / Unsupported / Undecided | <the mechanisms that exist to serve it> | <date + maintainer ruling, or blank> |

Status meanings: Supported means guaranteed and defended. Best effort means it usually works and is not guaranteed. Unsupported means explicitly ruled out. Undecided means nobody has ruled, which is not the same as supported.

## 2. Contracts and invariants

Assertions that hold, each stated so it could be tested.

| # | Invariant | Evidence | Covered |
|---|---|---|---|
| I1 | <assertion> | `path/File.cs:123` | `TestName` / Unverified |

Note explicitly where two sources in the code state different things.

## 3. Concept census

Each noun this area implements, and every place the same idea appears.

### <Concept name>

- **What it means:** one sentence.
- **Canonical implementation:** `path/File.cs:123`.
- **Other implementations of the same idea:** each with `file:line` and how it differs.
- **Assessment:** one implementation, or N implementations that should be one.

The purpose of this section is to make duplicate concepts countable. A concept implemented in more than one place is a candidate unless the difference is justified in writing.

## 4. Flows

The execution paths this area owns. One subsection per flow, each with a mermaid sequence diagram and a numbered walkthrough naming the mechanism that engages at each step.

### <Flow name>

```mermaid
sequenceDiagram
```

| Step | Mechanism | Location | Can it be skipped |
|---|---|---|---|

The last column is what makes this section produce candidates: a path that crosses several gates enforcing the same check is a finding.

## 5. Threading and shared state

| State | Owner | Protected by | Ordering guarantee | Read without the lock |
|---|---|---|---|---|

Followed by:

- **Lock order.** The total order, and what happens if it is violated.
- **Ambient channels.** Every thread-static or async-local slot, what it carries, who sets it, who consumes it, and its lifetime.
- **Reentrancy.** Which callbacks can re-enter which paths, and what forbids the rest.

## 6. Gaps and limitations

| # | Gap | Kind | Issue |
|---|---|---|---|
| G1 | <what does not work, or is unspecified> | Known broken / Unspecified / Best effort | #NNN |

Every open issue and pull request in this area appears here or in the backlog disposition below.

## 7. Candidates

Ranked by expected reduction. The output of the whole document.

### C1. <Name>

- **Tag:** duplicate-concept / unreachable / expensive-use-case / accidental-complexity
- **What exists:** the mechanism, with `file:line`.
- **Size:** production lines involved.
- **Why it exists:** the use case or bug it was built for, with the issue or pull request if known.
- **Recommendation:** what to do, and what it costs.
- **Decision needed:** the precise question for the maintainer.
- **Ruling:** <date + decision, or blank>

## 8. Backlog disposition

| Issue / PR | Disposition | Rationale |
|---|---|---|
| #NNN | Gap G1 / Close, superseded / Close, speculative / Keep, depends on I3 | |
````

## Producing one

Follow the nine steps in the design document. Step 5, independent verification, is mandatory and runs before the maintainer sees the document: one reader per evidence-bearing section, sections 2, 3, 4 and 5, each checking every `file:line` claim against the code and returning confirm or refute per claim, with no fixing. Refuted claims are corrected or dropped before the maintainer sees anything.

The Summary block is written last, after the rulings, and derived only from the sections below it. Writing it early guarantees it drifts.
