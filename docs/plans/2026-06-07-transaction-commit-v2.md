# Transaction Commit v2 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the fragmented transaction-commit paths with a single unified flow where the writer only does external source I/O and the transaction owns all in-process apply and rollback, making the commit simpler and faster (especially the source+local case) with no behavior change.

**Architecture:** `SubjectTransaction.CommitAsync` writes source-bound changes via the writer, then applies `snapshot - sourceWriteFailures` in one pass, then reconciles failures in one place. The writer (`SourceTransactionWriter`) becomes mechanical: classify, write, report, revert. See `docs/plans/2026-06-07-transaction-commit-v2-design.md` for the full design and the failure/rollback matrix.

**Tech Stack:** .NET 9 / C# 13, xUnit, Moq, BenchmarkDotNet. Solution: `src/Namotion.Interceptor.slnx`.

**Ground rules:**
- The existing **410 connector + 332 tracking tests are the behavioral oracle**. They must stay green. A red test means behavior changed; investigate before proceeding (do not "fix" by changing the test unless the test encoded an implementation detail, not behavior).
- Run unit tests with `dotnet test <project> --filter "Category!=Integration"`.
- Commit after every green step. No `Co-Authored-By` / AI attribution in messages (repo rule). No em dashes in code/docs.
- Branch: `feature/transaction-commit-v2` (already cut from #335 with the pool + design doc committed).

---

### Task 1: Add a span-based, exclude-aware apply primitive

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectPropertyChangeExtensions.cs`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Transactions/SubjectPropertyChangeExtensionsTests.cs` (create if absent)

**Step 1: Write failing tests**

```csharp
// applies every change when no exclude set; returns input-equivalent successful set
[Fact]
public void ApplyAllChanges_Span_NoExclude_AppliesAll() { /* arrange 3 changes on a tracked subject; act; assert all applied, no failures */ }

// excludes the given changes (by Property) from application
[Fact]
public void ApplyAllChanges_Span_WithExclude_SkipsExcluded() { /* exclude 1 of 3; assert only 2 applied, excluded one untouched */ }
```

**Step 2:** Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~ApplyAllChanges_Span"` -> FAIL (overload missing).

**Step 3: Implement** a `ReadOnlySpan<SubjectPropertyChange>` overload with an optional `IReadOnlyList<SubjectPropertyChange>? exclude` (matched by `Property` equality, two-pointer if exclude is an in-order subsequence, else `HashSet`). Lazy: allocate failed/error lists only on failure; on full success return the applied set. Keep the existing list overload for now.

**Step 4:** Run the same filter -> PASS. Then run the whole tracking suite -> PASS (no regressions).

**Step 5: Commit** `feat: add span/exclude overload to ApplyAllChanges`.

---

### Task 2: Define the new writer contract (additive, not yet wired)

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/ITransactionWriter.cs`
- Create: `src/Namotion.Interceptor.Tracking/Transactions/SourceWriteResult.cs`, `SourceRevertResult.cs`
- Keep (for now): `TransactionWriteResult.cs`, the old `WriteChangesAsync`.

**Step 1:** Add `WriteToSourcesAsync` + `RevertAsync` to `ITransactionWriter` (alongside the old method) and the two result structs (see design doc "Writer contract"). No test yet (no behavior).

**Step 2:** Implement them in `SourceTransactionWriter` (Connectors): `WriteToSourcesAsync` reuses the single-pass classification + read-once partition, writes source-bound per source (parallel only when >1 source), returns `(Written, Failed, Errors)`; `RevertAsync(written)` reverts via `ToRollbackChanges` + `WriteChangesInBatchesAsync`, returns `(Failed, Errors)`. Leave the old `WriteChangesAsync` in place.

**Step 3:** Run: `dotnet build src/Namotion.Interceptor.slnx -c Debug` -> succeeds. Full suites still green (nothing calls the new methods yet).

**Step 4: Commit** `feat: add WriteToSourcesAsync/RevertAsync writer contract (unused)`.

---

### Task 3: Rewrite CommitAsync to the unified flow

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs` (`CommitAsync` and the helpers it calls)

**Step 1:** Reread the failure/rollback matrix in the design doc. Identify the tests that pin each branch (`SubjectTransactionFailureHandlingTests`, `SubjectTransactionSourceTests`, `SubjectTransactionRequirementTests`, `SubjectTransactionLocalPropertyTests`). These are your spec.

**Step 2:** Rewrite `CommitAsync` to:
1. snapshot (pooled) + conflict check (unchanged),
2. `writer is null` -> synchronous branch: `ApplyAll(snapshot span)` + in-process rollback only,
3. else: `await writer.WriteToSourcesAsync(...)`; apply `snapshot` excluding `Failed`; reconcile per the matrix using `writer.RevertAsync` + in-process revert.
Route both branches through one reconcile/exception builder (`CreateFailureException`, kept).

**Step 3:** Run the **full** connector + tracking suites:
`dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "Category!=Integration"` and the tracking one. Expected: all green. Iterate on the reconcile logic until every failure-handling/source/requirement/local test passes. Do NOT edit those tests.

**Step 4:** Commit `refactor: unify transaction commit into single apply pass`.

---

### Task 4: Remove the dead old paths

**Files:**
- Modify: `SubjectTransaction.cs` (remove `TryApplyChangesInMemory`, `TryExecuteWritesWithSourceAsync`, `ApplyLocalChangesAsync`, `RollbackOnLocalFailureAsync` if now unused),
- Modify: `SourceTransactionWriter.cs` (remove `WriteChangesAsync`, `WriteSingleSourceAsync`, `WriteGroupedAsync`, and any helpers now unused: `FlattenChanges`, `WriteToSourceWithResultAsync`, etc.),
- Delete: `TransactionWriteResult.cs` (if unreferenced),
- Modify: `ITransactionWriter.cs` (remove old `WriteChangesAsync`),
- Modify: `SubjectPropertyChangeExtensions.cs` (remove the list-based `ApplyAllChanges` if the span overload replaced all callers).

**Step 1:** Delete each member, build after each removal (`dotnet build src/Namotion.Interceptor.slnx`) to confirm it was truly dead.

**Step 2:** Accept the public API snapshots: run the tracking + connectors test suites; `VerifyChecksTests.PublicApi` will fail; diff `.received.txt` vs `.verified.txt`, confirm the diff is exactly the contract change (writer methods, result types), then copy `.received.txt` over `.verified.txt`.

**Step 3:** Run full suites -> green.

**Step 4:** Commit `refactor: remove superseded commit paths and TransactionWriteResult`.

---

### Task 5: Close the apply-failure coverage gap

**Files:**
- Test: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionSourceTests.cs` (or FailureHandling)

**Step 1:** Confirm there is an apply-failure-during-commit test for each mode (a property whose setter throws on commit replay, with a source attached): Rollback reverts source + nothing applied; BestEffort reverts only that property's source. Reuse the existing `ThrowingDevice`/`*OnSetThrows*` patterns. Add any missing combination (notably single-source-with-local apply failure).

**Step 2:** Run -> green. **Step 3:** Commit `test: cover apply-failure during commit for all modes`.

---

### Task 6: Benchmark v2 vs #335 + pool

**Step 1:** Ensure CPU is pinned (`scaling_min_freq == scaling_max_freq`). Recreate/keep a baseline branch that has the 4-mode benchmark on top of #335+pool (the current `performance/reduce-transaction-allocations` + applied pool) for the A/B.

**Step 2:** Run: `pwsh scripts/benchmark.ps1 -Filter "*SubjectTransactionBenchmark*" -BaseBranch <335+pool baseline>` (full job). Compare all four modes.

**Step 3:** Success criterion: allocation and time at least as good as #335+pool on every mode, with `SingleSourceWithLocal` no longer an outlier, and a net reduction in production lines.

**Step 4:** Commit any benchmark adjustments; do not commit generated `benchmark_*.md`.

**Step 5 (very last, definitive result): benchmark v2 vs master.** The #335+pool comparison only proves v2 is not a regression against the in-flight work. The headline number for the PR is v2 vs **master**. Create/use a baseline branch that is `master` plus the 4-mode `SubjectTransactionBenchmark` (e.g. `bench-base-transactions`), then run:

`pwsh scripts/benchmark.ps1 -Filter "*SubjectTransactionBenchmark*" -BaseBranch <master+benchmark baseline>` (full job, pinned CPU).

Record the complete v2-vs-master table (all four modes, allocations and time) as the definitive result in the PR description.

---

## Notes for the implementer

- The transaction must stay source-agnostic: it never calls `TryGetSource` (that lives in the Connectors layer). It only holds the opaque `Written` list from the writer and passes it back to `RevertAsync`. `applyFailed intersect Written` is by `Property` equality (one change per property).
- The no-writer path must remain allocation-light and synchronous (completed `ValueTask`, no `CancellationTokenSource`); do not reintroduce a separate method for it.
- Keep the pooled pending buffer and the `Tracking.Performance.ObjectPool<T>` exactly as carried over.
