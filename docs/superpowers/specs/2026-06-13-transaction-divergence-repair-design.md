# Transaction divergence repair

- Date: 2026-06-13
- Status: approved design, implementation pending
- Issues: implements #340 and the reconciliation mechanism of #342 (epic #347, row 5 and part of row 4)
- Prerequisite: #343 via PR #344 (source-marked commit applies). Implementation must land after PR #344 is merged.

## 1. Context and scope

When a transaction commit fails after writing to external sources, the local model and the sources can end up persistently diverged: a revert write can fail, a transport error can leave the server state unknown, and the commit timeout path currently exits without any repair. Today the thrown `SubjectTransactionException` is the only record; nothing repairs the divergence afterwards.

This design adds a repair mechanism that runs inside the commit failure path and converges the local model to the source. It supersedes three statements in issue #340:

- the guess-only proposal ("no source read-back API is needed"): this design presumes first where a presumption is sound, then confirms by reading back;
- the "no public API drift" constraint: this design deliberately extends `ISubjectSource`, `WriteResult`, `ITransactionWriter`, and `WithSourceTransactions`;
- the "no connector-specific changes" constraint: OPC UA gets a read-back implementation and a resync override.

The consistency contract wording, the typed `ChangeOrigin` discriminator, and the #338 commit-isolation decision remain in the follow-up #342 design. Success-path races are out of scope here: #346 (stale queued write) and #338 (commit window isolation) have their own rows in epic #347.

## 2. The guarantee

After `CommitAsync` returns or throws, every source-bound property satisfies exactly one of:

1. **Consistent**: the local value equals the source value (clean success, clean failure, or repaired).
2. **Pending resync**: a divergence-flagged resynchronization is in flight that retries until it completes, escalating to a transport reconnect when needed. The pending state is queryable and raised as an event the whole time.
3. **Named residual**: one of the documented residuals in section 9, all reported loudly in the thrown exception.

Silent steady-state divergence is eliminated for the built-in `SourceTransactionWriter`, including the commit-timeout path. The guarantee is eventual where case 2 applies.

## 3. Failure classification

Repair must know whether divergence is even possible. `WriteResult` (Connectors) cannot express that today, so it gains a classification. The enum is defined once in `Namotion.Interceptor.Tracking` (namespace `Namotion.Interceptor.Tracking.Change`, next to `SubjectPropertyChange`) so both layers share it:

```csharp
public enum WriteFailureKind
{
    None,          // success
    NothingSent,   // failure before transmission: no divergence possible
    Rejected,      // server answered per item: accepted subset known exactly
    Indeterminate  // transport error mid-call: server state unknown
}
```

- `WriteResult.Failure(...)` defaults to `Indeterminate` (conservative: triggers repair). A new overload accepts an explicit kind for `NothingSent`.
- `WriteResult.PartialFailure(...)` implies `Rejected` (a response with per-item status codes was received).
- `SourceWriteResult` (Tracking) gains a `FailureKind` field mapped by `SourceTransactionWriter` from each `WriteResult`. Multi-source results merge conservatively: `Indeterminate` wins over `Rejected` wins over `NothingSent`.

Call-site mapping in the OPC UA client (`OutboundWriter`):

- "session is not connected" pre-check: `NothingSent`;
- semaphore-cancellation failure in `SubjectSourceExtensions.WriteChangesInBatchesAsync`: `NothingSent`;
- `ProcessWriteResults` outcomes, including the all-items-rejected case: `Rejected`;
- the `catch` around `session.WriteAsync`: `Indeterminate`.

Multi-batch simplification: when a middle batch fails, the not-yet-sent tail batches share the overall result's classification. Treating never-sent changes as in-doubt only costs an idempotent read-back that re-applies values the local model already holds. This keeps `WriteResult` a single flat result instead of a per-batch report.

Classification drives the repair decision:

| Outcome | Repair |
|---|---|
| `NothingSent` | none (no divergence) |
| `Rejected`, revert succeeded | none (state restored) |
| `Rejected`, revert failed | ladder, entry at the presume rung |
| `Indeterminate` | ladder, entry at the read-back rung |
| Commit timeout during source IO | ladder, entry at the read-back rung (all source-bound changes in doubt) |

## 4. Repair orchestration in the commit

### Hook points

All repair runs inside `SubjectTransaction.ReconcileWithWriterAsync`. Its four divergence-capable exits get wired to the ladder:

1. Rollback branch, source-write failure: repair runs after `RevertSourceWritesSafelyAsync` when the revert reported failures or the write was `Indeterminate`.
2. Rollback branch, local-apply failure: same treatment after its source revert.
3. BestEffort branches: same treatment for their targeted reverts.
4. Commit timeout: the write and revert calls get a catch for the commit-timeout cancellation. All source-bound changes are classified in-doubt, repair runs, then the original timeout exception rethrows. This supersedes the failure-matrix line "commit timeout during source revert: no convergence".

Retryability is unchanged: the timeout path still resets the transaction for retry (repair runs while `_isCommitting` is still true, before the rethrow reaches `EndCommit`). If repair moved local values, a `FailOnConflict` retry now correctly reports a conflict; an `IgnoreConflicts` retry re-pushes onto consistent state. Both are safe and documented.

### Division of labor

The placement constraint from #340 holds: the writer cannot touch the local model (`_isCommitting` makes `CaptureChange` reject tracked writes mid-commit), and the transaction cannot talk to sources (Tracking has no source types). Therefore **the writer computes, the transaction applies**:

- the writer walks the ladder and returns changes to apply locally, each carrying the owning source as `Source` so the apply publishes echo-suppressed notifications (this is why #343 is the prerequisite);
- the transaction applies them through `SubjectPropertyChangeOperations.ApplyLocalChanges`, the same primitive used for commit applies, and folds the outcome into the thrown exception.

### The writer seam

One method on `ITransactionWriter` with a default implementation (`Namotion.Interceptor.Tracking` targets net9.0), so custom writers keep compiling and keep today's no-repair behavior:

```csharp
// ITransactionWriter (Tracking); default implementation returns SourceRepairResult.None
ValueTask<SourceRepairResult> RepairDivergenceAsync(
    SourceRepairRequest request,
    CancellationToken cancellationToken);
```

- `SourceRepairRequest`: the affected changes (revert-failed subset or in-doubt set), the `WriteFailureKind`, the revert outcome, and the opaque revert state (which already carries the per-source grouping).
- `SourceRepairResult`: `ChangesToApply` (source-marked presumed and/or read-back values), `PendingResyncSources` (sources where reads failed and a resync was requested), and `Errors`.

### Clocks

The repair timeout belongs to the writer, not the transaction. `SourceTransactionWriter` bounds its ladder IO with its configured `RepairTimeout` (default 5 seconds) using an internally created CTS. This keeps the timeout-path repair working even though the commit token is already cancelled, and keeps Tracking free of repair configuration. Worst-case commit blocking is commit timeout plus repair timeout.

### Reporting surface

`SubjectTransactionException` keeps its shape: `FailedChanges` still means "the transaction intent failed". The repair outcome arrives as a `SourceDivergenceException` (a Connectors type) appended to `Errors`, carrying the affected properties, the failure kind, the rung that resolved each property (presumed, read-back, resync-pending, application-handled), and any repair errors.

## 5. The repair ladder

The full-state reload cannot run inline during the commit: `LoadInitialStateAsync` returns an opaque apply-action that writes subjects directly, and inside the commit's async flow those writes would be rejected as tracked writes (the same `_isCommitting` constraint). The reload therefore always happens on a detached flow, as part of the resync escalation. The inline ladder has three rungs:

- **Rung A, presume (no IO).** Only for revert-failed changes: the write was `Rejected` with a received response, so the source definitely accepted them, and a failed revert means the source almost certainly still holds the written value. The writer returns the written values as changes to apply; the local model immediately matches the probable source state. Skipped for `Indeterminate`, where there is nothing to presume. This delivers immediate consistency in the dominant failure mode (dropped connection), where the read-back below would only burn its timeout.
- **Rung B, targeted read-back (bounded IO).** If the source implements `ISupportsPropertyReadBack`, read the affected properties under `RepairTimeout`. Returned values confirm or correct rung A and resolve `Indeterminate` authoritatively. Properties whose read fails (bad status, timeout) escalate to rung C.
- **Rung C, resync request (fire-and-forget).** When rung B is unsupported, fails, or times out: `source.RequestResynchronization(reason)`, the divergence-pending flag is set, and the commit proceeds to throw. The resync applies values on its own background flow through the normal inbound path, so it is safe regardless of commit state; its completion clears the flag.

Multi-source transactions repair per source group, reusing the grouping carried in the revert state.

## 6. Source interface changes

### `ISubjectSource.RequestResynchronization` (breaking change)

```csharp
/// Ensures a full source-wins resynchronization eventually completes,
/// cycling the transport if one exists. Fire-and-forget; the implementation
/// retries until the resync lands and coalesces concurrent requests.
void RequestResynchronization(string reason);
```

- `SubjectSourceBase` provides the default implementation: schedule its existing `StartBuffering` plus `LoadInitialStateAndResumeAsync` choreography on its background lifetime. The existing behavior that a failed load propagates and triggers reconnection provides the retry guarantee.
- OPC UA overrides to route into `SessionManager` supervision; MQTT and WebSocket route into their reconnection handling likewise.
- In-memory and test sources inherit the base default and are correct for free; for them the resync request is the full reload.
- This is a breaking interface change for implementors that do not derive from `SubjectSourceBase`. Accepted: the library is in its 0.0.x breaking phase, and the member is required for the guarantee in section 2 (a last resort that is optional is not a last resort).

### `ISupportsPropertyReadBack` (opt-in capability)

```csharp
public interface ISupportsPropertyReadBack
{
    /// Reads current source values for the given properties.
    ValueTask<PropertyReadBackResult> ReadPropertyValuesAsync(
        IReadOnlyList<PropertyReference> properties, CancellationToken cancellationToken);
}
```

- `PropertyReadBackResult`: successful entries as (property, value, source timestamp), the properties that could not be read, and an optional error.
- It returns raw values rather than self-applying because the values must flow back through the transaction's `ApplyLocalChanges` during a commit, and through the inbound path when used by the manual converge API. `SourceTransactionWriter` constructs the source-marked `SubjectPropertyChange` entries uniformly.
- OPC UA implementation: `session.ReadAsync` over the affected node ids, batched by `OperationLimits.MaxNodesPerRead`, mapping per-item status codes (good yields a value, bad lands in the failed set).
- Follows the existing capability pattern (`ISupportsConcurrentWrites`). Sources without it skip rung B.

## 7. Configuration and policy

No new extension method. `WithSourceTransactions` gains an optional configure parameter; the parameterless call keeps working and enables automatic repair with defaults:

```csharp
public static IInterceptorSubjectContext WithSourceTransactions(
    this IInterceptorSubjectContext context,
    Action<DivergenceRepairOptions>? configure = null)
```

```csharp
public sealed class DivergenceRepairOptions
{
    public TimeSpan RepairTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public Func<DivergenceReport, DivergenceRepairDecision> OnDivergence { get; set; }
        = _ => DivergenceRepairDecision.RepairAutomatically;
}

public enum DivergenceRepairDecision
{
    RepairAutomatically,
    HandledByApplication
}
```

- The callback is the primitive; automatic and manual are its two return values. `SourceTransactionWriter` consults it at the start of `RepairDivergenceAsync`, before any rung runs; it executes inline in the commit failure path and must be fast.
- If the callback throws, the exception is captured into the report and the decision defaults to `RepairAutomatically`: a broken policy hook must not disable the safety mechanism.
- `HandledByApplication` runs no rungs: the pending state is recorded with the full report, the commit throws, and the application resolves it via one of three supported outs: the manual converge API, its own resync request, or re-asserting its intent with a new transaction.
- The same `WithSourceTransactions` call registers the `SourceConsistencyTracker` service (section 8).

`DivergenceReport` carries the source, the affected changes, the `WriteFailureKind`, the revert errors, and a timestamp.

## 8. Pending divergence tracking and reporting

A context service registered by `WithSourceTransactions`:

```csharp
public sealed class SourceConsistencyTracker
{
    public IReadOnlyCollection<PendingDivergence> GetPending();
    public event Action<DivergenceReport>? DivergenceDetected;
    public event Action<DivergenceResolution>? DivergenceResolved;
    public Task ConvergeToSourceAsync(
        IReadOnlyCollection<PropertyReference>? properties = null,
        CancellationToken cancellationToken = default);
}
```

- `PendingDivergence`: property, source, failure kind, since-timestamp, and whether a presumed (unconfirmed) value was applied.
- Events fire regardless of policy mode (the "never silent" rule). The commit exception's `SourceDivergenceException` is the in-band report; the tracker events are the out-of-band one. Handlers must be fast; no threading guarantees are given.
- `ConvergeToSourceAsync` is the manual repair entry point. It runs outside any commit window on the caller's flow and uses the plain inbound apply path: read-back where the source supports it, resync request otherwise.

**Clearing rule (unified): any source-confirmed value application to a pending property resolves its entry.** This single mechanism covers a rung B read-back, a subscription echo, an inbound update, a later successful commit write to the same property (its apply is source-marked per #343), and the resync's full-state load. The tracker subscribes to the context's property-change stream only while pending entries exist, so the steady-state cost is zero.

Belt and suspenders: when a resync completes (`LoadInitialStateAndResumeAsync` succeeds), the source signals the tracker (resolved from the context when registered) to clear all of its remaining pending entries. This covers a pending property that no longer exists at the source and would otherwise never receive a confirming apply.

`DivergenceResolution` carries how the entry resolved (read-back, echo, inbound, resync, commit, manual), which later feeds the #342 observability story without new plumbing.

**Pending-flag semantics across rungs:** the flag means "unconfirmed", not only "known-diverged". Presumed values that could not be confirmed (rung B failed, rung C requested) keep the flag set until a source-confirmed apply lands, even though the local value is probably already correct.

## 9. Edge cases and residual divergence

Handled:

- **Wrong-guess window**: rung A applies a value the source may no longer hold (revert applied but unacknowledged). Corrected by rung B where reads work, by the resync otherwise, and passively by subscription echoes; flagged unconfirmed until then.
- **Moving target**: a repair read-back can race a concurrent third-party write. That is quiescent consistency, not a defect; subscriptions keep converging afterwards.
- **Multi-source partial failure**: per-source repair with conservative kind merging.

Residual (documented, reported, no repair possible):

1. **A local property setter that deterministically throws** during apply or revert. Read-back fetches the correct source value, but applying it re-runs the same throwing setter. Reported in the exception; local-side divergence remains until the setter stops throwing (a later inbound apply retries it naturally).
2. **A custom `ITransactionWriter` that throws** instead of reporting. It returns neither a written set nor revert state, so nothing can be classified or repaired. Already documented as terminal; the built-in writer never takes this path.
3. **Reverts can clobber concurrent third-party writes and expose transient values to other clients.** OPC UA has no server-side transactions; the revert writes captured old values, not compare-and-swap. This does not break local/source agreement (both sides end at the old value); it overwrites another client's intent. Documented protocol limit, owned by the #342 contract.

## 10. Out of scope

- #346: stale queued non-transactional write overwriting a committed value (flush-time echo filter, epic row 3).
- #338: commit window isolation against concurrent local writes and inbound updates (decided by the #342 design).
- Contract wording, `ChangeOrigin` discriminator, and divergence taxonomy: the #342 design.

## 11. Documentation updates

- `docs/tracking-transactions.md` failure matrix: the revert-failure and in-doubt rows change from "diverged, reported" to "repaired or pending-resync"; the commit-timeout row changes from "no convergence" to "in-doubt repair, then retryable"; retry-conflict interplay documented.
- Sources documentation: `RequestResynchronization` semantics (including coalescing) and `ISupportsPropertyReadBack`.
- Issue #340: closing notes referencing this spec and the superseded statements.

## 12. Testing

- **Unit, repair orchestration** (Tracking/Connectors tests, fake sources via the existing `IFaultInjectable` pattern): each failure kind times each handling mode enters the correct rung; presumed values applied on revert failure; read-back corrects a wrong presumption; read-back failure requests resync and sets pending; timeout path walks the ladder on its own clock and stays retryable; `SourceDivergenceException` contents pinned; `FailOnConflict` retry after repair reports a conflict.
- **Unit, tracker**: every clearing rule (read-back, echo, later commit, resync completion signal, manual converge); events fire in both policy modes; change-stream subscription active only while entries are pending.
- **Unit, policy**: callback decisions honored; throwing callback captured and defaults to automatic; `HandledByApplication` runs no rungs and records pending state.
- **Unit, base class**: `SubjectSourceBase.RequestResynchronization` default runs the buffer/load/replay choreography, retries on load failure, and coalesces concurrent requests.
- **Integration, OPC UA** (`Namotion.Interceptor.OpcUa.Tests`, run targeted per repo convention): read-back maps per-item status codes and honors `MaxNodesPerRead`; resync request routes into session supervision; end-to-end revert-failure scenario converges against the server fixture.
- **Public API snapshots**: Connectors and Tracking verified files change; accept new snapshots in the implementation PR.
- Conventions: `When<Condition>_Then<ExpectedBehavior>` naming, explicit Arrange/Act/Assert, no hardcoded waits (`AsyncTestHelpers.WaitUntilAsync` for eventual paths).

## 13. Components touched

| Component | Change |
|---|---|
| `Namotion.Interceptor.Tracking` | `WriteFailureKind`, `SourceWriteResult.FailureKind`, `ITransactionWriter.RepairDivergenceAsync` (default impl), `SourceRepairRequest`/`SourceRepairResult`, `ReconcileWithWriterAsync` hook points incl. timeout amendment |
| `Namotion.Interceptor.Connectors` | `WriteResult` classification, `SourceTransactionWriter` ladder, `SourceDivergenceException`, `DivergenceRepairOptions`/report/decision, `SourceConsistencyTracker`, `WithSourceTransactions(configure)`, `ISubjectSource.RequestResynchronization`, `SubjectSourceBase` default, `ISupportsPropertyReadBack` |
| `Namotion.Interceptor.OpcUa` | `OutboundWriter` failure kinds, `ISupportsPropertyReadBack` implementation, `RequestResynchronization` override into `SessionManager`, resync-completion signal |
| `Namotion.Interceptor.Mqtt` / `WebSocket` | `RequestResynchronization` overrides into their reconnection handling, failure-kind mapping where determinable |
| Docs | `docs/tracking-transactions.md` failure matrix, sources docs |
