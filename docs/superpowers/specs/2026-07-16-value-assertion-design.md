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
- **Change queue** (connector-facing): needs no change at all. It publishes after `next()` whenever the equality handler let the write proceed, and it reads `context.Origin` after the terminal ran, so it publishes `Local`, old == new == the stored value, with ordinary ambient timestamp semantics (the inbound apply's changed timestamp, which is the newest event time, matching how hook-cascade writes inside the same apply already publish today) and a truthful received timestamp.
- **Observable** (application-facing): gains one gate, skips assertions. Application subscribers see nothing.
- **Generated code: zero changes.** `IsWritten` stays false, so the setter's existing bool gating already skips `OnChanged` and `RaisePropertyChanged`. No generator, metadata, or executor change anywhere in this design.
- **Transaction interceptor**: passes assertions through without capturing (one gate). An assertion asserts committed state and must deliver immediately; it is not a pending write.
- **Derived recalculation and lifecycle**: key on written changes and reference differences respectively; assertions no-op through them naturally. Gates are added only if profiling shows the wasted traversal matters.
- **Validation**: re-validates a value that is already stored; idempotent and harmless.

### Delivery falls out of existing rules

- **Echo skip**: the assertion is `Local` with a null source, so the own-source skip never matches and the change is delivered to every bound source, including the diverged one. That is the goal, achieved with zero new rules.
- **Dedup/coalescing**: ordinary merging is already correct. An assertion (100,100) followed by a real write (100,110) coalesces to (100,110).
- **Retry**: assertions retry like any write; re-asserting committed state is harmless.
- **Ordering and concurrency**: assertions sit in the queue in-order with every other change under the same locks. The entire revalidation apparatus of the synthesized design has no reason to exist.

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

`ChangeOriginKind.Correction` and its factory, `EnqueueCorrection`, send-time revalidation, the follow-up re-assert loop, kind-aware dedup, the echo-skip bypass, buffered/immediate delivery cases, retry filtering, and the observable-value getter read at synthesis (the asserted value is `context.NewValue`, already typed and in hand, so set-only properties work instead of being excluded).

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

- Set-only properties: work; no getter is involved.
- Derived properties: a stamped write to a derived property demotes unconditionally at finalization today; whether the equality path can even produce a derived assertion is verified in the plan.
- Multiple queues via fallback contexts: assertions publish through the same chain interceptors as ordinary writes, so aggregation needs no special fan-out.
- `Confirmed` transaction replays: self-exclude via the predicate (sent equals committed value).
- Endpoint timestamp-ordering (OPC UA read-after-write): assertions carry the inbound event's changed timestamp, the same exposure hook-cascade writes have today; endpoint-specific rejection stays a connector concern, as documented for all outbound writes.

## Testing

- Re-target the #365 behavioral tests from the #372 branch: diverged source converges, WebSocket delivery shape, transaction exclusion, pure-echo suppression.
- New: the gating matrix (observable silent, INPC silent, queue publishes `Local` old == new, write-timestamp metadata unchanged, transaction ignores), collection complete-snapshot end to end, set-only property assertion, dedup coalescing with a following real write.
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
6. Decide whether derived/validation gates are worth adding after profiling.
7. Verify the applier honors `Count` truncation semantics under the flag (today complete updates assume empty client state, so truncation was never exercised).
