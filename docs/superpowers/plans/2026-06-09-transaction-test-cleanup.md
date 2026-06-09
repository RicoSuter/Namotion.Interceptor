# Transaction Test-Suite Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clean up the transaction test suite per issue #338 item 3: rename to the `When..._Then...` convention, remove redundant tests without losing coverage, make flaky tests deterministic, fix dishonest tests, and add the missing coverage.

**Architecture:** Test-only change across 12 files in `src/Namotion.Interceptor.Tracking.Tests/Transactions/` and `src/Namotion.Interceptor.Connectors.Tests/Transactions/`. Five commits, one per concern (rename, delete, determinism, honesty, new coverage), each independently green. No production code changes, no file moves.

**Tech Stack:** xUnit, Moq, .NET 9.0.

**Spec:** `docs/superpowers/specs/2026-06-09-transaction-test-cleanup-design.md`

**Important repo rules:**
- Commit messages: no Claude/AI attribution, no em dashes anywhere.
- Subagents must NOT commit; the orchestrating session makes each commit after reviewing the task result.
- Build has warnings-as-errors; an unused variable or using will fail the build.

**Verification commands (used after every task):**
```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.Tracking.Tests
dotnet test src/Namotion.Interceptor.Connectors.Tests
```
Expected: build clean, all tests pass, no `*.PublicApi.received.txt` files appear anywhere under `src/`.

**Key code-path fact used throughout:** `TransactionTestBase.CreateContext()` (Connectors) includes `WithSourceTransactions()`, so those tests commit through `CommitWithWriterAsync`; Tracking tests have no writer and commit through `CommitWithoutWriter`. A Connectors test is only deletable as a duplicate of a Tracking test if the asserted behavior runs BEFORE that divergence: change capture, pending reads, begin validation (nested check), `ValidateCanCommit` (already-committed, disposed), the empty-commit early return, `Dispose`, and lock acquire/release. Every deletion below is in that category. Commit-behavior tests (apply, notifications, reverts) are NOT deletable cross-project and none are deleted.

---

### Task 1: Commit 1, mechanical rename

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Transactions/SubjectPropertyChangeOperationsTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionAdditionalTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionAsyncTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionFailureHandlingTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionLifecycleTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionLocalPropertyTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionOptimisticLockingTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionPropertyTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionReconcileRegressionTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionRequirementTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionSourceTests.cs`

`src/Namotion.Interceptor.Tracking.Tests/Transactions/SubjectTransactionTests.cs` is already fully `When..._Then...`; do not touch it in this task.

Rules: rename ONLY the method identifier on the `public async Task` / `public void` line. Zero body changes, zero comment changes, no file moves. Tests renamed here that are deleted in later tasks still get renamed (keeps this commit purely mechanical). Before renaming a method whose body you have not read, skim the body; if the proposed name misstates what the body verifies, adjust the name to match the body (stay in `When<Condition>_Then<ExpectedBehavior>` form) and note the deviation in the task report.

- [ ] **Step 1: Apply the rename mapping**

`SubjectPropertyChangeOperationsTests.cs` (also fixes the stale `ApplyAllChanges` references; the method under test is `ApplyLocalChanges`):

| Old | New |
|---|---|
| `ApplyAllChanges_Span_NoExclude_AppliesAll` | `WhenApplyLocalChangesWithoutExclude_ThenAllChangesAreApplied` |
| `ApplyAllChanges_Span_WithExclude_SkipsExcluded` | `WhenApplyLocalChangesWithExclude_ThenExcludedChangesAreSkipped` |
| `ApplyAllChanges_Span_WithOutOfOrderExclude_SkipsExcludedViaHashSet` | `WhenExcludeIsOutOfOrder_ThenExcludedChangesAreStillSkipped` |
| `ApplyAllChanges_Span_WhenApplyFails_ReportsFailedAndErrors` | `WhenApplyFails_ThenFailedChangesAndErrorsAreReported` |
| `ApplyAllChanges_Span_WithExcludeAndApplyFailure_PartitionsCorrectly` | `WhenExcludeAndApplyFailureCombine_ThenChangesArePartitionedCorrectly` |

`SubjectTransactionAdditionalTests.cs`:

| Old | New |
|---|---|
| `CommitAsync_WithInfiniteTimeout_DoesNotTimeout` | `WhenCommitTimeoutIsInfinite_ThenSlowSourceCommitSucceeds` |
| `BeginTransactionAsync_DefaultTimeout_Is30Seconds` | `WhenNoTimeoutSpecified_ThenDefaultCommitTimeoutIs30Seconds` |
| `BeginTransactionAsync_WithCancelledToken_ThrowsOperationCanceledException` | `WhenBeginCalledWithCancelledToken_ThenThrows` |
| `Dispose_CalledMultipleTimes_IsIdempotent` | `WhenDisposeCalledMultipleTimes_ThenIsIdempotent` |
| `CommitAsync_WithNoChanges_CompletesImmediately` | `WhenCommittedWithNoChanges_ThenCompletesImmediately` |
| `HasActiveTransaction_TracksCorrectly` | `WhenTransactionBeginsAndEnds_ThenHasActiveTransactionTracksIt` |
| `TransactionException_IsPartialSuccess_ReturnsCorrectValue` | `WhenSomeChangesApplied_ThenIsPartialSuccessIsTrue` |
| `WriteProperty_DerivedProperty_NotCapturedInTransaction` | `WhenDerivedPropertyChangesDuringTransaction_ThenItIsNotCaptured` |
| `CommitAsync_CalledSecondTime_ThrowsWithClearMessage` | `WhenCommitCalledSecondTime_ThenThrowsAlreadyCommitted` |
| `WriteResult_IsPartialFailure_DistinguishesFromFullFailure` | `WhenWriteResultIsPartialFailure_ThenItIsDistinctFromFullFailure` |
| `WriteResult_Success_IsZeroAllocation` | `WhenWriteResultSuccessReused_ThenBothValuesAreFullySuccessful` |

`SubjectTransactionAsyncTests.cs`:

| Old | New |
|---|---|
| `BeginTransactionAsync_SerializesConcurrentTransactions` | `WhenExclusiveTransactionActive_ThenSecondBeginWaitsUntilFirstEnds` |
| `BeginTransactionAsync_ReturnsTransaction` | `WhenTransactionBegun_ThenItIsBoundToTheContext` |
| `BeginTransactionAsync_SerializesTransactionsPerContext` | `WhenExclusiveTransactionActive_ThenSecondBeginWaitsUntilDisposed` |
| `WriteProperty_ToDifferentContext_ThrowsInvalidOperationException` | `WhenWritingSubjectOfDifferentContext_ThenThrows` |
| `WriteProperty_ToSameContext_Succeeds` | `WhenWritingTwoSubjectsOfSameContext_ThenBothChangesAreCaptured` |
| `BeginTransactionAsync_WithConflictBehavior_StoresBehavior` | `WhenConflictBehaviorSpecified_ThenTransactionStoresIt` |
| `DisposeAsync_ReleasesLock_AllowsNewTransaction` | `WhenTransactionDisposed_ThenLockIsReleasedForNewTransaction` |
| `CommitAsync_WithFailOnConflict_ThrowsWhenValueChangedExternally` | `WhenValueChangedExternallyWithFailOnConflict_ThenCommitThrowsConflict` |
| `CommitAsync_WithIgnoreConflict_DoesNotThrowWhenValueChangedExternally` | `WhenValueChangedExternallyWithIgnoreConflict_ThenCommitSucceeds` |
| `CommitAsync_WithFailOnConflict_SucceedsWhenNoConflict` | `WhenNoExternalChangeWithFailOnConflict_ThenCommitSucceeds` |
| `CommitAsync_WithFailOnConflict_SucceedsWhenStartingFromNull` | `WhenOldValueIsNullAndUnchanged_ThenFailOnConflictCommitSucceeds` |
| `WriteProperty_CapturesOriginalOldValue_ForConflictDetection` | `WhenSamePropertyWrittenTwice_ThenOriginalOldValueIsKept` |
| `CommitAsync_AfterConflictFailure_CanRetrySuccessfully` | `WhenConflictResolvedExternally_ThenSameTransactionRetrySucceeds` |
| `CommitAsync_AfterConflictFailure_PendingChangesRemainIntact` | `WhenCommitFailsWithConflict_ThenPendingChangesRemainIntact` |
| `CommitAsync_AfterAlreadyCommitted_ThrowsInvalidOperation` | `WhenTransactionAlreadyCommitted_ThenSecondCommitThrows` |

`SubjectTransactionFailureHandlingTests.cs` (`WhenWriterThrows_...` and `WhenWriterThrowsDuringRevert_...` already conform; leave them):

| Old | New |
|---|---|
| `BestEffortMode_AppliesSuccessfulChanges_WhenSomeSourcesFail` | `WhenSomeSourcesFailInBestEffortMode_ThenSuccessfulChangesAreApplied` |
| `RollbackMode_AppliesAllChanges_WhenAllSourcesSucceed` | `WhenAllSourcesSucceedInRollbackMode_ThenAllChangesAreApplied` |
| `RollbackMode_RevertsSuccessfulSources_WhenAnySourceFails` | `WhenAnySourceFailsInRollbackMode_ThenSuccessfulSourceWritesAreReverted` |
| `RollbackMode_ReportsRevertFailures_WhenRevertAlsoFails` | `WhenRevertAlsoFailsInRollbackMode_ThenRevertFailuresAreReported` |
| `RollbackMode_ChangesWithoutSource_NotApplied_WhenSourceFails` | `WhenSourceFailsInRollbackMode_ThenNoSourceChangesAreNotApplied` |
| `BestEffortMode_ChangesWithoutSource_AlwaysApplied_WhenSourceFails` | `WhenSourceFailsInBestEffortMode_ThenNoSourceChangesAreStillApplied` |

`SubjectTransactionLifecycleTests.cs`:

| Old | New |
|---|---|
| `BeginTransaction_CreatesActiveTransaction` | `WhenTransactionBegun_ThenItBecomesCurrent` |
| `Dispose_CleansUpWithoutCommit_ImplicitRollback` | `WhenDisposedWithoutCommit_ThenPendingChangesAreDiscarded` |
| `BeginTransaction_WhenNested_ThrowsInvalidOperationException` | `WhenNestedTransactionAttempted_ThenThrows` |
| `CommitAsync_WithNoChanges_ReturnsImmediately` | `WhenCommittedWithNoChanges_ThenReturnsImmediately` |
| `CommitAsync_AppliesChangesToModel` | `WhenCommitted_ThenChangesAreAppliedToModel` |
| `CommitAsync_CleansUpPendingChanges` | `WhenCommitted_ThenPendingChangesAreCleared` |
| `CommitAsync_AfterDispose_ThrowsObjectDisposedException` | `WhenCommitCalledAfterDispose_ThenThrowsObjectDisposed` |
| `CommitAsync_WhenCalledTwice_ThrowsInvalidOperationException` | `WhenCommitCalledTwice_ThenThrows` |
| `Dispose_CalledMultipleTimes_IsIdempotent` | `WhenDisposeCalledMultipleTimes_ThenIsIdempotent` |
| `AsyncLocalBehavior_CurrentClearedAfterUsingBlock` | `WhenUsingBlockEnds_ThenCurrentTransactionIsCleared` |
| `InterceptorRegistration_TransactionBeforeObservable` | `WhenContextCreated_ThenTransactionInterceptorPrecedesObservable` |

`SubjectTransactionLocalPropertyTests.cs`:

| Old | New |
|---|---|
| `BestEffortMode_LocalPropertyThrows_AppliesSuccessfulChanges` | `WhenLocalPropertyThrowsInBestEffortMode_ThenSuccessfulChangesAreApplied` |
| `RollbackMode_LocalPropertyThrows_RevertsAllChanges` | `WhenLocalPropertyThrowsInRollbackMode_ThenAllChangesAreReverted` |
| `RollbackMode_LocalPropertyThrows_RevertsExternalSources` | `WhenLocalPropertyThrowsInRollbackMode_ThenExternalSourcesAreReverted` |
| `RollbackMode_LocalPropertyRevertThrows_ReportsMultipleFailures` | `WhenLocalRevertAlsoThrowsInRollbackMode_ThenMultipleFailuresAreReported` |
| `RollbackMode_LocalPropertyRevertThrows_ReportsForwardChangeNotInvertedRollback` | `WhenLocalRevertThrowsInRollbackMode_ThenForwardChangeIsReportedNotInvertedRollback` |
| `NoWriter_BestEffortMode_LocalPropertyThrows_AppliesSuccessfulChanges` | `WhenLocalPropertyThrowsInBestEffortModeWithoutWriter_ThenSuccessfulChangesAreApplied` |
| `NoWriter_RollbackMode_LocalPropertyThrows_RevertsSuccessfulChanges` | `WhenLocalPropertyThrowsInRollbackModeWithoutWriter_ThenSuccessfulChangesAreReverted` |
| `AllLocalPropertiesSucceed_NoException` | `WhenAllLocalPropertiesSucceed_ThenCommitSucceeds` |
| `MixedSourceAndLocal_SourceFails_LocalNotApplied_InRollbackMode` | `WhenSourceFailsWithMixedChangesInRollbackMode_ThenLocalChangesAreNotApplied` |
| `StagedExecution_ExternalSourcesWrittenBeforeLocalProperties` | `WhenCommitting_ThenExternalSourcesAreWrittenBeforeLocalApplies` |
| `StagedExecution_SourceBoundPropertyOnSetThrows_RevertsExternalSources` | `WhenSourceBoundApplyThrows_ThenExternalSourcesAreReverted` |
| `BestEffortMode_SourceBoundPropertyOnSetThrows_RevertsFailedSourceOnly` | `WhenSourceBoundApplyThrowsInBestEffortMode_ThenOnlyFailedSourceIsReverted` |
| `BestEffortMode_SourceBoundPropertyOnSetThrows_MaintainsPerPropertyConsistency` | `WhenSourceBoundApplyThrowsInBestEffortMode_ThenPerPropertyConsistencyIsMaintained` |
| `RollbackMode_SingleSourceWithLocal_SourceBoundApplyThrows_RevertsEverything` | `WhenSourceBoundApplyThrowsInRollbackModeWithSingleSource_ThenEverythingIsReverted` |
| `BestEffortMode_SingleSourceWithLocal_SourceBoundApplyThrows_KeepsSuccessfulAndRevertsFailedSource` | `WhenSourceBoundApplyThrowsInBestEffortModeWithSingleSource_ThenSuccessfulKeptAndFailedSourceReverted` |

`SubjectTransactionOptimisticLockingTests.cs`:

| Old | New |
|---|---|
| `OptimisticLocking_ConflictDetection_ThrowsWhenValueChangedExternally` | `WhenValueChangedExternallyInOptimisticTransaction_ThenCommitThrowsConflict` |
| `OptimisticLocking_AllowsConcurrentTransactionStart` | `WhenMultipleOptimisticTransactionsBegin_ThenNoneBlocks` |
| `ExclusiveLocking_BlocksOtherExclusiveTransactions` | `WhenExclusiveTransactionActive_ThenOtherExclusiveTransactionWaits` |
| `OptimisticLocking_WithConflictBehaviorIgnore_DoesNotThrowOnConflict` | `WhenConflictIgnoredInOptimisticTransaction_ThenCommitSucceeds` |
| `OptimisticLocking_TransactionHasCorrectLockingValue` | `WhenOptimisticTransactionBegun_ThenLockingIsOptimistic` |
| `ExclusiveLocking_TransactionHasCorrectLockingValue` | `WhenExclusiveTransactionBegun_ThenLockingIsExclusive` |
| `DefaultLocking_IsExclusive` | `WhenLockingNotSpecified_ThenDefaultIsExclusive` |
| `OptimisticLocking_DisposeCleansUpWithoutHoldingLock` | `WhenOptimisticTransactionDisposedWithoutCommit_ThenNothingIsAppliedAndNewTransactionCanBegin` |
| `OptimisticLocking_MultipleOptimisticCanCoexistInDifferentContexts` | `WhenMultipleOptimisticTransactionsInSeparateFlows_ThenAllCanCoexist` |
| `OptimisticLocking_CommitSerializesWithExclusive` | `WhenOptimisticCommitsWhileExclusiveActive_ThenCommitWaitsForExclusive` |
| `ExclusiveLocking_WaitsForOptimisticCommit` | `WhenOptimisticCommitInProgress_ThenExclusiveBeginWaits` |

`SubjectTransactionPropertyTests.cs` (the three `When..._Then...` methods already conform; leave them):

| Old | New |
|---|---|
| `WriteProperty_WhenTransactionActive_CapturesChange` | `WhenPropertyWrittenDuringTransaction_ThenChangeIsCaptured` |
| `WriteProperty_WhenNoTransaction_PassesThrough` | `WhenNoTransactionActive_ThenWritePassesThrough` |
| `WriteProperty_WhenIsCommittingTrue_PassesThrough` | `WhenCommitting_ThenApplyWritesPassThroughToModel` |
| `WriteProperty_DerivedPropertySkipped_NotCaptured` | `WhenDerivedPropertyChangesDuringTransaction_ThenOnlyBasePropertiesAreCaptured` |
| `WriteProperty_SamePropertyMultipleTimes_LastWriteWins` | `WhenSamePropertyWrittenMultipleTimes_ThenLastWriteWins` |
| `WriteProperty_PreservesChangeContext_SourceAndTimestamps` | `WhenChangeCapturedWithChangeContext_ThenSourceAndTimestampsArePreserved` |
| `CommitAsync_PreservesChangeContext_SourceAndTimestamps` | `WhenCommitted_ThenNotificationsPreserveSourceAndTimestamps` |
| `ReadProperty_WhenTransactionActive_ReturnsPendingValue` | `WhenReadingDuringTransaction_ThenPendingValueIsReturned` |
| `ReadProperty_WhenNoTransaction_ReturnsActualValue` | `WhenNoTransactionActive_ThenReadReturnsActualValue` |
| `ReadProperty_WhenNoPendingChange_ReturnsActualValue` | `WhenNoPendingChangeForProperty_ThenReadReturnsActualValue` |
| `ReadProperty_WhenIsCommittingTrue_ReturnsActualValue` | `WhenReadingDuringCommitNotification_ThenAppliedValueIsReturned` |
| `CommitAsync_FiresChangeNotifications` | `WhenCommitted_ThenChangeNotificationsFire` |
| `DisposeWithoutCommit_DoesNotFireChangeNotifications` | `WhenDisposedWithoutCommit_ThenNoChangeNotificationsFire` |
| `CommitAsync_WithMultipleSubjects_AppliesAllChanges` | `WhenMultipleSubjectsModified_ThenAllChangesAreApplied` |
| `Integration_DerivedPropertyUpdates_AfterCommit` | `WhenCommitted_ThenDerivedPropertyReflectsNewValues` |
| `CommitAsync_WithDependentProperties_CommitsInInsertionOrder` | `WhenDependentPropertiesCommitted_ThenInsertionOrderIsPreserved` |
| `CommitAsync_WithDependentProperties_ValidatorRejectsInvalidSpeedDuringCapture` | `WhenDependentPropertyInvalidDuringCapture_ThenValidatorRejectsWrite` |

`SubjectTransactionReconcileRegressionTests.cs`:

| Old | New |
|---|---|
| `BestEffortMode_WhenSourceWriteFailsAndAnotherApplyFails_KeepsSuccessAndRevertsOnlyAppliedThenFailed` | `WhenSourceWriteAndAnotherApplyFailInBestEffortMode_ThenSuccessIsKeptAndOnlyAppliedThenFailedIsReverted` |
| `OptimisticMode_WhenLockAcquireCancelled_TransactionRemainsRetryable` | `WhenOptimisticLockAcquireCancelled_ThenTransactionRemainsRetryable` |

`SubjectTransactionRequirementTests.cs`:

| Old | New |
|---|---|
| `SingleWriteRequirement_ThrowsException_WhenMultipleSources` | `WhenSingleWriteRequirementWithMultipleSources_ThenCommitFails` |
| `SingleWriteRequirement_ThrowsException_WhenChangesExceedBatchSize` | `WhenSingleWriteRequirementExceedsBatchSize_ThenCommitFails` |
| `SingleWriteRequirement_Succeeds_WhenRequirementsMet` | `WhenSingleWriteRequirementIsMet_ThenCommitSucceeds` |
| `SingleWriteRequirement_Succeeds_WithUnlimitedBatchSize` | `WhenSingleWriteRequirementWithUnlimitedBatchSize_ThenCommitSucceeds` |
| `SingleWriteRequirement_AllowsChangesWithoutSource` | `WhenSingleWriteRequirementWithNoSourceChanges_ThenCommitSucceeds` |
| `SingleWriteRequirement_AllowsMixedSourceAndNoSource` | `WhenSingleWriteRequirementWithMixedChanges_ThenCommitSucceeds` |
| `NoneRequirement_AllowsMultipleSources` | `WhenNoneRequirementWithMultipleSources_ThenCommitSucceeds` |
| `SingleWriteWithRollback_RevertsOnFailure_WithSingleSource` | `WhenSingleWriteRequirementFailsInRollbackMode_ThenSingleSourceIsReverted` |
| `SingleWriteRequirement_ValidationFailure_ReturnsChangesWithoutSourceAsSuccessful` | `WhenSingleWriteRequirementValidationFails_ThenNoSourceChangesAreReportedSuccessful` |
| `SingleWriteRequirement_BatchSizeViolation_ReturnsChangesWithoutSourceAsSuccessful` | `WhenBatchSizeValidationFails_ThenNoSourceChangesAreReportedSuccessful` |

`SubjectTransactionSourceTests.cs`:

| Old | New |
|---|---|
| `SetSource_StoresSourceReference` | `WhenSetSourceCalled_ThenSourceReferenceIsStored` |
| `TryGetSource_WhenNoSourceSet_ReturnsFalse` | `WhenNoSourceSet_ThenTryGetSourceReturnsFalse` |
| `SetSource_WhenCalledWithDifferentSource_ReturnsFalse` | `WhenSetSourceCalledWithDifferentSource_ThenReturnsFalse` |
| `SetSource_WhenCalledWithSameSource_ReturnsTrue` | `WhenSetSourceCalledWithSameSource_ThenReturnsTrue` |
| `RemoveSource_WithMatchingSource_ClearsSourceReference` | `WhenRemoveSourceCalledWithMatchingSource_ThenSourceReferenceIsCleared` |
| `RemoveSource_WithDifferentSource_DoesNotClearSourceReference` | `WhenRemoveSourceCalledWithDifferentSource_ThenSourceReferenceIsKept` |
| `CommitAsync_WithSourceBoundProperty_WritesToSource` | `WhenSourceBoundPropertyCommitted_ThenSourceIsWritten` |
| `CommitAsync_WithSourceWriteFailure_ThrowsTransactionException` | `WhenSourceWriteFails_ThenCommitThrowsTransactionException` |
| `CommitAsync_WithMixedSourceAndLocal_InBestEffortMode_AppliesLocalAndSuccessfulSource` | `WhenMixedChangesCommittedInBestEffortMode_ThenLocalAndSuccessfulSourceChangesAreApplied` |
| `CommitAsync_WithMultipleSources_GroupsBySource` | `WhenMultipleSourcesCommitted_ThenChangesAreGroupedBySource` |
| `CommitAsync_WithMultipleSourcesAndLocal_WritesSourcesAndAppliesLocal` | `WhenMultipleSourcesAndLocalCommitted_ThenSourcesAreWrittenAndLocalApplied` |
| `CommitAsync_WithSingleSourceAndLocal_WritesOnlySourceBoundAndAppliesLocal` | `WhenSingleSourceAndLocalCommitted_ThenOnlySourceBoundChangesAreWrittenToSource` |
| `CommitAsync_UserCancellationIsIgnored_CommitSucceeds` | `WhenUserTokenCancelledDuringCommit_ThenCommitStillSucceeds` |
| `CommitAsync_WithCommitTimeout_TimesOutAndFails` | `WhenSourceWriteExceedsCommitTimeout_ThenCommitFails` |
| `Integration_WithMockSource_VerifyWriteChangesAsyncCalled` | `WhenSourceBoundPropertyCommitted_ThenWriteChangesAsyncIsCalledOnce` |
| `CommitAsync_WithMultipleContexts_ResolvesCallbacksPerContext` | `WhenSubjectsFromMultipleContextsCommitted_ThenCallbacksResolvePerContext` |
| `CommitAsync_WithPartialWriteFailure_ReportsOnlyFailedChanges` | `WhenSourcePartiallyFails_ThenOnlyFailedChangesAreReported` |
| `CommitAsync_WithPartialWriteFailure_InRollbackMode_RevertsSuccessfulChanges` | `WhenSourcePartiallyFailsInRollbackMode_ThenSuccessfulChangesAreReverted` |
| `RollbackMode_WhenSourceRemovedDuringWrite_StillRevertsToOriginalSource` | `WhenSourceRemovedDuringWriteInRollbackMode_ThenRevertStillTargetsOriginalSource` |
| `Dispose_DuringInFlightCommit_DoesNotReturnLiveBufferToPool` | `WhenDisposedDuringInFlightCommit_ThenLiveBufferIsNotReturnedToPool` |
| `Dispose_DuringInFlightExclusiveCommit_ReleasesLockAfterCommitCompletes` | `WhenDisposedDuringInFlightExclusiveCommit_ThenLockIsReleasedAfterCommitCompletes` |
| `CommitAsync_MultiSource_SourceReappearsAfterSwitch_PreservesGroupingAndOrder` | `WhenSourceReappearsAfterSwitch_ThenGroupingAndOrderArePreserved` |
| `CommitAsync_MultiSource_LateSwitchWithInterleavedLocals_GroupsSourceBoundOnly` | `WhenLateSourceSwitchWithInterleavedLocals_ThenOnlySourceBoundChangesAreGrouped` |
| `CommitAsync_SingleSource_WithTrailingLocals_WritesExactlySourceBound` | `WhenSingleSourceWithTrailingLocals_ThenExactlySourceBoundChangesAreWritten` |

- [ ] **Step 2: Verify the diff is signature-only**

Run: `git diff --stat && git diff | grep "^[+-]" | grep -v "^[+-][+-]" | grep -cv "public \(async \)\?\(Task\|void\|ValueTask\)"`
Expected: 11 files changed; the count of changed lines that are NOT method signature lines is 0.

- [ ] **Step 3: Build and test**

Run the three verification commands. Expected: clean build, 339 Tracking + 423 Connectors tests pass (totals unchanged; only names differ).

- [ ] **Step 4: Commit**

```bash
git add -A src/
git commit -m "Rename transaction tests to the When/Then convention (#338)"
```

---

### Task 2: Commit 2, deletions with disposition table

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Transactions/SubjectTransactionTests.cs` (folds into survivors)
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionAdditionalTests.cs` (deletions)
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionAsyncTests.cs` (deletions)
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionLifecycleTests.cs` (deletions)
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionOptimisticLockingTests.cs` (deletions)
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionPropertyTests.cs` (deletion)

All names below are the post-Task-1 names. Apply the folds (Step 1) BEFORE the deletions (Step 2) so coverage never drops between edits.

- [ ] **Step 1: Fold unique assertions into the Tracking survivors**

In `SubjectTransactionTests.cs` (Tracking), replace these four test bodies:

`WhenTransactionAlreadyCommitted_ThenCommitAgainThrows` gains the message assert from the deleted Additional/Lifecycle copies:

```csharp
[Fact]
public async Task WhenTransactionAlreadyCommitted_ThenCommitAgainThrows()
{
    // Arrange
    var context = CreateTransactionContext();

    using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
    {
        await transaction.CommitAsync(CancellationToken.None);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        Assert.Contains("already been committed", exception.Message);
    }
}
```

`WhenDisposeCalledMultipleTimes_ThenIsIdempotent` gains the Current-slot asserts from the deleted copies:

```csharp
[Fact]
public async Task WhenDisposeCalledMultipleTimes_ThenIsIdempotent()
{
    // Arrange
    var context = CreateTransactionContext();
    var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
    Assert.Same(transaction, SubjectTransaction.Current);

    // Act & Assert
    transaction.Dispose();
    Assert.Null(SubjectTransaction.Current);

    transaction.Dispose();
    Assert.Null(SubjectTransaction.Current);
}
```

`WhenCommittedWithNoChanges_ThenSucceeds` gains a deterministic completes-synchronously assert (replaces the deleted stopwatch test's "completes immediately" intent; valid because the no-writer empty commit returns a completed ValueTask without awaiting) and the pending-empty assert from the deleted Lifecycle copy:

```csharp
[Fact]
public async Task WhenCommittedWithNoChanges_ThenSucceeds()
{
    // Arrange
    var context = CreateTransactionContext();

    // Act & Assert
    using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
    {
        var commitTask = transaction.CommitAsync(CancellationToken.None);
        Assert.True(commitTask.IsCompletedSuccessfully, "Empty commit should complete synchronously.");
        await commitTask;
        Assert.Empty(transaction.GetPendingChanges());
    }
}
```

`WhenNestedTransactionAttempted_ThenThrows` gains the message assert from the deleted Lifecycle copy:

```csharp
[Fact]
public async Task WhenNestedTransactionAttempted_ThenThrows()
{
    // Arrange
    var context = CreateTransactionContext();

    using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        });
        Assert.Contains("Nested transactions are not supported", exception.Message);
    }
}
```

`WhenConflictDetected_ThenConflictExceptionThrown` gains the message assert from the deleted OptimisticLocking copy: inside the existing `// Act & Assert` block, after `Assert.Equal(nameof(Person.FirstName), ex.ConflictingProperties[0].Name);` add:

```csharp
Assert.Contains(nameof(Person.FirstName), ex.Message);
```

- [ ] **Step 2: Delete the redundant tests**

Disposition table (every deleted test's coverage survives in the named test; the safety argument is that each asserted behavior runs before the writer/no-writer code-path divergence, see header):

| # | Delete (Connectors) | Survivor | Why safe |
|---|---|---|---|
| 1 | `AdditionalTests.WhenCommitCalledSecondTime_ThenThrowsAlreadyCommitted` | Tracking `WhenTransactionAlreadyCommitted_ThenCommitAgainThrows` | already-committed check in `ValidateCanCommit`, pre-divergence; message assert folded |
| 2 | `AsyncTests.WhenTransactionAlreadyCommitted_ThenSecondCommitThrows` | same | same |
| 3 | `LifecycleTests.WhenCommitCalledTwice_ThenThrows` | same | same |
| 4 | `AdditionalTests.WhenDisposeCalledMultipleTimes_ThenIsIdempotent` | Tracking `WhenDisposeCalledMultipleTimes_ThenIsIdempotent` | `Dispose` is writer-independent; Current asserts folded |
| 5 | `LifecycleTests.WhenDisposeCalledMultipleTimes_ThenIsIdempotent` | same | same |
| 6 | `AdditionalTests.WhenCommittedWithNoChanges_ThenCompletesImmediately` | Tracking `WhenCommittedWithNoChanges_ThenSucceeds` | empty commit early-returns before writer selection; sync-completion assert folded (also removes the stopwatch flake) |
| 7 | `LifecycleTests.WhenCommittedWithNoChanges_ThenReturnsImmediately` | same | same; pending-empty assert folded |
| 8 | `LifecycleTests.WhenCommitCalledAfterDispose_ThenThrowsObjectDisposed` | Tracking `WhenTransactionDisposed_ThenCommitThrows` | disposed check in `ValidateCanCommit`, pre-divergence |
| 9 | `AsyncTests.WhenTransactionDisposed_ThenLockIsReleasedForNewTransaction` | Tracking `WhenExclusiveTransactionDisposed_ThenLockIsReleased` | lock release on dispose, no commit involved |
| 10 | `AsyncTests.WhenExclusiveTransactionActive_ThenSecondBeginWaitsUntilDisposed` | same-file `WhenExclusiveTransactionActive_ThenSecondBeginWaitsUntilFirstEnds` | same file, same scenario (tx1 holds exclusive lock, suppressed-flow tx2 waits, completes after release); context-binding assert covered by `WhenTransactionBegun_ThenItIsBoundToTheContext` |
| 11 | `AsyncTests.WhenSamePropertyWrittenTwice_ThenOriginalOldValueIsKept` | Tracking `WhenSamePropertyWrittenTwice_ThenLastWriteWinsAndOriginalOldValuePreserved` | capture path only (no commit), identical asserts (old=original, new=last, single pending) |
| 12 | `PropertyTests.WhenSamePropertyWrittenMultipleTimes_ThenLastWriteWins` | same | same capture-path branch; a third write exercises the same subsequent-write code as the second |
| 13 | `LifecycleTests.WhenNestedTransactionAttempted_ThenThrows` | Tracking `WhenNestedTransactionAttempted_ThenThrows` | nested check at top of `BeginTransactionAsync`, pre-everything; message assert folded |
| 14 | `OptimisticLockingTests.WhenValueChangedExternallyInOptimisticTransaction_ThenCommitThrowsConflict` | Tracking `WhenConflictDetected_ThenConflictExceptionThrown` | both writer-less optimistic FailOnConflict; survivor asserts strictly more; message assert folded |
| 15 | `OptimisticLockingTests.WhenConflictIgnoredInOptimisticTransaction_ThenCommitSucceeds` | Tracking `WhenConflictBehaviorIsIgnore_ThenNoConflictException` | both writer-less optimistic Ignore, identical scenario and asserts |
| 16 | `OptimisticLockingTests.WhenMultipleOptimisticTransactionsInSeparateFlows_ThenAllCanCoexist` | `WhenMultipleOptimisticTransactionsBegin_ThenNoneBlocks` (rewritten in Task 3) | identical scenario (N suppressed-flow optimistic begins, hold, dispose); only N differs; its Locking assert is folded into the Task 3 rewrite |
| 17 | `AdditionalTests.WhenDerivedPropertyChangesDuringTransaction_ThenItIsNotCaptured` | `PropertyTests.WhenDerivedPropertyChangesDuringTransaction_ThenOnlyBasePropertiesAreCaptured` | same project, same base-class context, same capture-path scenario; survivor additionally reads `FullName` to provoke the derived path and asserts `IsDerived` on all pending changes |

Note items 10 to 17 were discovered during planning and go beyond the issue's five groups; same deletion rule applied (read body, verify survivor covers everything, fold what does not).

Deletion rule, mandatory: before deleting each test, read its body one last time and diff its asserts against the survivor's. If you find an assert the survivor lacks and the table above does not list it as folded, STOP and fold it into the survivor first.

- [ ] **Step 3: Build and test**

Run the three verification commands. Expected: clean build, all green. Test count drops by exactly 17 (Tracking count unchanged at 339, Connectors 423 -> 406).

- [ ] **Step 4: Commit** (include the disposition table in the commit message body)

```bash
git add -A src/
git commit -m "Remove redundant transaction tests and fold unique asserts into survivors (#338)"
```

---

### Task 3: Commit 3, determinism fixes

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionOptimisticLockingTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionSourceTests.cs`

- [ ] **Step 1: Rewrite the flaky optimistic-begin test deterministically**

In `SubjectTransactionOptimisticLockingTests.cs`, replace the entire body of `WhenMultipleOptimisticTransactionsBegin_ThenNoneBlocks` (the old body measured start-time spread < 200ms and used `Task.Delay(50)`) with:

```csharp
[Fact]
public async Task WhenMultipleOptimisticTransactionsBegin_ThenNoneBlocks()
{
    // Arrange
    var context = CreateContext();
    var allBegan = new CountdownEvent(5);
    var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var lockingModes = new List<TransactionLocking>();
    var tasks = new List<Task>();

    // Act: each flow begins an optimistic transaction and holds it open until all five have begun.
    for (var i = 0; i < 5; i++)
    {
        Task task;
        using (ExecutionContext.SuppressFlow())
        {
            task = Task.Run(async () =>
            {
                var transaction = await context.BeginTransactionAsync(
                    TransactionFailureHandling.BestEffort,
                    TransactionLocking.Optimistic);
                lock (lockingModes)
                {
                    lockingModes.Add(transaction.Locking);
                }
                allBegan.Signal();
                await release.Task;
                transaction.Dispose();
            });
        }
        tasks.Add(task);
    }

    // Assert: the countdown reaches zero only if every begin completed while the other four
    // transactions were still open; if optimistic begin took the per-context lock, it never would.
    Assert.True(allBegan.Wait(TimeSpan.FromSeconds(10)),
        "Optimistic transactions blocked each other at begin.");
    release.SetResult(true);
    await Task.WhenAll(tasks);
    Assert.All(lockingModes, locking => Assert.Equal(TransactionLocking.Optimistic, locking));
}
```

The `lockingModes` assert preserves the Locking check folded from the test deleted as item 16 in Task 2. The 10-second `Wait` is a failure timeout (fails loudly), not a pacing delay; this is the `CountdownEvent` pattern CLAUDE.md prescribes.

- [ ] **Step 2: Replace the simulated-hang delay in the timeout test**

In `SubjectTransactionSourceTests.cs`, inside `WhenSourceWriteExceedsCommitTimeout_ThenCommitFails`, change:

```csharp
await Task.Delay(TimeSpan.FromSeconds(10), ct);
```
to:
```csharp
await Task.Delay(Timeout.Infinite, ct);
```

The hang is only ever ended by the commit-timeout cancellation; `Timeout.Infinite` states that intent and cannot wake spuriously on a slow CI runner.

- [ ] **Step 3: Replace the WhenAny completion guard**

In `SubjectTransactionSourceTests.cs`, replace the `WaitWithTimeout` helper:

```csharp
private static async Task<bool> WaitWithTimeout(Task task, TimeSpan timeout)
{
    try
    {
        await task.WaitAsync(timeout).ConfigureAwait(false);
        return true;
    }
    catch (TimeoutException)
    {
        return false;
    }
}
```

Behavior note: a faulted task now surfaces its exception at the `WaitWithTimeout` call site instead of returning true and deferring observation; that makes failures in `WhenDisposedDuringInFlightExclusiveCommit_ThenLockIsReleasedAfterCommitCompletes` point at the actual exception. The bounded negative check (`Assert.False(await WaitWithTimeout(bBegan.Task, TimeSpan.FromMilliseconds(200)), ...)`) stays as is: it observes a non-occurrence within a window and is documented in the test as unable to false-fail.

- [ ] **Step 4: Build and test**

Run the three verification commands. Expected: clean build, all green, counts unchanged from Task 2 (339 + 406).

- [ ] **Step 5: Commit**

```bash
git add -A src/
git commit -m "Make optimistic-locking and timeout transaction tests deterministic (#338)"
```

---

### Task 4: Commit 4, honesty fixes

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionLifecycleTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionAdditionalTests.cs`

The misleading retry test was already renamed honestly in Task 1 (`WhenConflictResolvedExternally_ThenSameTransactionRetrySucceeds`); its true-retry companion is added in Task 5. Nothing further here.

- [ ] **Step 1: Delete the interceptor-order test**

Delete `WhenContextCreated_ThenTransactionInterceptorPrecedesObservable` from `SubjectTransactionLifecycleTests.cs` (matches interceptor order via `GetType().Name` string comparison, an implementation detail). Behavioral coverage of that ordering is confirmed present: writes during an active transaction fire no notifications and notifications fire on commit, asserted in the same project and context by `PropertyTests.WhenTransactionActive_ThenPropertyChangedNotFired` and `PropertyTests.WhenCommitted_ThenChangeNotificationsFire`, and in Tracking by `WhenTransactionCommitted_ThenObservableNotificationsFire` / `WhenTransactionDisposedWithoutCommit_ThenNoObservableNotificationsFire`. If `Namotion.Interceptor.Connectors.Transactions` or `Interceptors` usings become unused after the deletion, remove them (warnings are errors).

- [ ] **Step 2: Delete the fake zero-allocation test**

Delete `WhenWriteResultSuccessReused_ThenBothValuesAreFullySuccessful` (pre-rename: `WriteResult_Success_IsZeroAllocation`) from `SubjectTransactionAdditionalTests.cs`. It asserts nothing about allocation; `WriteResult.Success` semantics (IsFullySuccessful, not partial, empty FailedChanges) remain covered by `WhenWriteResultIsPartialFailure_ThenItIsDistinctFromFullFailure` plus every success-path commit test; allocation claims belong to `Namotion.Interceptor.Benchmarks`.

- [ ] **Step 3: Build and test**

Run the three verification commands. Expected: clean build, all green, Connectors 406 -> 404.

- [ ] **Step 4: Commit** (state both deletions and the surviving coverage in the message body)

```bash
git add -A src/
git commit -m "Remove implementation-detail transaction tests (#338)"
```

---

### Task 5: Commit 5, new coverage

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionFailureHandlingTests.cs` (tests 1-4 below)
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionAsyncTests.cs` (test 5)

These tests document existing behavior, so they are expected to pass on first run. If one fails, do NOT loosen the assert to make it pass: the point of this task is pinning exact contents. Diagnose against `SubjectTransaction.ReconcileWithWriterAsync` and report the discrepancy.

- [ ] **Step 1: Add the error-content helper to `SubjectTransactionFailureHandlingTests`**

Source failures are wrapped (e.g. `SourceTransactionWriteException`), so content asserts check the message chain:

```csharp
private static bool ErrorMentions(Exception exception, string text)
    => exception.Message.Contains(text) || exception.InnerException?.Message.Contains(text) == true;
```

- [ ] **Step 2: Add test 1, multi-stage rollback content asserts (source write fails + revert of the other source fails)**

```csharp
[Fact]
public async Task WhenSourceAndRevertFailInRollbackMode_ThenFailedChangesAndErrorsListExactContents()
{
    // Arrange: FirstName's source write succeeds but its revert fails; LastName's source write fails.
    var context = CreateContext();
    var person = new Person(context);

    var callCount = 0;
    var failingRevertSource = new Mock<ISubjectSource>();
    failingRevertSource.Setup(s => s.WriteBatchSize).Returns(0);
    failingRevertSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
        .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
        {
            callCount++;
            return callCount == 1
                ? new ValueTask<WriteResult>(WriteResult.Success)
                : new ValueTask<WriteResult>(WriteResult.Failure(changes, new InvalidOperationException("Revert boom")));
        });

    var failSource = CreateFailingSource("Write boom");

    new PropertyReference(person, nameof(Person.FirstName)).SetSource(failingRevertSource.Object);
    new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

    using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
    person.FirstName = "John";
    person.LastName = "Doe";

    // Act
    var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
        () => transaction.CommitAsync(CancellationToken.None).AsTask());

    // Assert: exact properties and error contents, not just counts.
    Assert.Empty(exception.AppliedChanges);
    Assert.Equal(2, exception.FailedChanges.Count);
    Assert.Contains(exception.FailedChanges, change => change.Property.Name == nameof(Person.FirstName)); // revert failed
    Assert.Contains(exception.FailedChanges, change => change.Property.Name == nameof(Person.LastName));  // source write failed
    Assert.Equal(2, exception.Errors.Count);
    Assert.Contains(exception.Errors, error => ErrorMentions(error, "Write boom"));
    Assert.Contains(exception.Errors, error => ErrorMentions(error, "Revert boom"));
    Assert.Null(person.FirstName); // local model untouched (Rollback applies nothing on source failure)
    Assert.Null(person.LastName);
}
```

- [ ] **Step 3: Add test 2, apply failure + local revert failure with a source in play**

Pins the exact `FailedChanges` contents for the deepest rollback flow, including that a successfully source-reverted change is NOT reported as failed (the documented exclusion of `written` changes):

```csharp
[Fact]
public async Task WhenApplyAndLocalRevertFailInRollbackMode_ThenFailedChangesListEveryStuckProperty()
{
    // Arrange: FirstName succeeds at its source (and must be source-reverted later);
    // PropertyB fails its local apply, which triggers rollback; PropertyA applies, then
    // fails its local revert (sequence-based: apply is its first setter call, revert the second).
    var context = CreateContext();
    var person = new Person(context);
    var propertyACalls = 0;
    var device = new ThrowingDevice(context);
    device.ShouldThrow = property =>
    {
        if (property == nameof(ThrowingDevice.PropertyB))
        {
            return true;
        }
        propertyACalls++;
        return propertyACalls > 1;
    };

    var writeCount = 0;
    var revertedSource = new Mock<ISubjectSource>();
    revertedSource.Setup(s => s.WriteBatchSize).Returns(0);
    revertedSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
        .Callback(() => writeCount++)
        .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            new ValueTask<WriteResult>(WriteResult.Success));
    new PropertyReference(person, nameof(Person.FirstName)).SetSource(revertedSource.Object);

    using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
    person.FirstName = "John";
    device.PropertyA = true;
    device.PropertyB = true;
    device.ThrowingEnabled = true;

    // Act
    var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
        () => transaction.CommitAsync(CancellationToken.None).AsTask());

    // Assert
    Assert.Empty(exception.AppliedChanges);
    Assert.Equal(2, exception.FailedChanges.Count);
    Assert.Contains(exception.FailedChanges, change => change.Property.Name == nameof(ThrowingDevice.PropertyB)); // failed apply
    Assert.Contains(exception.FailedChanges, change => change.Property.Name == nameof(ThrowingDevice.PropertyA)); // failed local revert
    // FirstName was written to its source and successfully reverted there; it is intentionally
    // NOT reported in FailedChanges (the written set is excluded from rolled-back locals).
    Assert.DoesNotContain(exception.FailedChanges, change => change.Property.Name == nameof(Person.FirstName));
    Assert.Equal(2, exception.Errors.Count); // PropertyB apply error + PropertyA revert error
    Assert.Equal(2, writeCount);             // source written, then reverted
    Assert.True(device.PropertyA);            // stuck: local revert failed, applied value remains
    Assert.False(device.PropertyB);           // never applied
    Assert.Null(person.FirstName);            // local apply rolled back
}
```

- [ ] **Step 4: Add test 3, optimistic locking + writer + source failure**

```csharp
[Fact]
public async Task WhenSourceFailsInOptimisticTransaction_ThenCommitFailsTerminallyAndLockIsReleased()
{
    // Arrange
    var context = CreateContext();
    var person = new Person(context);
    var failSource = CreateFailingSource("Source boom");
    new PropertyReference(person, nameof(Person.FirstName)).SetSource(failSource.Object);

    var transaction = await context.BeginTransactionAsync(
        TransactionFailureHandling.Rollback, TransactionLocking.Optimistic);
    person.FirstName = "John";

    // Act
    var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
        () => transaction.CommitAsync(CancellationToken.None).AsTask());

    // Assert
    Assert.Null(person.FirstName);
    Assert.Empty(exception.AppliedChanges);
    var failed = Assert.Single(exception.FailedChanges);
    Assert.Equal(nameof(Person.FirstName), failed.Property.Name);
    Assert.Contains(exception.Errors, error => ErrorMentions(error, "Source boom"));

    // Terminal: a second commit is rejected.
    var second = await Assert.ThrowsAsync<InvalidOperationException>(
        () => transaction.CommitAsync(CancellationToken.None).AsTask());
    Assert.Contains("already been committed", second.Message);
    transaction.Dispose();

    // The optimistic commit lock was released: a new transaction can begin and commit.
    using var nextTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
    person.LastName = "Doe";
    await nextTransaction.CommitAsync(CancellationToken.None);
    Assert.Equal("Doe", person.LastName);
}
```

- [ ] **Step 5: Add test 4, Errors-to-FailedChanges cardinality**

```csharp
[Fact]
public async Task WhenSourceBatchOfTwoChangesFails_ThenTwoFailedChangesShareOneError()
{
    // Arrange: one source, two properties; the source fails the whole batch with one exception.
    var context = CreateContext();
    var person = new Person(context);
    var failSource = CreateFailingSource("Batch boom");
    new PropertyReference(person, nameof(Person.FirstName)).SetSource(failSource.Object);
    new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

    using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
    person.FirstName = "John";
    person.LastName = "Doe";

    // Act
    var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
        () => transaction.CommitAsync(CancellationToken.None).AsTask());

    // Assert: FailedChanges is per change, Errors is per failed source batch.
    Assert.Equal(2, exception.FailedChanges.Count);
    Assert.Contains(exception.FailedChanges, change => change.Property.Name == nameof(Person.FirstName));
    Assert.Contains(exception.FailedChanges, change => change.Property.Name == nameof(Person.LastName));
    var error = Assert.Single(exception.Errors);
    Assert.True(ErrorMentions(error, "Batch boom"));
}
```

- [ ] **Step 6: Add test 5, true retry-through-conflict, to `SubjectTransactionAsyncTests.cs`**

Place it directly after `WhenConflictResolvedExternally_ThenSameTransactionRetrySucceeds` (its companion):

```csharp
[Fact]
public async Task WhenConflictPersists_ThenRetryFailsAgainWithConflict()
{
    // Arrange
    var context = InterceptorSubjectContext
        .Create()
        .WithRegistry()
        .WithTransactions()
        .WithFullPropertyTracking();

    var person = new Person(context);
    person.FirstName = "Original";

    using var transaction = await context.BeginTransactionAsync(
        TransactionFailureHandling.BestEffort,
        conflictBehavior: TransactionConflictBehavior.FailOnConflict);

    person.FirstName = "TransactionValue";

    // External change that is never resolved.
    Task externalTask;
    var asyncFlowControl = ExecutionContext.SuppressFlow();
    try
    {
        externalTask = Task.Run(() => person.FirstName = "ExternalChange");
    }
    finally
    {
        asyncFlowControl.Undo();
    }
    await externalTask;

    // Act & Assert: first commit fails with a conflict and stays retryable.
    await Assert.ThrowsAsync<SubjectTransactionConflictException>(
        () => transaction.CommitAsync(CancellationToken.None).AsTask());
    Assert.Single(transaction.GetPendingChanges());

    // The retry detects the same unresolved conflict and fails the same way; pending changes survive.
    await Assert.ThrowsAsync<SubjectTransactionConflictException>(
        () => transaction.CommitAsync(CancellationToken.None).AsTask());
    Assert.Single(transaction.GetPendingChanges());
}
```

- [ ] **Step 7: Build and run the new tests first, then everything**

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~WhenSourceAndRevertFailInRollbackMode|FullyQualifiedName~WhenApplyAndLocalRevertFail|FullyQualifiedName~WhenSourceFailsInOptimisticTransaction|FullyQualifiedName~WhenSourceBatchOfTwoChangesFails|FullyQualifiedName~WhenConflictPersists"
```
Expected: 5 passed. Then run the three verification commands. Expected: clean build, all green, Connectors 404 -> 409.

- [ ] **Step 8: Commit**

```bash
git add -A src/
git commit -m "Add transaction failure-content, optimistic-writer, and retry coverage (#338)"
```

---

### Task 6: Final verification and PR

- [ ] **Step 1: Full-solution verification**

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```
Expected: clean build, all unit tests green across the whole solution. Confirm no `*.PublicApi.received.txt` exists: `find src -name "*.received.txt"` returns nothing.

- [ ] **Step 2: Remove the temporary design docs from the branch** (repo pattern from #338: superpowers specs/plans do not ship)

```bash
git rm docs/superpowers/specs/2026-06-09-transaction-test-cleanup-design.md docs/superpowers/plans/2026-06-09-transaction-test-cleanup.md
git commit -m "Remove design spec and plan docs from PR (#338)"
```

- [ ] **Step 3: Push and open the PR**

```bash
git push -u origin transaction-test-cleanup
```

PR title: `Clean up the transaction test suite (#338)`. Body must include: summary of the five commits (rename / delete / determinism / honesty / new coverage), the full 19-row disposition table (17 from Task 2, 2 from Task 4) mapping every deleted test to its surviving coverage, the note that commits are reviewable independently and the suite is green after each, and a test plan section. No AI attribution, no em dashes.

---

## Self-Review (completed during planning)

- **Spec coverage:** rename (Task 1), de-dupe with disposition (Task 2), determinism (Task 3), honesty (Task 4), coverage gaps incl. retry companion (Task 5), verification and PR (Task 6). All spec sections mapped.
- **Type consistency:** `ErrorMentions` defined in Task 5 Step 1, used in Steps 2, 4, 5 (same file); test 5 lives in AsyncTests and does not use it. `ThrowingDevice` (`ShouldThrow`, `ThrowingEnabled`, `PropertyA/B`) matches `src/Namotion.Interceptor.Connectors.Tests/Models/ThrowingDevice.cs`. `SourceRevertResult`/`WriteResult` usages match their record definitions.
- **Expected-count math:** Task 2 deletes 17 (3+2+2+1+1 issue groups = 9, plus 8 discovered... recount: items 1-9 are the issue's five groups = 9 tests; items 10-17 = 8 more; total 17). Connectors 423-17=406; Task 4: 406-2=404; Task 5: 404+5=409. Tracking stays 339 throughout (folds replace bodies, no adds/removes).
