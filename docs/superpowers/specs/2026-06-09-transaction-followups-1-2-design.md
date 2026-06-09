# Transaction follow-ups 1 and 2 (from #337 review)

Design for the first two follow-up items in https://github.com/RicoSuter/Namotion.Interceptor/issues/338#issuecomment-4653925749.

## Scope

This covers exactly two of the three follow-up items from the #337 transaction review comment:

1. A throwing `ITransactionWriter` leaves sources written and the transaction retryable.
2. `CurrentTransaction` (`AsyncLocal`) can be silently bypassed across async flows.

Item 3 (test-suite cleanup) and the broader isolation-window problem in the #338 issue body (options 1/2/3) are explicitly out of scope and will be handled separately.

Guiding constraint for this PR: keep it minimal. Both items are about API misuse that should not happen with a well-behaved writer and correct single-flow usage. The goal is to fail fast and surface the bug where we can do so reliably, and to document it where we cannot, without adding new abstractions, new public API surface, or new state machinery.

All production changes are in `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs` plus XML/markdown docs. No public API members are added, so no `PublicApi.verified.txt` snapshot changes are expected.

## Item 1: throwing `ITransactionWriter`

### Problem

`ITransactionWriter` is contractually required to report per-source failures via `SourceWriteResult`, never to throw (see the `<remarks>` on `ITransactionWriter.WriteToSourcesAsync`). The built-in `SourceTransactionWriter` never throws, so this is latent and only reachable through a custom writer.

In `SubjectTransaction.CommitWithWriterAsync`, `ReconcileWithWriterAsync` awaits `writer.WriteToSourcesAsync(...)` *before* `ApplyLocalChanges`. If the writer throws there:

- The exception propagates out of `ReconcileWithWriterAsync` before `FinishCommit()` runs.
- `FinishCommit()` is what sets `_isCommitted = true`, so it stays `false`.
- In the `finally`, `EndCommit` sees `!_isCommitted` and resets `_commitStarted` to `0`, leaving the transaction retryable.
- A retry re-runs `WriteToSourcesAsync`, re-pushing to sources that may already hold the values (double write).

This is inconsistent with a *reported* full source failure: that path returns a `SubjectTransactionException`, runs `FinishCommit()` (terminal), and is correctly non-retryable. The thrown path is the only one that diverges.

### Fix

Treat a writer throw the same as a full failure. Wrap only the `WriteToSourcesAsync` await inside `ReconcileWithWriterAsync` and convert a throw into the same failure return value the reported path already produces. The existing `FinishCommit()` + `throw failure` in `CommitWithWriterAsync` then makes it terminal for free, with no new field and no change to `EndCommit`'s logic.

```csharp
// ReconcileWithWriterAsync, around the existing WriteToSourcesAsync await:
SourceWriteResult writeResult;
try
{
    writeResult = await writer
        .WriteToSourcesAsync(changes, _requirement, cancellationToken).ConfigureAwait(false);
}
catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
{
    // The writer violated the no-throw contract (and this is not our commit timeout). No
    // SourceWriteResult was returned, so we have neither the written set nor the revertState that
    // RevertSourceWritesAsync needs; sources cannot be reverted. The local model is untouched
    // (apply runs only after the source write returns), so report every change as failed. Returning
    // a failure routes through the normal FinishCommit() path, making the transaction terminal
    // (non-retryable) and consistent with a reported full source failure.
    return new SubjectTransactionException(
        "The transaction writer threw an exception during commit. Sources may be in an undefined, " +
        "un-reverted state; the transaction is terminal and must be disposed, not retried.",
        appliedChanges: [],
        failedChanges: changes.ToArray(),
        errors: [exception]);
}

var (written, failedSource, sourceErrors, revertState) = writeResult;
// ... unchanged from here
```

Notes:

- `changes` is a `Memory<SubjectPropertyChange>` backed by the pooled snapshot buffer; `.ToArray()` copies it out before the buffer is returned in `EndCommit`. Allocation on this error path is acceptable (rare, contract violation).
- The `when (!cancellationToken.IsCancellationRequested)` filter preserves current timeout behavior. The token in scope is the commit timeout token; if the timeout fired, the writer's `OperationCanceledException` is not caught, propagates as today, and remains retryable.
- The stale comment in `EndCommit` that lists "the writer threw" as a retryable case must be corrected (it now applies only to the commit timeout, conflict detection, and optimistic lock acquisition failure).

### Why "same as a full failure" is correct

- Local model: identical. A reported full failure applies nothing locally; a throw also applies nothing locally (it happens before `ApplyLocalChanges`).
- Transaction state: identical. Both report every change as failed and become terminal/non-retryable.
- External sources: the only unavoidable difference. A reported failure can revert partial writes; a throw cannot, because the writer died before returning the `written` set and `revertState`. That residue only exists when a writer has already broken its contract, and it is surfaced via the exception.

### Tests

In `src/Namotion.Interceptor.Connectors.Tests/Transactions/` (alongside the existing writer/failure tests), using a fake `ITransactionWriter` whose `WriteToSourcesAsync` throws:

- `WhenWriterThrows_ThenAllChangesFailAndTransactionIsTerminal`:
  - `CommitAsync` throws `SubjectTransactionException`.
  - `FailedChanges` contains all changes; `AppliedChanges` is empty; the thrown exception is in `Errors` (and is the inner exception).
  - The local model is unchanged.
  - A second `CommitAsync` is rejected (terminal), confirming the transaction is not retryable.

### Docs

- `ITransactionWriter.WriteToSourcesAsync` `<remarks>`: state that a throw makes the transaction terminal with all changes reported failed and sources left un-reverted.
- `CommitAsync` XML doc: note that a throwing writer yields a terminal `SubjectTransactionException`.
- `docs/tracking-transactions.md` error-handling section: one sentence on the throwing-writer outcome.

## Item 2: `AsyncLocal` bypass across async flows

### Problem

The current transaction is stored in `static AsyncLocal<SubjectTransaction?> CurrentTransaction`. `AsyncLocal` values flow *downward* into awaited children but do not flow back up to a parent. To make `await context.BeginTransactionAsync(...)` work, the custom `TransactionAwaiter.GetResult()` calls `SetCurrent(transaction)` in the caller's flow.

The write path in `SubjectTransactionInterceptor.WriteProperty` routes on this: if `HasActiveTransaction` is true but `Current` is `null`, the write falls through to a normal direct model write.

**Facet A, silent bypass.** If `BeginTransactionAsync` is awaited inside a helper that returns the transaction up to the caller, `SetCurrent` ran in the helper's flow and does not propagate. In the caller's flow `Current == null`, so property writes bypass the transaction and hit the model directly with no exception, and the commit captures nothing.

```csharp
async Task<SubjectTransaction> StartTx() => await ctx.BeginTransactionAsync(...);
var tx = await StartTx();   // SetCurrent ran inside StartTx's flow, not here
person.Name = "x";          // caller flow: Current == null -> direct model write
await tx.CommitAsync(...);  // commits an empty changeset
```

**Facet B, Dispose clobbering.** `Dispose()` does `CurrentTransaction.Value = null` unconditionally. If a transaction is disposed in a different flow than the one it was set in, that nulls whatever transaction is current in the *disposer's* flow, which may be an unrelated transaction.

### Fix

Both facets are misuse. We fail fast where the signal is unambiguous and guard where throwing is unsafe.

**Facet A: fail fast at commit, not at write.** A write-time check is not feasible: `HasActiveTransaction && Current == null` is identical for "forgot to carry the transaction" and "legitimate non-transactional write while another flow holds a transaction" (the latter is supported and is exercised by the `RunWithoutAsyncLocalFlowAsync` tests). At commit time we hold `this`, so we can reliably assert the committing flow owns the transaction. Add to `ValidateCanCommit`:

```csharp
if (!ReferenceEquals(CurrentTransaction.Value, this))
    throw new InvalidOperationException(
        "Transaction is being committed from a different async flow than the one it is active in. " +
        "Begin, use, commit, and dispose a transaction within the same async flow.");
```

- Legitimate down-flow usage keeps `Current == this` (AsyncLocal flows across awaits, `ConfigureAwait(false)`, and thread hops within the same logical flow), so there are no false positives. Verified against the existing concurrency tests, which begin and commit inside the same `Task.Run` lambda.
- This runs before the empty-commit fast path, so the bypass scenario (which commits empty) is caught.
- It pairs with the existing nested-transaction check at begin, guarding the lifecycle at both ends.
- It surfaces the bug at the earliest reliable point. It does not retroactively capture the already-bypassed writes; that is impossible once they have hit the model.

**Facet B: guard `Dispose`, do not throw.** `Dispose` runs from `using`/`finally` during exception unwinding, so a throw there could mask the original exception. Guard the slot clear instead:

```csharp
if (CurrentTransaction.Value == this)
    CurrentTransaction.Value = null;
```

Under correct single-flow usage `Value == this` always holds, so this is a no-op there; it only prevents clobbering an unrelated transaction when disposed in the wrong flow. In practice the commit-time check above already catches the wrong-flow case before a clobbering `Dispose` is reached.

### Tests

In `src/Namotion.Interceptor.Tracking.Tests/Transactions/SubjectTransactionTests.cs`:

- `WhenCommittingFromDifferentAsyncFlow_ThenThrows`: begin via a helper `static async Task<SubjectTransaction> BeginInHelper(...) => await ctx.BeginTransactionAsync(...)` so `SetCurrent` runs in the helper's flow; in the caller flow `CommitAsync` throws `InvalidOperationException` mentioning the same async flow.
- `WhenDisposingFromDifferentAsyncFlow_ThenOtherTransactionSlotIsNotCleared`: obtain `tx1` from a helper (its `SetCurrent` lost on return), begin `tx2` in the main flow (now `Current == tx2`), dispose `tx1`, assert `SubjectTransaction.Current` is still `tx2`. This pins the guard (without it, the slot would be clobbered to `null`).

### Docs

- `docs/tracking-transactions.md`: add a `### Single Async Flow` subsection under Limitations describing the begin/use/commit/dispose-in-one-flow requirement, with the wrong-flow commit exception example. Update the Thread Safety bullets and Best Practice #5 to reference it.
- `BeginTransactionAsync` XML doc (extension method and internal method): add a single-flow note.

## Out of scope

- Item 3 test-suite cleanup (separate follow-up PR, rebased on this one).
- The isolation-window problem in the #338 issue body (options 1/2/3).
- A scoped callback API (e.g. `RunInTransactionAsync(...)`) that would structurally prevent Facet A. It is the principled long-term fix but adds public API surface; it can be added later without conflicting with this change.
- A write-time bypass check (infeasible: indistinguishable from legitimate concurrent non-transactional writes).

## Testing

- Unit tests above.
- `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"` must pass.
- Confirm existing transaction tests (Tracking + Connectors) still pass, especially the concurrency tests that begin and commit within `Task.Run` flows and the `RunWithoutAsyncLocalFlowAsync` external-write tests.
