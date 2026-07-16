# Value Assertion Writes for Diverged Sources

Closes #365. Written against master (#366 typed origins plus #374 survival hardening). Supersedes the synthesized-correction approach in the unmerged PR #372.

## Problem

The equality-suppressed divergence case (scenario 8 of #366): the model holds 100, a source sends 105, an `OnChanging` hook clamps it to 100. The equality check suppresses the write because the projected value equals the stored value, no change is published, and the source holds 105 indefinitely. Nothing ever tells it otherwise.

A second, related gap: diff-based connectors (WebSocket partial updates) build collection and dictionary updates by diffing a change's old value against its new value. Any re-assertion of unchanged state has old == new, so the diff is empty and a diverged client cannot be converged for collection-valued properties. Any solution to the first problem needs a complete-snapshot form for the second.

## Why not synthesized corrections (learnings from PR #372)

PR #372 solved the first problem by synthesizing a `Correction` change outside the write pipeline and enqueuing it directly into the change queues. Queue-jumping loses the ordering guarantees every ordinary change gets for free, and the branch re-earned them by hand: send-time revalidation against the live model, a bounded follow-up re-assert loop, kind-aware dedup rules, an own-source echo-skip bypass, buffered-versus-immediate delivery cases, retry-queue filtering, and several rounds of timestamp design. It also shipped a public `ChangeOriginKind.Correction` whose `Source` records "who diverged" rather than "whose value was stored", overloading the origin invariant with an exception clause.

The root cause is placement: the suppress-or-not decision was made after the write returned (in `SetValueFromOrigin`), which forced a thread-static outcome slot to ferry "was it suppressed?" out of the chain and a synthetic change to ferry the response back in. The equality handler itself stands at the exact intersection of all the facts needed to decide, at the moment the suppression happens.

## Design

One predicate, one bit, ordinary pipeline.

### Detection: in the equality handler

```csharp
var valuesEqual = EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue);
if (valuesEqual)
{
    var assert =
        context.Origin.Kind == ChangeOriginKind.FromSource    // stamped inbound write
        && !SentValueEqualsStored(ref context);               // source sent something else: divergence
    if (!assert)
        return;                                               // pure echo or local no-op: suppress as today

    context.IsAssertion = true;
}
next(ref context);
```

- `valuesEqual` is the existing comparison; it is what makes this an assertion rather than a write.
- `Origin.Kind == FromSource` excludes local no-ops (stay suppressed), `Confirmed` replays (their sent value equals the committed value, so they also fail the next test), and everything unstamped.
- Sent != stored is the same typed comparison `FinalizeOrigin` uses for origin survival, including the null-survival rule (`SentValue is TProperty t ? Equals(t, NewValue) : SentValue is null && NewValue is null`), read at suppression time instead of finalization time. It requires exposing the attempted sent value on the write context (currently a private field).
- A pure echo (sent == stored) fails the divergence test and stays suppressed, so there is no ping-pong.

### The assertion write

The marked context proceeds through the chain like any write, with one branch at the terminal:

- **Terminal write**: takes the subject lock, runs `FinalizeOrigin` (which demotes `FromSource` to `Local` automatically, because sent != stored), skips the backing-field write, skips `SetWriteTimestamp` (the metadata keeps the value's real last-change time), and leaves `IsWritten` false.
- **Change queue** (connector-facing): gains an explicit publish gate, `IsWritten || IsAssertion`. Today the queue enqueues unconditionally after `next()`, which is why the terminal recheck below can suppress publication only through this gate (a dropped assertion leaves both flags false). The published assertion carries `Local` (the terminal demoted it), old == new == the stored value, ordinary ambient timestamp semantics (the inbound apply's changed timestamp, matching how hook-cascade writes inside the same apply already publish today) and a truthful received timestamp. The plan verifies whether any existing path reaches the queue with `IsWritten` false; if one does, it publishes phantom changes today and the gate fixes a latent bug rather than changing intended behavior.
- **Observable** (application-facing): gains one gate, skips assertions. Application subscribers see nothing.
- **Generated code: zero changes.** `IsWritten` stays false, so the setter's existing bool gating already skips `OnChanged` and `RaisePropertyChanged`. No generator, metadata, or executor change anywhere in this design.
- **Transaction interceptor**: passes assertions through without capturing (one gate). An assertion asserts committed state and must deliver immediately; it is not a pending write.
- **Derived recalculation**: gated, and the gate is required for correctness, not an optimization. `DerivedPropertyChangeHandler.WriteProperty` recalculates unconditionally after `next()` (it does not key on `IsWritten`; the existing transaction-capture suppression exists precisely because of that), so an ungated assertion would evaluate dependent getters, including side-effecting ones, breaking the application-silent contract.
- **Validation**: gated. `ValidationInterceptor` throws on failure and runs before the terminal, so an ungated assertion would reach validators with origin still `FromSource` (demotion happens at the terminal) and could turn a previously silent equal-valued apply into a connector-facing `ValidationException` (a stored-but-now-invalid legacy value, or a provenance-aware validator). The asserted value is already committed; validating it is not this write's job.
- **Lifecycle**: keys on reference differences; assertions (old == new) no-op through it naturally.

The gating principle, stated honestly: every write interceptor must decide act-or-pass for assertions. The default answer is pass (queue publishes, everything else skips or no-ops), and each gate is one visible branch in the interceptor it belongs to, not a parallel delivery mechanism.

### Delivery falls out of existing rules

- **Echo skip**: the assertion is `Local` with a null source, so the own-source skip never matches and the change is delivered to every bound source, including the diverged one. That is the goal, achieved with zero new rules.
- **Dedup/coalescing**: ordinary merging is already correct in queue order. An assertion (100,100) followed by a real write (100,110) coalesces to (100,110).
- **Retry**: there are two retry paths and they need different treatment. The connected-operation path (`WriteRetryQueue.FlushAsync`) sends dequeued changes straight to `source.WriteChangesInBatchesAsync` with no local reapplication, so assertions ride it mechanically, filtered for staleness by the send-time guard below. The reconnect path (`ReapplyRetryQueue` draining `DrainForLocalReapply`) re-applies changes locally through `metadata.SetValue`; an assertion re-applied locally is an equal-valued Local write, which the equality handler suppresses, so assertions are dropped at reapply instead (structural old == new check) and convergence relies on regeneration: `LoadInitialStateAsync` re-applies the source's state inbound and a still-diverged source produces a fresh assertion.
- **Reconnect subscription ordering**: regeneration only works if the processor's queue subscription exists when the initial load runs. Today `SubjectSourceBase.ExecuteAsync` creates the `ChangeQueueProcessor` after `LoadInitialStateAndResumeAsync`, so changes published during initial load are never seen by this source's processor (this already silently drops local writes made during the load window, a pre-existing gap). The subscription moves before the initial load; the processor buffers during the load and delivery starts afterward as today.

### Staleness: what ordering does and does not give

Publishers enqueue after `next()` returns, outside the terminal lock, so enqueue order is not write order: a concurrent real write (100,110) can enqueue before a stale assertion (100,100), and coalescing by queue position would then send 100 while the model holds 110. This race exists today for two concurrent ordinary writes; the assertion design must not add a publisher on a false ordering claim. Two cheap guards, both drop-on-doubt (a dropped assertion costs only "the source stays diverged until its next event", never a wrong value):

1. **Terminal recheck, timestamp-based**: the equality handler captures `TryGetWriteTimestamp` as a baseline when it marks the assertion; the terminal assertion branch compares it under the subject lock and drops the assertion (clears the marker, so the queue gate suppresses publication) if the timestamp moved, meaning a real write landed in between. Timestamp reads are lock-free metadata reads, deliberately not value reads: running the getter under `SyncRoot` would invert the codebase's getters-outside-locks discipline, which is exactly why the synthesized design also used timestamp baselines. Necessary but not sufficient (tick granularity, user-settable clock), so on any doubt: drop.
2. **Send-time guard, value-based**: before writing a structurally identifiable assertion (old == new) to a source, the outbound writer reads the current value via `Metadata.GetValue` (outside any lock) and drops the assertion unless the model still holds it. Placed on the shared source-write path (`WriteChangesInBatchesAsync`), it covers both processor sends and retry flushes, including the terminal-to-publish residue of the race above.

Set-only properties are excluded from assertions entirely (the detection predicate requires `Metadata.GetValue` to be non-null): the send-time guard cannot revalidate a value it cannot read, and asserting unverifiable state contradicts drop-on-doubt. The synthesized design excluded set-only properties for the same reason.

The genuinely in-flight window (an inbound value from the same source lands while the assertion write is already on the wire, and the inbound change is echo-skipped) is an instance of the documented limitation for all outbound writes racing inbound applies (`docs/connectors.md`, local-first sync model): the identical exposure exists today when a real local write is in flight while the same source sends a newer value. Two positions exist:

- **Accept at parity (this design's default)**: recovery is the connector's reconciliation layer, read-after-write on OPC UA (on by default), state-digest divergence detection on WebSocket, the source's next inbound event, or explicit resynchronization via #342. Closing the window only for assertions would give them stronger guarantees than real writes without fixing the class; the class-level fix belongs to #342.
- **Bounded post-write recheck (optional hardening)**: after a successful assertion write to a source, compare the model value once and enqueue a fresh assertion if it moved, bounded to avoid livelock under continuous racing. This is the synthesized design's follow-up mechanism in miniature and the one piece of that machinery with unreplicated value. It narrows the window without closing it (the recheck has its own in-flight window).

The decision between the two is recorded before implementation (open item).

### Identifying assertions downstream

Structurally: old == new, which ordinary published changes can never have while the equality handler is registered (suppression itself guarantees old != new). Consumers that want only real mutations filter on value inequality. Without the equality handler there is no suppression, hence no divergence problem and no assertions; equal-valued stamped writes publish as ordinary writes and converge sources anyway. The diverged source's identity is not carried on the change (`Local` has no source); it is logged at the detection site, where it is known.

## Complete snapshots for collections

`SubjectUpdateFactory` diffs old against new for `Collection` and `Dictionary` kinds; an assertion's empty diff cannot converge a diverged client. The fix builds on machinery that already exists: `BuildCollectionComplete` and `BuildDictionaryComplete` (used for initial loads) already emit full state as `Items` plus `Count` with no `Operations`, and work today only because clients apply them onto empty state. The completeness concept exists in code but not on the wire.

- `SubjectPropertyUpdate` gains an `IsComplete` flag (a flag, not new enum members: `Kind` answers "what shape", completeness answers "merge or replace", and fusing the axes would grow every `Kind` switch and still need more members for #342's full resync).
- The complete builders always set the flag, making initial-load updates self-describing as well.
- The applier gains replace semantics when the flag is set: clear the property's items, then apply `Items`/`Count`.
- The factory routes a subject-collection or dictionary change with old == new (an assertion) to the existing complete builder instead of the diff builder. No new builder, no new wire shape.
- Scalar `Value` and `Object` updates are complete by nature and do not use the flag.

Wire compatibility caveat: an old client ignores the unknown flag and merges, and with index-keyed `Items` a merge of a smaller complete snapshot onto a larger client collection leaves stale trailing items, which is worse than staying diverged. The WebSocket protocol therefore gates sending completes behind a protocol version or capability check (plan item).

`IsComplete` is also the natural building block for explicit full-resynchronization later (#342).

## What is deliberately not built

`ChangeOriginKind.Correction` and its factory, `EnqueueCorrection`, the bounded follow-up re-assert loop, kind-aware dedup, the echo-skip bypass, buffered/immediate delivery cases, and the synthesis-time observable-value read with its lock dance. Two pieces of the synthesized design survive in simplified form: the send-time staleness guard (a single drop-on-doubt check on the shared source-write path instead of a revalidation loop with follow-ups) and the timestamp-baseline recheck (one comparison at the terminal instead of synthesis-side baseline plumbing). Set-only properties remain excluded, as in the synthesized design.

## Public API impact

- `PropertyWriteContext<T>`: exposes the attempted sent value (internal) and the assertion marker. The marker must be readable by interceptors in the Tracking assembly, so it is a public bool property (get public, set internal or public; decide in the plan).
- `SubjectPropertyUpdate.IsComplete`: wire-protocol addition; WebSocket clients must implement replace-on-complete.
- `ChangeOriginKind` stays `Local | FromSource | Confirmed`.
- Nothing else moves: no generator changes, no metadata delegate changes, no executor changes, no new origin kinds, no intent-API changes.

## Semantics summary

| Observer | Sees an assertion? |
|---|---|
| Application (observable, INPC, hooks, derived) | no |
| Property write-timestamp metadata | unchanged (keeps real last-change time) |
| Change queue subscribers / connectors | yes: `Local`, old == new, fresh event timestamps |
| The diverged source | yes: receives the stored value, converges |
| Other bound sources | yes: receive a no-op-valued write of their current value |
| Transactions | never captured |

## Edge cases

- Set-only properties: excluded from assertions by the detection predicate (no getter means no revalidation; see the staleness section).
- Derived properties: a stamped write to a derived property demotes unconditionally at finalization today; whether the equality path can even produce a derived assertion is verified in the plan.
- Multiple queues via fallback contexts: assertions publish through the same chain interceptors as ordinary writes, so aggregation needs no special fan-out.
- `Confirmed` transaction replays: self-exclude via the predicate (sent equals committed value).
- Endpoint timestamp-ordering (OPC UA read-after-write): assertions carry the inbound event's changed timestamp, the same exposure hook-cascade writes have today; endpoint-specific rejection stays a connector concern, as documented for all outbound writes.

## Testing

- Re-target the #365 behavioral tests from the #372 branch: diverged source converges, WebSocket delivery shape, transaction exclusion, pure-echo suppression.
- New: the gating matrix (observable silent, INPC silent, derived recalculation skipped including side-effecting derived getters, validation skipped, queue publishes `Local` old == new, write-timestamp metadata unchanged, transaction ignores), collection complete-snapshot end to end, set-only property exclusion, dedup coalescing with a following real write.
- Staleness guards: the concurrent-write race (stale assertion vs racing real write, terminal recheck drops), the send-time guard on both the processor path and the retry flush path, and reconnect self-healing via initial-state reload.
- Benchmarks: the equality handler adds one `Kind` branch and only in the values-equal case; write benchmarks confirm no local-write regression.

## Relationship to other work

- **Supersedes PR #372.** Recommended mechanics: fresh branch off master, cherry-picking the behavioral tests, test models, and the typed-equality helper from the #372 branch; #372 closes with a pointer to this design.
- **#369 (explicit origin plumbing) simplifies.** The outcome-return half of that design existed to ferry `ValueUnchanged` to a detection site that no longer exists; `WriteOutcome` collapses back to the bool the generated setter needs, and #369 reduces to the origin-parameter half. Its spec (`2026-07-14-explicit-origin-plumbing-design.md`) is revised after this design is approved. The assertion design itself is mechanism-agnostic: the equality handler reads everything from the write context under both the thread-static and the explicit-parameter plumbing.
- **#342**: `IsComplete` is the reusable snapshot form for `RequestResynchronization`.

## Open items for the implementation plan

1. `IsAssertion` visibility shape on the context (public get with internal set, or fully public).
2. Verify transaction interceptor ordering relative to the equality handler and pin the pass-through with a test.
3. Verify the derived-property edge (can a stamped equality-suppressed write target a derived property at all).
4. WebSocket client implementation of replace-on-complete, and the protocol version or capability gate so completes are never sent to clients that would silently merge them.
5. Confirm assertion timestamps against the OPC UA read-after-write integration tests.
6. Exact placement of the send-time guard so it covers processor sends and retry flushes once (`WriteChangesInBatchesAsync` on `SubjectSourceBase` is the candidate).
7. Verify the applier honors `Count` truncation semantics under the flag (today complete updates assume empty client state, so truncation was never exercised).
8. Whether the residual publish-order race for ordinary concurrent writes (pre-existing, independent of assertions) deserves its own issue.
9. Decide accept-at-parity versus bounded post-write recheck for the in-flight window (see the staleness section) before implementation starts.
10. Verify the reconnect subscription reorder (processor created before initial load) against the OPC UA and WebSocket integration tests; it also fixes the pre-existing loss of local writes made during the load window.
