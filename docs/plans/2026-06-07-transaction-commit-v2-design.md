# Transaction commit v2 design

## Motivation

The current transaction commit (as built up in PR #335 plus the buffer-pool follow-up) is fast but fragmented. It has several code paths and applies changes in two different places, which makes it hard to reason about and leaves one path slow.

Concretely, the commit splits into:

1. A synchronous in-memory path (`TryApplyChangesInMemory`) used when no `ITransactionWriter` is registered.
2. The writer's single-source fast path.
3. The writer's grouped (multi-source) path.

On top of that the apply happens twice: the writer applies the source-bound changes in-process, then the transaction applies the local (no-source) changes separately. That second split is the root of both the duplication and the slow "single source + local" case.

Measured per-mode allocation (50 changes, with #335 + pool, vs master):

| Mode | master | #335 + pool |
|---|---|---|
| Local | 90.56 KB | 4.95 KB |
| SingleSource | 143.41 KB | 14.74 KB |
| SingleSourceWithLocal | 128.30 KB | 45.12 KB |
| MultiSource | 143.05 KB | 57.67 KB |

`SingleSourceWithLocal` is the outlier: ~45 KB, far more than `local (5) + source (15)` would suggest, because a transaction with any local change forfeits the "no aggregate lists" fast path and runs two apply passes.

## Goal

Simpler **and** faster than #335 + pool, with the mixed-path outlier fixed. Behavior and correctness must not change; the public API of the writer interface may change (no external implementations exist).

## v2 architecture

One commit flow for every mode. The writer becomes mechanical (external source I/O only); the transaction owns all in-process apply, failure policy, and rollback orchestration.

```
CommitAsync(ct):
  ValidateCanCommit(); acquire lock (optimistic only)
  changes = snapshot pending (pooled buffer)
  ThrowIfConflictsDetected(changes)
  writer = Context.TryGet<ITransactionWriter>()

  // 1. Write source-bound changes to sources (no apply). No writer => nothing to write.
  (written, failedS, srcErrors) = writer is null
        ? (empty, empty, empty)
        : await writer.WriteToSourcesAsync(changes, requirement, ct)

  // 2. Apply everything destined in-process, in ONE pass:
  //    the whole snapshot minus the source-write failures.
  toApply = failedS.Count == 0 ? changes : changes.Except(failedS)
  (applied, applyFailed, applyErrors) = ApplyAll(toApply)

  // 3. Reconcile (one place); see failure matrix.
  clear pending; mark committed
  throw on any failure
```

Key property: `snapshot = written + failedS + local`, so `snapshot - failedS = written + local`, which is exactly what should land in-process. On the common no-source-failure path that is the entire snapshot, so every mode runs the same single span-based apply with no extra list allocation.

## Writer contract

The writer no longer applies in-process and never reverts on its own. All policy (Rollback / BestEffort) leaves the writer and lives in the transaction.

```csharp
public interface ITransactionWriter
{
    // Writes every source-bound change to its source (best-effort per source) and reports the
    // per-change outcome. Does not apply in-process. Performs classification (source vs local) and
    // the SingleWrite requirement check, since it alone knows the SetSource mappings.
    ValueTask<SourceWriteResult> WriteToSourcesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        TransactionRequirement requirement,
        CancellationToken cancellationToken);

    // Reverts previously-written changes at their sources (for rollback).
    ValueTask<SourceRevertResult> RevertAsync(
        IReadOnlyList<SubjectPropertyChange> written,
        CancellationToken cancellationToken);
}

public readonly record struct SourceWriteResult(
    IReadOnlyList<SubjectPropertyChange> Written,  // source-bound, reached their source (for revert)
    IReadOnlyList<SubjectPropertyChange> Failed,   // source-bound, write failed (excluded + reported)
    IReadOnlyList<Exception> Errors);

public readonly record struct SourceRevertResult(
    IReadOnlyList<SubjectPropertyChange> Failed,
    IReadOnlyList<Exception> Errors);
```

`Local` is not returned: the transaction applies `snapshot - Failed`, which already equals `written + local`. The transaction stays source-agnostic (it lives in the `Tracking` layer, which cannot call the `Connectors`-layer `TryGetSource`); it only holds the opaque `Written` list to hand back to `RevertAsync` on rollback.

## Failure / rollback matrix

Two invariants, identical to today (the existing 410 connector + 332 tracking tests are the oracle for exact `SubjectTransactionException` contents):

- **Rollback = all-or-nothing.** On any failure (source-write or in-process apply): nothing is applied in-process, every change that reached a source is reverted there, and the exception reports the failures.
- **BestEffort = partial, per-property-consistent.** Successful changes (written + applied) stay; failures are reported; and any change that failed to apply has its source write reverted so the source and the model never disagree about that property.

Transaction reconcile:

```
if Rollback && failedS.Count > 0:
    rev = await writer.RevertAsync(written)
    throw Tx([], failedS (+rev.Failed), srcErrors (+rev.Errors))   // local NOT applied

(applied, applyFailed, applyErrors) = ApplyAll(snapshot - failedS)

if applyFailed.Count > 0:
    if Rollback:
        RevertInProcess(applied)
        rev = await writer.RevertAsync(written)
        throw Tx([], failedS+applyFailed (+rev.Failed), srcErrors+applyErrors (+rev.Errors))
    else: // BestEffort
        await writer.RevertAsync(applyFailed intersect written)   // keep source == model
        throw Tx(applied, failedS+applyFailed, srcErrors+applyErrors)

if failedS.Count > 0:   // BestEffort, source partial-fail, applies ok
    throw Tx(applied, failedS, srcErrors)
```

`applyFailed intersect written` is by `Property` equality (one change per property), no source read.

## Apply model

One apply primitive, the existing zero-copy `ApplyAllChanges`, generalized to take a `ReadOnlySpan<SubjectPropertyChange>` plus an optional exclude set (the rare source-write-failure case). It returns the input as the successful set on full success (no copy), allocating failed/error lists only on failure. This replaces both `TryApplyChangesInMemory` and the list-based `ApplyAllChanges`.

## No-writer fast path

No longer a separate method. With `writer == null` there are no source writes and no `RevertAsync`, so the entire flow is synchronous and `CommitAsync` returns a completed `ValueTask` with no `CancellationTokenSource` and no async state machine, preserving the existing in-memory commit speed. One flow, two trivial branches (writer / no-writer).

## Buffer pool

Unchanged from the pool work: the per-transaction pending `OrderedDictionary` is rented from a pooled `ObjectPool<T>` (consolidated into the `Tracking` layer) and cleared before return. This keeps the snapshot buffer near-free across commits.

## Public API changes

- `ITransactionWriter` gains `WriteToSourcesAsync` + `RevertAsync` and loses `WriteChangesAsync`.
- `TransactionWriteResult` is replaced by `SourceWriteResult` + `SourceRevertResult`.
- `SubjectTransaction.CommitAsync` already returns `ValueTask` (from #335).

Only one internal implementation (`SourceTransactionWriter`) exists; it shrinks substantially (write + revert + classification only, no apply or rollback orchestration). Public API snapshots updated accordingly.

## Testing strategy

- Run the existing 410 connector + 332 tracking tests green throughout. They encode the exact rollback/failure semantics and are the behavioral oracle; a red test means behavior changed and must be investigated.
- Keep the `SingleSourceWithLocal` and `MultipleSourcesAndLocal` tests; add an apply-failure-during-commit test per mode.
- Benchmark all four modes against #335 + pool; success criterion is simpler code **and** allocation/time at least as good on every mode, with the `SingleSourceWithLocal` outlier removed.

## Expected outcome

- One commit flow, one apply pass, one rollback orchestrator; the writer is mechanical I/O.
- `SingleSourceWithLocal` drops toward the cost of a single apply pass (no aggregate-list shuffle), removing the outlier.
- Net code reduction versus #335 + pool (the in-memory path, the two-pass local handling, and the writer's per-mode rollback branches all collapse).
