# Transaction Test-Suite Cleanup Design

Addresses item 3 of the #337 transaction review follow-ups (issue #338, follow-up comment). Items 1 and 2 were completed in #339. This is a test-only change: no production code is modified, no public API changes.

## Goals

1. Rename all transaction tests to the repo convention `When<Condition>_Then<ExpectedBehavior>`.
2. Remove genuinely redundant tests without losing any coverage.
3. Eliminate the remaining hardcoded-wait and wall-clock assertions in transaction tests.
4. Fix tests whose names or assertions misrepresent what they verify.
5. Close the coverage gaps named in the issue.
6. Keep the diff easy to review: renames, deletions, fixes, and new tests land as separate commits.

## Non-Goals

- No file moves or file reorganization (e.g., `SubjectTransactionAdditionalTests.cs` keeps its name); moves would destroy rename visibility in diffs. A later trivial PR can redistribute files if desired.
- No renaming of test files or of tests outside the transaction suite.
- No production code changes.

## Scope

The 12 transaction test files:

- `src/Namotion.Interceptor.Tracking.Tests/Transactions/` (2 files)
- `src/Namotion.Interceptor.Connectors.Tests/Transactions/` (10 files)

## Structure: One PR, One Commit Per Concern

### Commit 1: Mechanical rename

Every test method in scope renamed to `When<Condition>_Then<ExpectedBehavior>`. Zero body changes, zero file moves, so the diff shows only method signature lines.

This commit absorbs two issue bullets that are pure naming problems:

- The five stale `ApplyAllChanges_*` methods in `SubjectPropertyChangeOperationsTests` are renamed to reference the actual method under test, `ApplyLocalChanges` (e.g., `WhenApplyLocalChangesWithExclude_ThenExcludedAreSkipped`).
- `CommitAsync_AfterConflictFailure_CanRetrySuccessfully` does not retry through a conflict (it restores the external value first, so the retry no longer conflicts). That behavior is legitimate and worth keeping, so it gets an honest name: `WhenConflictResolvedExternally_ThenSameTransactionRetrySucceeds`. The true retry-through-conflict companion is added in commit 5.

### Commit 2: Deletions with a disposition table

The duplicated lifecycle behaviors are implemented entirely in `Namotion.Interceptor.Tracking`'s `SubjectTransaction` and involve no writer, so a single survivor in Tracking.Tests suffices. Connectors.Tests keeps only lifecycle tests that genuinely involve a writer (e.g., `Dispose_DuringInFlightExclusiveCommit_ReleasesLockAfterCommitCompletes`).

| Behavior | Survivor (Tracking.Tests) | Deleted (Connectors.Tests) |
|----------|---------------------------|----------------------------|
| Commit twice throws | `WhenTransactionAlreadyCommitted_ThenCommitAgainThrows` | copies in `AdditionalTests`, `AsyncTests`, `LifecycleTests` |
| Idempotent dispose | `WhenDisposeCalledMultipleTimes_ThenIsIdempotent` | copies in `AdditionalTests`, `LifecycleTests` |
| Empty commit succeeds | `WhenCommittedWithNoChanges_ThenSucceeds` | copies in `AdditionalTests`, `LifecycleTests` |
| Commit after dispose throws | `WhenTransactionDisposed_ThenCommitThrows` | copy in `LifecycleTests` |
| Lock released on dispose | `WhenExclusiveTransactionDisposed_ThenLockIsReleased` | copy in `AsyncTests` |

(Test names as of commit 1; the table in the PR description uses the final names.)

Deletion rule: read each candidate's body first. Any assertion the survivor lacks is folded into the survivor in the same commit. Known folds:

- If the `AdditionalTests` commit-twice copy asserts message text ("ThrowsWithClearMessage"), the survivor gains that assert.
- Deleting the `AdditionalTests` empty-commit copy removes its stopwatch flake; the survivor gains a deterministic completes-synchronously assert (`CommitAsync(...).IsCompletedSuccessfully` is true before awaiting) to preserve the "completes immediately" intent without wall-clock measurement.

A test whose body exercises anything the survivor does not (different setup, writer involvement, extra behavior) is kept or merged, not deleted.

The commit message contains the full disposition table mapping every deleted test to its surviving equivalent.

### Commit 3: Determinism fixes

Rewritten in place, keeping the names they received in commit 1 and the same intent, no timing. Identified here by their pre-rename names:

- `OptimisticLocking_AllowsConcurrentTransactionStart`: drop the start-spread < 200ms measurement and the `Task.Delay(50)`. The deterministic essence of "optimistic begin does not block" is: begin all N optimistic transactions and assert all are simultaneously active before any completes.
- `OptimisticLocking_MultipleOptimisticCanCoexistInDifferentContexts`: replace `Task.Delay(50)` with a `TaskCompletionSource` handshake.

Same-spirit sweep in transaction test helpers:

- Simulated-hang sources using `Task.Delay(TimeSpan.FromSeconds(10), ct)` become `Task.Delay(Timeout.Infinite, ct)` (the hang is only ever ended by cancellation; a finite delay can wake spuriously on a slow CI run).
- The `Task.WhenAny(task, Task.Delay(timeout))` completion guard becomes `task.WaitAsync(timeout)`.
- `TransactionTestBase`'s parameterized, cancellation-aware slow-source delay stays; it simulates source latency for timeout tests and is not a synchronization wait.

### Commit 4: Honesty fixes

- `InterceptorRegistration_TransactionBeforeObservable` asserts interceptor order by matching `GetType().Name`, an implementation detail. The behavioral consequence of that order (no observable notifications fire for writes during an active transaction; notifications fire on commit) is already covered by `WhenTransactionActive_ThenPropertyChangedNotFired`, `WhenTransactionCommits_ThenPropertyChangedFired`, and the Tracking observable tests. After confirming that coverage, delete the test; if a gap is found, rewrite it against public behavior instead. The disposition table records the outcome.
- `WriteResult_Success_IsZeroAllocation` does not measure allocation. Delete it: `SourceWriteResult` is a readonly record struct, so the claim is structural, and allocation claims belong to the Benchmarks project.

### Commit 5: New coverage

1. **Multi-stage rollback content asserts**: for source-fail-plus-revert-fail and apply-fail-plus-both-reverts-fail flows, assert the exact properties in `FailedChanges` and the exact error messages in `Errors`, not just counts.
2. **Optimistic + writer + source failure**: an optimistic-locking transaction with a writer whose source write fails (Rollback mode), asserting the commit fails terminally, the optimistic lock is released (a new transaction can begin and commit), and the exception contents are correct.
3. **Errors-to-FailedChanges cardinality**: pin the documented relationship for a multi-property single-source batch failure: one error per failed source batch vs one failed change per property.
4. **`WhenConflictPersists_ThenRetryFailsAgain`**: a same-transaction retry while the external conflict still exists fails again with `SubjectTransactionConflictException`, proving repeated conflict detection on a retryable transaction.

All new tests follow repo conventions: `When..._Then...` names, explicit Arrange/Act/Assert comments, no hardcoded waits.

## Verification

At every commit boundary:

- `dotnet build src/Namotion.Interceptor.slnx` clean (warnings as errors)
- `dotnet test src/Namotion.Interceptor.Tracking.Tests` and `dotnet test src/Namotion.Interceptor.Connectors.Tests` green

The green run after commit 2 is the no-coverage-lost proof: the suite passes with only the survivors, before any new tests are added. Each commit is independently revertable.

No public API changes, so no `PublicApi.verified.txt` snapshot updates are expected; a produced `.received.txt` fails the build and would indicate a mistake.

## Risks

- **Silent coverage loss through deletion**: mitigated by the read-body-first rule, the fold-asserts-into-survivor rule, and the disposition table.
- **Rename commit accidentally changing behavior**: mitigated by the zero-body-change rule; the commit 1 diff contains only signature lines, verifiable by inspection.
- **Determinism rewrites weakening the original intent**: each rewrite states the deterministic essence it preserves (see commit 3); reviewers can compare intent, not implementation.
