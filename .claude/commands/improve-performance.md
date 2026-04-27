# Improve Performance

Run a measurement-driven performance pass: brainstorm candidates, implement each on its own branch, benchmark each against a shared baseline, and let the user pick winners.

## Usage

`/improve-performance`

Optional argument: a topic slug (e.g. `attach-detach`) or a path to a doc/issue with pre-existing candidates. If omitted, the skill asks.

## Workflow

The skill has three phases: interactive setup (questions up front), automated loop (one subagent per candidate, sequential, hands-off), and interactive picks (user selects winners). All branch management happens in the single working checkout. No worktrees.

The design doc lives only on the parent branch and is updated there as each candidate finishes. Candidate branches contain only the implementation commit.

### Phase 1: Setup (interactive, all questions up front)

Before doing any work, refuse to proceed if the working tree is dirty (`git status --porcelain` non-empty). Tell the user to commit or stash and re-invoke.

Ask the user one question at a time:

1. **Topic slug.** Used as the branch prefix and doc filename. Example: `attach-detach`. Reject anything containing slashes, spaces, or uppercase.
2. **Candidate source.** Offer: (a) brainstorm fresh now, (b) load from an existing doc path, (c) load from a GitHub issue number, (d) user pastes a list. For (a), drive a focused brainstorm in this conversation (no need to spawn a subagent).
3. **Benchmark mapping.** Read the benchmark project sources. For each candidate, propose a BenchmarkDotNet filter pattern. Show the table with a `Coverage` column. For candidates with no clear coverage, ask per candidate: (a) write a new benchmark on the parent branch, (b) skip the candidate, (c) run with the closest existing filter and accept noisy results.
4. **Test projects.** Default: the test projects whose subjects the candidates touch (e.g. `Namotion.Interceptor.Registry.Tests Namotion.Interceptor.Tracking.Tests`). Show the default and let the user override.
5. **Run config.** `LaunchCount` (default 3) and `Short` toggle (default off).
6. **GitHub issue.** Ask whether to open or update a tracking issue (default: skip).

Then print a final plan summary (parent branch name, list of candidates with branch names, benchmark filter, test projects, doc path) and ask one explicit `proceed?` confirmation. Warn that the working directory will be owned by the skill for ~30-60 min.

On confirmation:
- Create the parent branch: `git checkout -b performance/<slug>` from `master`.
- For each new benchmark from step 3a: prompt the user (or implement inline if simple) to add it to `Namotion.Interceptor.Benchmark`. Build to verify. Commit with `perf(bench): add <name> benchmark` on the parent branch.
- Write `docs/design/performance-<slug>.md` with: motivation, hot spots, candidates list (numbered, each with a `## Results` placeholder section), benchmark filter, decision criteria. Commit on the parent branch.
- If a tracking issue was requested: create or update it with a link to the doc.

### Phase 2: Automated loop (sequential, one subagent per candidate)

For each candidate (in the order listed in the doc):

1. Verify state: working tree clean, current branch is `performance/<slug>`. If not, attempt cleanup with `git checkout -- . && git clean -fd`. If still not clean, hard stop and surface the situation to the user.
2. Create the candidate branch: `git checkout -b performance/<slug>/<candidate-slug>` (from the parent's current HEAD, which includes any previous doc updates).
3. Spawn the `performance-optimizer` subagent with a prompt containing:
   - `task_description` (the candidate description verbatim from the doc)
   - `benchmark_filter` (from the mapping table)
   - `base_branch` = `performance/<slug>`
   - `launch_count` (from setup)
   - `test_projects` (from setup)
   - `current_branch` = `performance/<slug>/<candidate-slug>`
   - Plus: `candidate_title` so the orchestrator can use it in the eventual commit message.
4. Wait for the subagent to return its structured result. The agent leaves changes uncommitted in the working tree.
5. Based on the returned status:
   - `success`: stage with `git add .` and commit with `git commit -m "perf: <candidate_title>"` on the candidate branch. Capture the resulting commit SHA.
   - `build-failed`, `tests-failed`, `benchmark-failed`, `clarification-needed`: clean the working tree with `git checkout -- . && git clean -fd`. Drop any dangling stash matching `benchmark-script-auto-stash` if present.
   - `precondition-failed`: hard stop and surface to the user.
6. Switch back to the parent: `git checkout performance/<slug>`.
7. Append the result to the design doc under that candidate's `## Results` section. Include status, commit SHA (or "skipped" with reason), files changed, notes, and the benchmark comparison table (extract just the table, not the full BenchmarkDotNet header).
8. Commit the doc update on the parent branch with message `docs(perf): <candidate-slug> results`.
9. Print a one-line summary to the user (`✓ candidate N/M: <slug> (status, mean Δ, alloc Δ)`).
10. Continue with the next candidate.

### Phase 3: Picks (interactive)

When the loop finishes, print a summary table:

```
N | Candidate slug | Status | Mean Δ | Alloc Δ | Branch
```

Pull the deltas from each candidate's results section in the doc (parse the comparison report). Mark regressions with a flag.

Ask the user: "Which to keep? (comma-separated indices, `all`, or `none`)."

On selection:
- Cherry-pick each picked candidate's implementation commit onto `performance/<slug>` in the order chosen. Stop on conflict and surface the file list to the user.
- Re-run the benchmark on the combined parent branch: `pwsh scripts/benchmark.ps1 -Filter "<filter>" -BaseBranch master -LaunchCount <n>`. This catches interaction effects between picked candidates.
- Append a `## Combined results` section to the design doc with this final report. Commit on the parent branch.

Print the parent branch name and a suggested next step (`gh pr create -B master -H performance/<slug>`). Do NOT open the PR. Do NOT push.

## Failure handling

- Dirty tree at any phase boundary: stop, do not attempt destructive cleanup. Show `git status` to the user.
- Subagent returns `build-failed` or `tests-failed`: mark candidate skipped in the doc with the failure detail, clean up the candidate branch state (`git checkout -- . && git clean -fd`, drop any stash named `benchmark-script-auto-stash`), continue with next candidate.
- Subagent returns `clarification-needed`: surface to user mid-loop. Either edit the candidate description in the doc and re-run that candidate, or skip.
- Cherry-pick conflict during final assembly: stop, show conflict files, let user resolve manually.

## Constraints

- Never push branches. Never open PRs. Never delete branches automatically.
- Never modify `master` outside this skill. The parent branch holds all in-progress changes.
- Branch names: `performance/<slug>` for the parent, `performance/<slug>/<candidate-slug>` for each candidate. Slugs are lowercase, kebab-case, no slashes.
- Doc path: `docs/design/performance-<slug>.md`. If the file already exists, ask the user whether to append a new dated section or use a different slug.
- Commit messages and doc text: no em dashes, no AI attribution, no "Generated with..." footer.
- Subagents only run in this repository.

## Notes

- The benchmark script (`scripts/benchmark.ps1`) accepts `-BaseBranch` so the comparison runs against the parent perf branch, not master. This matters for candidates that depend on a benchmark added on the parent: comparing against master would show "new benchmark + candidate" with no baseline.
- Sequential by design. Concurrent BenchmarkDotNet runs on the same machine pollute each other's numbers, defeating the purpose.
- The user owns the working directory while this runs (~30-60 min typical for 5-7 candidates at `LaunchCount 3`). Warn during setup confirmation.
