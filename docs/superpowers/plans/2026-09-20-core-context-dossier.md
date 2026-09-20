# Core Context Dossier Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce `docs/design/core-context.md`, a verified architecture dossier for the `Namotion.Interceptor` core library, ending in a ranked list of simplification candidates the maintainer rules on.

**Architecture:** Eight-section dossier per `docs/superpowers/specs/2026-09-20-architecture-dossier-template.md`. Analysis sections (2 Contracts, 3 Concept census, 4 Flows, 5 Threading) are written first from the code, then verified by independent readers, then the derived sections (1 Supported use cases, 6 Gaps, 7 Candidates, 8 Backlog) are written on top of verified material only.

**Tech Stack:** Markdown with mermaid sequence diagrams. Evidence gathered with `grep`, `git log` and `gh`. No production code changes in this plan.

**Adaptation note:** This is an analysis plan, not a TDD plan, so there is no failing-test-then-implement cycle. The discipline that replaces it: every load-bearing claim is written with `file:line` evidence, and Task 6 dispatches independent readers who confirm or refute each claim against the code before any conclusion is drawn from it. Refuted claims are corrected or deleted. That gate is the reason this plan is trustworthy, so do not skip or reorder it.

**Working directory:** `/home/rico/GitHub/Namotion.Interceptor/.claude/worktrees/arch-dossier` on branch `docs/architecture-dossier`. Run every command from there.

---

## File Structure

| File | Responsibility |
|---|---|
| `docs/design/core-context.md` | Create. The dossier. The only durable deliverable. |
| `/tmp/claude-1000/.../scratchpad/core-evidence/` | Create. Raw command output used as evidence while writing. Never committed. |
| `docs/superpowers/specs/2026-09-20-architecture-dossier-template.md` | Read only. The skeleton to copy. |
| `docs/superpowers/specs/2026-09-20-architecture-dossier-design.md` | Read only. The method and the nine steps. |

The area under analysis, 36 files and 3544 lines, all of `src/Namotion.Interceptor/`:

| Group | Files |
|---|---|
| Context and services | `InterceptorSubjectContext.cs` (1088), `IInterceptorSubjectContext.cs`, `InterceptorSubjectContextExtensions.cs`, `Ordering/ServiceOrderResolver.cs` (261) |
| Interception pipeline | `Interceptors/IReadInterceptor.cs`, `Interceptors/IWriteInterceptor.cs` (339), `Interceptors/IMethodInterceptor.cs`, `Interceptors/ILifecycleInterceptor.cs`, `Interceptors/IInterceptorExecutor.cs`, `Interceptors/InterceptorExecutor.cs` (142) |
| Chain caching | `Cache/ReadInterceptorChain.cs`, `Cache/ReadInterceptorFactory.cs`, `Cache/WriteInterceptorChain.cs`, `Cache/WriteInterceptorFactory.cs`, `Cache/MethodInvocationChain.cs`, `Cache/MethodInvocationFactory.cs`, `Cache/Delegates.cs` |
| Origin and write state | `ChangeOrigin.cs`, `AttemptedOrigin.cs`, `PendingOrigin.cs`, `PropertyWriteState.cs`, `SubjectChangeContext.cs` |
| Property identity | `PropertyReference.cs` (278), `PropertyReferenceExtensions.cs`, `SubjectPropertyMetadata.cs`, `PropertyInfoExtensions.cs`, `PropertyChangedEventArgsCache.cs` |
| Subject contract | `IInterceptorSubject.cs`, `InterceptorSubjectExtensions.cs`, `IRaisePropertyChanged.cs` |
| Attributes | `Attributes/DerivedAttribute.cs`, `Attributes/InterceptorSubjectAttribute.cs`, `Attributes/RunsBeforeAttribute.cs`, `Attributes/RunsAfterAttribute.cs`, `Attributes/RunsFirstAttribute.cs`, `Attributes/RunsLastAttribute.cs` |

Known inputs that already exist and must be used rather than re-derived:

- `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt`, 231 lines. The exact public surface of core. Primary input for section 1.
- `src/Namotion.Interceptor.Tests/`, 25 files, 145 `[Fact]`/`[Theory]` tests. The Covered/Unverified mapping for section 2.
- The core library is unusually well commented, with invariants already stated inline (for example `InterceptorSubjectContext.cs:18` states rule R1 and `:22` the lock order). Section 2 is mostly harvesting.

---

## Task 1: Scaffold the dossier and pin the baseline

**Files:**
- Create: `docs/design/core-context.md`
- Create: scratchpad directory for evidence

- [ ] **Step 1: Create the evidence scratchpad**

Use the scratchpad path given in your own environment, which is session specific. Do not glob for it: `/tmp/claude-1000/-home-rico-GitHub-Namotion-Interceptor/` holds one directory per past session and a glob matches dozens of them.

```bash
mkdir -p "$SCRATCHPAD/core-evidence"
```

where `$SCRATCHPAD` is the scratchpad directory named in your environment. Everything written there is working material and is never committed.

- [ ] **Step 2: Record the baseline facts**

```bash
git rev-parse HEAD
find src/Namotion.Interceptor -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' | wc -l
find src/Namotion.Interceptor -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -exec cat {} + | wc -l
```

Expected: a sha, `36`, and `3544` (measured on master; the `feature/value-assertions` branch differs). If the counts differ, master moved. Record the new numbers and use them, do not use the numbers in this plan.

- [ ] **Step 3: Churn ranking for the area**

```bash
git log --since='12 months ago' --name-only --pretty=format: origin/master -- 'src/Namotion.Interceptor/*.cs' | grep '\.cs$' | sort | uniq -c | sort -rn | head -20
```

Save the output to the evidence directory. It tells you which core files move, which is where to look hardest in Task 4.

- [ ] **Step 4: Create the dossier skeleton**

Copy the skeleton fenced block out of `docs/superpowers/specs/2026-09-20-architecture-dossier-template.md` into `docs/design/core-context.md`. Fill only the header:

```markdown
# Core Context: Architecture Dossier

Area: `src/Namotion.Interceptor/` (the core library, .NET Standard 2.0). 36 files, 3544 lines.
Boundary: Tracking, Registry, Connectors and the source generator are out of scope and get their own dossiers. Where core defines a contract those libraries depend on, the contract is in scope and the consumer's use of it is not.
Written against: `<sha from Step 2>`
Verified: pending
```

Leave every other section with its template tables empty. Do not write placeholder prose.

- [ ] **Step 5: Commit**

```bash
git add docs/design/core-context.md
git commit -m "docs(core): scaffold the core context dossier"
```

---

## Task 2: Section 2, contracts and invariants

The core library already states most of its invariants inline. This task harvests them rather than inventing them.

**Files:**
- Modify: `docs/design/core-context.md` (section 2)

- [ ] **Step 1: Read all 36 files**

Read every file in `src/Namotion.Interceptor/`. At 3544 lines this is cheap and it is the only way to find invariants stated in comments rather than in code. Do not sample.

- [ ] **Step 2: Extract the explicitly stated rules**

```bash
grep -rn --include=*.cs -E 'Lock order|R[0-9]:|[Ii]nvariant|must not|never|Do not |load-bearing|deliberately|by design' src/Namotion.Interceptor
```

Save to the evidence directory. This finds the comment-stated rules. Reading in Step 1 finds the rest.

- [ ] **Step 3: Write section 2**

One row per invariant. Example rows showing the required shape, using two real ones already in the code:

```markdown
| # | Invariant | Evidence | Covered |
|---|---|---|---|
| I1 | A service query takes no context lock. It pins one snapshot with a single volatile read and walks other contexts' snapshots the same way, so the downward service walk and the upward invalidation walk cannot form a lock cycle, including in cyclic fallback graphs. | `src/Namotion.Interceptor/InterceptorSubjectContext.cs:18` | Unverified |
| I2 | Lock order is `_mutationLock` then a `_usedByContexts` set lock, never the reverse. No path takes a second `_mutationLock`. | `src/Namotion.Interceptor/InterceptorSubjectContext.cs:22` | Unverified |
| I3 | `LastNonSourceCommitRevision` excludes `FromSource` commits but includes `Confirmed` commits. The asymmetry is load-bearing and must not be "fixed" in either direction. | `src/Namotion.Interceptor/PropertyWriteState.cs` (the `LastNonSourceCommitRevision` remarks block) | Unverified |
```

Rules for this section:
- State each invariant so it could be tested. "The context is thread safe" is not an invariant. "A query never takes `_mutationLock`" is.
- Cite the line where the rule is stated or enforced, not the file.
- Leave `Covered` as `Unverified` for now. Task 3 fills it.
- Add a subsection `### Contradictions` listing any two places in the code that state different things, with both citations. Write nothing there if you find none.

- [ ] **Step 4: Commit**

```bash
git add docs/design/core-context.md
git commit -m "docs(core): harvest declared contracts and invariants"
```

---

## Task 3: Map invariants to tests

**Files:**
- Modify: `docs/design/core-context.md` (section 2, `Covered` column)

- [ ] **Step 1: List the core tests**

```bash
grep -rn --include=*.cs -E '\[(Fact|Theory)\]' -A3 src/Namotion.Interceptor.Tests | grep -E 'public (async )?(void|Task)' | sed -E 's/.*(public .*)\(.*/\1/'
```

Expected: about 145 test method names. Save to the evidence directory.

- [ ] **Step 2: Fill the Covered column**

For each invariant row, search the test project for a test that would fail if the invariant were broken:

```bash
grep -rn --include=*.cs -i '<keyword from the invariant>' src/Namotion.Interceptor.Tests
```

Write the test method name if one exists, otherwise leave `Unverified`. Do not write a test name unless you have read the test and confirmed it actually asserts the invariant. A test that merely touches the same class is not coverage.

- [ ] **Step 3: Add the coverage summary**

Under section 2, add one line: `N invariants, M covered by a test, K unverified.` The unverified list is an output of this dossier and feeds later work.

- [ ] **Step 4: Commit**

```bash
git add docs/design/core-context.md
git commit -m "docs(core): map invariants to test coverage"
```

---

## Task 4: Section 3, concept census

This is the section that produces the deletions. Its purpose is to make duplicate concepts countable.

**Files:**
- Modify: `docs/design/core-context.md` (section 3)

- [ ] **Step 1: Enumerate the concepts**

Work from the file groups in the File Structure table above. Expect roughly these nouns, and add any you find that are missing: context, service, service ordering, fallback context, delegation target, context state snapshot, interceptor, interceptor chain, chain cache, property reference, property metadata, subject, change origin, write state, timestamp, ambient scope.

- [ ] **Step 2: Write one subsection per concept**

Required shape, with a worked example using a real finding:

```markdown
### Change origin

- **What it means:** the provenance of a property write, used downstream to decide whether a change is echoed back to the source that sent it.
- **Canonical implementation:** `src/Namotion.Interceptor/ChangeOrigin.cs:35`, a readonly struct of `ChangeOriginKind` plus an optional source.
- **Other implementations of the same idea:**
  - `src/Namotion.Interceptor/AttemptedOrigin.cs:8` wraps a `ChangeOrigin` with the value evidence it was stamped with, the "attempted" stage.
  - `src/Namotion.Interceptor/PendingOrigin.cs:22` holds the "pending" stage in a thread-static frame keyed by target property.
  - `src/Namotion.Interceptor/PropertyWriteState.cs` records the finalized outcome as revision counters that encode which origins committed.
- **Assessment:** one three-stage state machine (pending, attempted, finalized) implemented across four types plus a thread static. Candidate.
```

- [ ] **Step 3: Rule for the assessment line**

Write either `one implementation` or `N implementations that should be one`. If N is greater than one and the difference is justified, quote the justification with its `file:line`. An unjustified duplicate becomes a candidate in Task 8.

- [ ] **Step 4: Commit**

```bash
git add docs/design/core-context.md
git commit -m "docs(core): concept census"
```

---

## Task 5: Sections 4 and 5, flows and threading

**Files:**
- Modify: `docs/design/core-context.md` (sections 4 and 5)

- [ ] **Step 1: Write section 4, the four flows core owns**

One subsection each for: **property read**, **property write**, **method invocation**, **service resolution**. Each gets a mermaid sequence diagram and the step table from the template.

Required shape for the step table, which is what turns this section into findings:

```markdown
| Step | Mechanism | Location | Can it be skipped |
|---|---|---|---|
| 1 | Pin the context state with a volatile read | `InterceptorSubjectContext.cs:112` | No, this is what makes the query lock free |
| 2 | Resolve the delegation target if one is set | `InterceptorSubjectContext.cs:116` | Only when no fallback context is registered |
```

The last column is the point. A path crossing several gates that enforce the same check is a finding, and it goes into section 7.

- [ ] **Step 2: Gather the threading evidence**

```bash
grep -rn --include=*.cs -E 'ThreadStatic|AsyncLocal|Volatile\.|Interlocked\.|lock *\(|SemaphoreSlim|Monitor\.' src/Namotion.Interceptor
```

Save to the evidence directory. Expect at least seven `[ThreadStatic]` slots: five traversal buffers in `InterceptorSubjectContext.cs`, `PendingOrigin._frame`, and `SubjectChangeContext._current`.

- [ ] **Step 3: Write section 5**

Fill the state table, then the three required subsections:

- **Lock order.** The total order and what happens if it is violated. `InterceptorSubjectContext.cs:22` already states it.
- **Ambient channels.** One row per thread-static or async-local slot: what it carries, who sets it, who consumes it, its lifetime, and whether it survives an `await`. `PendingOrigin.cs:22` documents that it is synchronous by design and never crosses an await. Record that for each slot, because a slot that must not cross an await is a constraint the dossier has to state.
- **Reentrancy.** Which callbacks can re-enter which paths. `PendingOrigin.cs` documents that same-property re-entry from `OnChanging` is unsupported. Find the rest.

- [ ] **Step 4: Commit**

```bash
git add docs/design/core-context.md
git commit -m "docs(core): execution flows, threading and shared state"
```

---

## Task 6: Independent verification

Mandatory gate. No conclusion may be drawn from an unverified claim. Do not write sections 1, 6 or 7 before this task completes.

**Files:**
- Modify: `docs/design/core-context.md` (corrections only)

- [ ] **Step 1: Dispatch three verifiers in parallel**

Send all three Agent calls in a single message so they run concurrently. Use the Opus model for each. Each gets a fresh general-purpose agent with no knowledge of how the dossier was written.

Prompt template, with `<SECTION>` replaced by `2 (Contracts and invariants)`, `3 (Concept census)` and `5 (Threading and shared state)` respectively:

```
Read docs/design/core-context.md, section <SECTION>, in the git worktree at
/home/rico/GitHub/Namotion.Interceptor/.claude/worktrees/arch-dossier.

For EVERY claim in that section that carries a file:line citation, open the cited
file at the cited line and decide whether the code actually supports the claim.

Return a table with one row per claim: claim id, verdict (CONFIRMED / REFUTED /
CITATION WRONG / UNSUPPORTED BY EVIDENCE), and for anything that is not CONFIRMED,
the correct fact with its own file:line.

Rules:
- Do not fix the document. Return verdicts only.
- Do not accept a claim because it sounds right. Open the file.
- A citation that points at the wrong line is CITATION WRONG even if the claim is true.
- If a claim has no citation at all, it is UNSUPPORTED BY EVIDENCE.
```

- [ ] **Step 2: Apply the verdicts**

For each non-CONFIRMED row: correct the claim, fix the citation, or delete the claim. Deleting is a valid and often correct outcome. Do not argue with a verifier without opening the file yourself.

- [ ] **Step 3: Record the verification result**

Update the dossier header:

```markdown
Verified: 2026-09-20, three independent readers over sections 2, 3 and 5. N claims checked, M corrected, K deleted.
```

- [ ] **Step 4: Commit**

```bash
git add docs/design/core-context.md
git commit -m "docs(core): apply independent verification verdicts"
```

---

## Task 7: Sections 1 and 6, supported use cases and gaps

Written only on verified material.

**Files:**
- Modify: `docs/design/core-context.md` (sections 1 and 6)

- [ ] **Step 1: Derive the use case list from the public API**

```bash
cat src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt
```

231 lines, the exact public surface. Every public capability maps to at least one use case row. A capability with no use case row is itself a finding.

- [ ] **Step 2: Write section 1**

```markdown
| # | Use case | Status | Cost | Ruling |
|---|---|---|---|---|
| U1 | A subject graph resolves services through more than one context, via registered fallback contexts. | Undecided | Fallback contexts, delegation targets, the cyclic delegation marker, upward invalidation walks, the per-context used-by set, and five thread-static traversal buffers. Roughly 600 of the 1088 lines of `InterceptorSubjectContext.cs`. | |
```

Rules:
- Status is `Undecided` for everything the maintainer has not ruled on. Do not write `Supported` because the code supports it. That is the whole point of the column.
- `Cost` names the mechanisms and gives a line estimate, because the ruling is a price comparison.
- Leave `Ruling` blank. Task 10 fills it from the maintainer.

- [ ] **Step 3: Write section 6 from the issue backlog**

```bash
gh issue list --limit 100 --label "area: core" --json number,title --template '{{range .}}#{{.number}} {{.title}}{{"\n"}}{{end}}'
```

Ten issues carry `area: core`: #219, #222, #224, #409, #410, #411, #443, #464, #539, #552.

Four more are core bugs without the label and must be included: #402 (`InterceptorExecutor` fallback overrides, callback ordering undoes a concurrent add and blocks detach on a cyclic chain), #403 (concurrent `TryAddService` across delegation levels can admit a duplicate), #404 (`TryAddService` factory runs under the mutation lock so a cross-context factory can deadlock), #406 (harden service equality handling against reentrancy and duplicate retention).

Note in the dossier that these four are mislabelled, and add `area: core` to them:

```bash
gh issue edit 402 --add-label "area: core"
gh issue edit 403 --add-label "area: core"
gh issue edit 404 --add-label "area: core"
gh issue edit 406 --add-label "area: core"
```

Then one row per gap:

```markdown
| # | Gap | Kind | Issue |
|---|---|---|---|
| G1 | Concurrent `TryAddService` across delegation levels can admit a duplicate service. | Known broken | #403 |
```

- [ ] **Step 4: Commit**

```bash
git add docs/design/core-context.md
git commit -m "docs(core): supported use case surface and known gaps"
```

---

## Task 8: Section 7, candidates

The output of the document.

**Files:**
- Modify: `docs/design/core-context.md` (section 7)

- [ ] **Step 1: Collect candidates from the earlier sections**

Sources, in order of yield:
- Section 3 assessments reading `N implementations that should be one`.
- Section 4 step tables where `Can it be skipped` is anything other than a flat `No`.
- Section 1 rows whose `Cost` is large relative to how likely the use case is to be real.
- Section 6 gap clusters where several issues share one root.

- [ ] **Step 2: Write one subsection per candidate, ranked by expected reduction**

Required shape, with the four already predicted by the spec as the expected floor. Confirm or refute each against what you actually found, and add the ones you discovered:

```markdown
### C1. Multi-context topology

- **Tag:** expensive-use-case
- **What exists:** fallback contexts, delegation targets, the cyclic delegation marker, upward invalidation walks, the lazily allocated per-context used-by set, and five thread-static traversal buffers. `src/Namotion.Interceptor/InterceptorSubjectContext.cs:38` and following.
- **Size:** <measured lines>
- **Why it exists:** context inheritance so child subjects join the parent's pipeline without being handed a context. Cyclic fallback support appears to be defensive rather than requested; confirm against the git history of the cyclic marker.
- **Recommendation:** <yours, with its cost>
- **Decision needed:** Does any supported scenario require more than one context per subject graph, and does any require cyclic fallback graphs? #494 and #501 are both attempts to collapse this to one context, so a ruling here also unblocks those.
- **Ruling:**
```

The other three predicted candidates, to confirm or refute: ambient thread-static channels (`duplicate-concept`, seven slots across three purposes, issue #369 already proposes making origin explicit); the origin lifecycle across four types (`duplicate-concept`); service ordering, `ServiceOrderResolver.cs` at 261 lines plus four attributes (`accidental-complexity`).

- [ ] **Step 3: Size every candidate**

```bash
git log --oneline -S '<a distinctive identifier from the mechanism>' -- src/Namotion.Interceptor
```

Use this to find when and why a mechanism was introduced, which is what the `Why it exists` line needs. A mechanism added by a bug fix, with the issue in the commit message, is the strongest pruning evidence in this whole exercise.

- [ ] **Step 4: Commit**

```bash
git add docs/design/core-context.md
git commit -m "docs(core): ranked simplification candidates"
```

---

## Task 9: Section 8, backlog disposition

**Files:**
- Modify: `docs/design/core-context.md` (section 8)

- [ ] **Step 1: Enumerate the pull requests that touch core**

```bash
gh pr list --limit 200 --json number,title,isDraft,createdAt --template '{{range .}}#{{.number}} {{if .isDraft}}DRAFT{{else}}ready{{end}} {{.title}}{{"\n"}}{{end}}'
```

For each candidate, check whether it touches core:

```bash
gh pr view <number> --json files --jq '.files[].path' | grep '^src/Namotion.Interceptor/' | head
```

Known large ones to check first because they dominate the decision: #494 (+19789/-6736, single-context lifecycle), #501 (+28522/-7565, race-safe single-context lifecycle protocol), #472 (enforce unique context authorities), #412 (marked superseded in its own title), #322 (clean up ancestor fallback contexts on subject detach).

- [ ] **Step 2: Write section 8**

```markdown
| Issue / PR | Disposition | Rationale |
|---|---|---|
| #412 | Close, superseded | Its own title marks it superseded. Confirm what superseded it before closing. |
| #403 | Gap G1 | Recorded as a known gap in section 6, blocked on the ruling for C1. |
```

- [ ] **Step 3: Do not close anything yet**

Write the disposition column, but execute no closes. Closing is the maintainer's call and depends on the Task 10 rulings. The repository rule is that issues are not closed before the pull request that resolves them merges.

- [ ] **Step 4: Commit**

```bash
git add docs/design/core-context.md
git commit -m "docs(core): backlog disposition for the core area"
```

---

## Task 10: Ruling session

This task is not executable by an agent. It is a checkpoint with the maintainer.

**Files:**
- Modify: `docs/design/core-context.md` (Ruling columns only)

- [ ] **Step 1: Present sections 1 and 7 to the maintainer**

For each row, give the recommendation and the reasoning. Do not present the whole dossier for reading; present the decisions.

- [ ] **Step 2: Record every ruling with a date**

```markdown
| Ruling |
|---|
| 2026-09-20: Supported. Context inheritance stays, cyclic fallback graphs are dropped. |
```

Anything not ruled stays `Undecided`. Never infer a ruling.

- [ ] **Step 3: Commit**

```bash
git add docs/design/core-context.md
git commit -m "docs(core): record maintainer rulings on use cases and candidates"
```

---

## Task 11: File the work items

**Files:** No repository files. Creates GitHub issues.

- [ ] **Step 1: One issue per approved removal**

Each sized to roughly 150 to 250 production lines. If a candidate is bigger, split it into staged issues and say in each which one must land first.

```bash
gh issue create --title "<what is removed>" --label "area: core" --body "<body>"
```

The body must contain: the candidate id and a link to `docs/design/core-context.md`, the maintainer's ruling and its date, the mechanisms to remove with `file:line`, the invariants from section 2 that must still hold afterwards, and the tests that cover them.

- [ ] **Step 2: Link the rulings back**

Add the issue number to each candidate's `Ruling` line in the dossier, then commit.

```bash
git add docs/design/core-context.md
git commit -m "docs(core): link candidates to their work items"
```

- [ ] **Step 3: Open the dossier pull request**

```bash
git push -u origin docs/architecture-dossier
gh pr create --title "docs: core context architecture dossier" --body "<body>"
```

The removals it authorizes are separate pull requests, one per issue, so no reviewer holds an analysis and a refactor at once.

---

## Task 12: Extract the skill

Only after Task 11. The point of piloting first was to learn what the template gets wrong.

**Files:**
- Create: `.claude/skills/architecture-dossier/SKILL.md`
- Create: `.claude/skills/architecture-dossier/template.md`

- [ ] **Step 1: List what changed**

Write down every place the template or the nine-step process had to be adapted during Tasks 1 to 11. That list is the actual content of the skill, because it is what a future run would otherwise get wrong.

- [ ] **Step 2: Write the skill**

Frontmatter `name` and `description` following `superpowers:writing-skills`. Body carries the nine steps, the seven authoring rules, and the mandatory verification prompt from Task 6 verbatim, since that is the step most likely to be skipped under context pressure.

- [ ] **Step 3: Move the template**

Move the skeleton from `docs/superpowers/specs/2026-09-20-architecture-dossier-template.md` to `.claude/skills/architecture-dossier/template.md`, amended with what Step 1 found.

- [ ] **Step 4: Commit**

```bash
git add .claude/skills/architecture-dossier
git commit -m "docs: extract the architecture dossier skill from the core pilot"
```

---

## Notes for the executor

- **Never `git add docs/`.** `docs/superpowers/` is untracked but not gitignored in this repository, so directory-level staging commits scaffolding. Always stage by explicit file path.
- **No em dashes** anywhere in the dossier. Repository rule.
- **No AI attribution** in commit messages, issue bodies or pull request descriptions beyond the trailers this session already uses.
- **No production code changes** in this plan. If you find a bug while reading, file an issue and note it in section 6. Do not fix it here.
- **A claim without a citation is not a claim.** If you cannot cite it, either find the evidence or delete the sentence.
- **Outward writes.** The only steps that change anything outside this branch are the four `gh issue edit` label fixes in Task 7, the issue creation in Task 11, and the push and pull request in Task 11. Everything else is local. Nothing closes an issue: that waits for the pull request that resolves it to merge.
