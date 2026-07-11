# Typed ChangeOrigin with one-shot source stamping

Status: draft for review
Date: 2026-07-12
Related: #345 (hook cascade null-source publishing), #365 (typed ChangeOrigin discriminator), #342 (consistency contract, owns deferred extensions), supersedes PR #348 (closed unmerged)

## Summary

Replace the ambient, dynamic-extent source scope with a per-write origin stamp, and replace `SubjectPropertyChange.Source` (`object?`) with a typed `ChangeOrigin` discriminator. A change carries a source only when its stored value is exactly the value that source sent or confirmed. Every other write, including hook cascades, `INotifyPropertyChanged` handler write-backs, and derived recalculations, is local origin by default, structurally rather than by convention.

The work lands as two release-safe PRs:

- **PR 1** (closes #345): the mechanism swap, the `Local | FromSource | Confirmed` kinds, and validator provenance. Solves scenarios 1 to 7 below.
- **PR 2** (closes #365): the `Correction` kind with detection and delivery. Solves scenario 8.

## Problem

`SubjectChangeContext` is a thread-static struct carrying a source reference plus changed and received timestamps. Connectors apply inbound values by wrapping a single setter invocation in a scope (`SetValueFromSource`, `WithState`), and the publishing interceptors read the ambient source at publish time. The scope exists only to smuggle parameters past the fixed property setter signature (`person.Name = x`).

Because a scope has dynamic extent, it covers everything downstream on the call stack, not just the one write it was armed for. Hooks, INPC handlers, and derived recalculations silently inherit a source stamp they never earned, and the outbound echo suppression (`ChangeQueueProcessor` skips changes whose source equals the target source) then wrongly suppresses their writes. PR #348 fixed the semantics by adding a counter-scope (`WithLocalOrigin`) at every escape point: generated hook bodies, INPC raises, derived recalculation, plus a documentation-enforced contract on every hand-written `IRaisePropertyChanged` implementation, plus generator machinery to detect implemented hooks, plus four documented holes the counter-scopes cannot reach. That approach is correct but accretive: every future callback type needs another anti-scope forever.

The ambient design also forces a concurrency constraint: `WebSocketSubjectHandler` must hold a `lock` (not a `SemaphoreSlim`) across a whole update apply because thread-static state cannot survive async boundaries.

### Scenarios that are currently wrong or inconsistent (definition of done)

1. Hook cascade writes during an inbound apply are echo-suppressed and never reach the source (#345).
2. INPC handler write-backs inherit the ambient source the same way.
3. Transformed values (`OnXChanging` reassigns `ref newValue`) must publish as locally computed so corrections flow back to the source.
4. Manual `IRaisePropertyChanged` implementations require a viral, documentation-enforced local-origin contract.
5. A write interceptor that rewrites `PropertyWriteContext.NewValue` during a source-scoped write keeps the ambient source stamp (documented hole).
6. Lifecycle handlers that write inherit ambient authority.
7. Validators must peek at thread-static state (`SubjectChangeContext.Current.Source is ISubjectSource`) and cannot distinguish inbound from commit-confirmed origins.
8. The correction-suppressed-by-equality case (#365 comment): the model stores 100, the source sends 105, a hook projects it to 100, the equality interceptor suppresses the unchanged write, nothing publishes, and the source keeps its diverged value indefinitely.

Scenarios 1 to 7 collapse under the mechanism swap plus typed kinds, mostly by the absence of code. Scenario 8 requires one new capability: distinguishing "the model changed" from "an outbound synchronization must be published".

## Root cause and design principle

The root cause is a granularity mismatch: source authority was propagated with dynamic extent (everything downstream inherits it) while its correct meaning applies to exactly one write (the write the connector itself performs). Timestamps do not share this problem: "all writes in one logical event share one time" is genuinely a dynamic-extent concept.

The design therefore splits the two concerns:

- **Timestamps stay ambient.** `WithChangedTimestamp`, the received timestamp, the lazy resolve and sentinel machinery in `PropertyWriteContext` are unchanged. A new `WithTimestamps(changed, received)` scope replaces the timestamp-carrying half of the deleted `WithState`.
- **Origin becomes a one-shot, per-write stamp.** Armed immediately before a single setter invocation, consumed by the matching write chain, cleared unconditionally by the arming frame. Nothing downstream can inherit it.

## The ChangeOrigin model

New types in the core `Namotion.Interceptor` library:

```csharp
public enum ChangeOriginKind : byte
{
    Local = 0,
    FromSource = 1,
    Confirmed = 2,
    // Correction = 3 is added in PR 2, when its producer lands.
}

public readonly struct ChangeOrigin
{
    public ChangeOriginKind Kind { get; }
    public object? Source { get; }

    public static ChangeOrigin Local { get; } // default(ChangeOrigin)
    public static ChangeOrigin FromSource(object source);
    public static ChangeOrigin Confirmed(object source);
}
```

Design points:

- `default(ChangeOrigin)` is truthfully `Local`, so nothing ever needs to set it for ordinary writes.
- `Source` is non-null exactly when `Kind != Local`. The factories enforce this.
- The enum is byte-backed deliberately: standalone the struct is pointer-aligned to 16 bytes either way, but inside `SubjectPropertyChange` the runtime's auto layout can fold a byte-sized kind into existing padding, where an int-backed enum could grow the struct. Do not widen the enum.
- The shape is additive by design: a `TriggeredBy` field and further kinds (`Correction` in PR 2, `Presumed` if #342 question 4 is ever adopted) can be added without breaking consumers.
- `SubjectPropertyChange.Source` (`object?`) is replaced by `SubjectPropertyChange.Origin` (`ChangeOrigin`). Hard break, no compatibility shim. The struct's `WithSource` with-er becomes `WithOrigin`.

## The pending stamp mechanism

A thread-static slot in the core library holds the pending stamp:

```
(PropertyReference target, ChangeOrigin origin, object? sentValue)
```

Public arming API in core:

```csharp
public static class PendingOrigin
{
    public static PendingOriginScope Arm(PropertyReference target, ChangeOrigin origin, object? sentValue);
    // internal: TryConsume(PropertyReference property, out ChangeOrigin origin, out object? sentValue)
}
```

`PendingOriginScope` is a ref struct whose `Dispose` clears the slot unconditionally.

Rules:

- **Armed per write.** `SetValueFromSource(property, source, changedTimestamp, receivedTimestamp, value)` keeps its public signature. Internally it arms the slot with `(property, FromSource(source), value)`, enters the timestamp scope, invokes the setter, and disposes both scopes in `finally`. Batch applies arm once per property write in a loop, which is what all existing OPC UA and MQTT call sites already do.
- **Consumed at chain entry, only on target match.** When `InterceptorExecutor` starts a write chain, it consumes the stamp into `PropertyWriteContext` if and only if the slot is armed and its target equals the chain's property (two reference comparisons). Otherwise the context gets `ChangeOrigin.Local` and the slot is left untouched.
- **Cleared by the arming frame regardless.** A cancelled write (`OnXChanging` sets `cancel`) never reaches the chain; the `finally` prevents the stamp from leaking into a later write.

### Why target matching is required

On master the generated setter runs `OnXChanging` before `SetPropertyValue`, so the hook executes before the armed property's chain exists. Without target matching, a changing hook that writes another property Q would start Q's chain first, and Q would steal the stamp: Q publishes as `FromSource(S)` and gets echo-suppressed, then P publishes as `Local`. Both origins exactly inverted. With target matching, Q's chain sees a mismatch and is `Local`; P's chain matches and consumes. This is the only case that needs the check (`OnXChanged` and INPC cascades run after P's chain has consumed), but it is a real case, and the check also hardens re-entrancy generally.

### Why nested writes are Local structurally

- Changing-hook cascade writes: target mismatch (see above).
- Changed-hook cascades, INPC handler write-backs: the slot was already consumed by the triggering chain.
- Derived recalculations (`SetPropertyValueWithInterception`): the slot is empty by the time they run; the `WithSource(null)` anti-scope in `DerivedPropertyChangeHandler` is deleted with no replacement.
- Lifecycle handlers that write: same as any nested write, `Local`.
- A hand-written `IRaisePropertyChanged` implementation cannot get this wrong: handler writes are ordinary writes. The viral local-origin contract from PR #348 does not exist in this design.

Thread-static state is retained but shrinks to a synchronous, single-write handoff: armed and consumed within one call frame, never held across `await`, never inherited.

## PropertyWriteContext.Origin lifecycle

`PropertyWriteContext<TProperty>` grows two members: `ChangeOrigin Origin` and the internal `SentValue` (boxed only on the already-boxed non-generic path).

`Origin` has two phases, with one mutation point:

- **Before the terminal write executes**, `Origin` is the attempted origin: `FromSource(S)` or `Confirmed(S)` for stamped writes, `Local` otherwise. This is what validators and other mid-chain interceptors read: "this write is being driven by source S".
- **When the terminal write lands** (the same point `IsWritten` becomes true), the executor finalizes `Origin` in place with the survival check: if `Kind != Local` and the final value does not equal `SentValue`, `Origin` becomes `Local`. Equality uses `EqualityComparer<TProperty>.Default` on the generic path and `object.Equals` on the boxed path, matching the equality interceptor's semantics.
- If the write never lands (`IsWritten == false`), `Origin` keeps the attempted value. PR 2's correction detection depends on this.

There is no separate `EffectiveOrigin` property. A lazily-cached effective origin was considered and rejected: a mid-chain reader would freeze the survival check before later interceptors finish rewriting `NewValue`, caching a wrong answer. Finalizing in place at the `IsWritten` transition removes the ordering hazard.

### What the survival check solves

`SentValue` must ride the stamp rather than being read from `NewValue` at chain entry, because `OnXChanging` runs before the chain and may already have transformed the value. The single check then unifies two scenarios:

- **Hook transforms (scenario 3):** the source sent 105, the hook clamped to 100, stored 100 differs from sent 105, so the write publishes as `Local` and the corrected value flows back to the source.
- **Interceptor rewrites (scenario 5):** a write interceptor that rewrites `NewValue` during a stamped write makes the stored value differ from the sent value, so it publishes as `Local`. The documented hole closes with no code specific to it.

Known residual wart, carried over unchanged and documented: a custom equality that treats distinct instances as equal keeps the stamp. In-place mutation of reference values remains undetectable by any mechanism.

## Publishers, transactions, and the generator

- `PropertyChangeQueue` and `PropertyChangeObservable` read `context.Origin` (finalized, since they publish after `next()` returns) and the existing `WriteTimestampForPublishing`. Their ambient reads of the source are deleted; the received timestamp continues to come from the ambient timestamp context.
- `SubjectPropertyChange.Create` takes a `ChangeOrigin` instead of `object? source`.
- `SubjectTransactionInterceptor` reads `context.Origin` at capture instead of thread-static state. Transaction capture therefore sees `Local` for user writes.
- Commit replay (`SubjectPropertyChangeOperations`) arms the stamp per applied change instead of wrapping in `WithState`: accepted commit values are armed `Confirmed(source)` by `SourceTransactionWriter` (replacing `SubjectPropertyChange.WithSource` stamping with `WithOrigin(ChangeOrigin.Confirmed(source))`); revert applications re-arm each change's stored `Origin` verbatim so revert notifications keep their original provenance.
- **The generator is untouched in PR 1.** Master's bare setter (`OnXChanging`, `SetPropertyValue`, `OnXChanged`, `PropertyChanged?.Invoke`) is already correct under this design. No emitted scopes, no hook detection, no forwarder analysis, no `IRaisePropertyChanged` contract.

## Echo suppression

The skip in `ChangeQueueProcessor` stays a single reference comparison, now over `change.Origin.Source`:

| Origin | Delivery |
|---|---|
| `Local` | delivered to every bound source |
| `FromSource(S)` | skipped for S, delivered to all other bound sources |
| `Confirmed(S)` | skipped for S (the commit already wrote it), delivered to all others |
| `Correction(S)` (PR 2) | delivered only to S |

In PR 1 the kind is not consulted for the skip at all; the comparison `ReferenceEquals(change.Origin.Source, _source)` is behavior-identical to today. The kind exists for validators, observability, and PR 2.

## Batch applies

`ApplySubjectUpdate(update, factory)` gains an optional `object? source = null` parameter (a binary-only break, acceptable at 0.0.x). The update appliers (`SubjectUpdateApplier`, `SubjectItemsUpdateApplier`) thread it down the tree walk and arm per property write. The three WebSocket apply sites replace `using (SubjectChangeContext.WithSource(...))` with the parameter. The thread-static lock constraint documented in `WebSocketSubjectHandler` disappears; the lock itself is kept (apply serialization may be desirable on its own) and only the comment is corrected, to keep the PR focused. `PathExtensions` already applies per property and only swaps the internals of its `SetValueFromSource` call.

## Validators (PR 1)

`IPropertyValidator` changes from

```csharp
IEnumerable<ValidationResult> Validate<TProperty>(PropertyReference property, TProperty value);
```

to

```csharp
IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context);
```

where `PropertyValidationContext<TProperty>` is a readonly struct carrying `Property`, `Value`, and `Origin` (attempted origin, since validation runs mid-chain). The name aligns with `PropertyWriteContext` and avoids colliding with `System.ComponentModel.DataAnnotations.ValidationContext`. Future members are additive instead of another signature break.

`DataAnnotationsValidator` ignores the origin. A provenance-aware validator branches on `Kind`: strict on `Local`, permissive on `FromSource` and `Confirmed`. Transaction timing falls out with no extra work: capture-time validation sees `Local`, commit-replay validation sees `Confirmed`, so user input is gated at capture and source-confirmed values are not re-rejected at replay.

## PR 2: the Correction kind

Adds `ChangeOriginKind.Correction` together with its producer and consumer, so no dead enum member ships.

**Detection.** In the queue interceptor, after `next()` returns: a write that was armed `FromSource(S)`, has `IsWritten == false`, and whose `SentValue` differs from the stored value is the diverged case from the #365 comment. The three-outcome matrix:

| Case | Outcome |
|---|---|
| stored value changed | normal write and change publication |
| stored value unchanged, sent value differs from stored | synthesize `Correction(S)` |
| stored value unchanged, sent value equals stored | suppress everything (pure echo or no-op) |

`Confirmed` writes never produce corrections: the commit protocol already guarantees the source holds the value.

**Synthesis.** The queue interceptor creates a `SubjectPropertyChange` with `Origin = Correction(S)` and old value equal to new value equal to the stored value. No model write occurred, so nothing else fires: no `OnXChanged`, no INPC raise, no timestamp mutation.

**Delivery.** The synthesized change enters the outbound change queue only. The observable never sees corrections: a correction is not a model change, so app-level subscribers (UI, GraphQL) have nothing to react to; divergence observability is #342 tracker territory. `ChangeQueueProcessor` gets one branch: `Correction(S)` is delivered only to S, written like a normal outbound write. All other kinds keep the single-comparison skip.

**Boundary, documented.** A correction is produced only when the source actively sends a diverging value. Reasserting the model to a silently diverged source (no inbound traffic) remains a reconciliation concern owned by #342.

## Deletions and breaking changes

Deleted in PR 1:

- `SubjectChangeContext.Source` field, `WithSource`, and `WithState` (replaced by `WithTimestamps(changed, received)` for the timestamp half). `SubjectChangeContext` becomes a timestamps-only ambient context; the name stays.
- The `WithSource(null)` anti-scope in `DerivedPropertyChangeHandler`.
- Ambient source reads in `PropertyChangeQueue`, `PropertyChangeObservable`, `SubjectTransactionInterceptor`.
- The cascades-inherit-source semantics and its documentation (the five-bullet exception list in `docs/connectors.md` collapses to "origin is stamped per write; everything else is Local").

Breaking public API (acceptable at 0.0.x, mostly internal consumers):

- `SubjectPropertyChange.Source` becomes `Origin` (`ChangeOrigin`); `WithSource` with-er becomes `WithOrigin`.
- `SubjectChangeContext.WithSource` and `WithState` removed; `WithTimestamps` added.
- `IPropertyValidator.Validate` signature change to `PropertyValidationContext<TProperty>`.
- `ApplySubjectUpdate` gains a source parameter.
- New core types: `ChangeOrigin`, `ChangeOriginKind`, `PendingOrigin`, `PendingOriginScope`; `PropertyWriteContext.Origin` added.
- Public API snapshot files (`VerifyChecksTests.PublicApi.verified.txt`) updated in affected test projects.

Unchanged:

- All OPC UA and MQTT call sites (the `SetValueFromSource` signature survives; only its internals change).
- The generator and all generated code.
- `WithChangedTimestamp` and the whole timestamp sentinel and lazy-resolve machinery.
- The echo skip's single reference comparison (PR 1).

Documentation updates: `docs/connectors.md` (source semantics section rewritten and shortened), `docs/tracking.md`, `docs/connectors-subject-updates.md`, `docs/connectors-opcua-client.md` (scope examples replaced by `SetValueFromSource` / `ApplySubjectUpdate` with source), `docs/design/tracking-derived-properties.md` (anti-scope pseudocode updated).

## Out of scope and forward compatibility

- **TriggeredBy** (causal metadata on `Local` changes): shape-reserved, not built. No wrong scenario needs it; its consumer (read visibility for hooks and diagnostics) is tracked in #342 question 9. The correction case does not need it because the write chain still holds the consumed stamp at the detection point.
- **Presumed** ("we wrote to the source and assume it holds the value"): only meaningful if the read-back optimization deferred in PR #349 is adopted; owned by #342 questions 4 and 9. No new issue needed.
- **In-place mutation** of reference-typed values: undetectable by any design.
- **Custom equality** treating distinct instances as equal keeps the source stamp: documented limitation.
- Renaming `SubjectChangeContext`: considered, rejected as churn; the name remains accurate for a timestamps-only context.

## Performance

- No new allocations anywhere: the pending stamp is thread-static fields, `ChangeOrigin` is a 16-byte struct, `SubjectPropertyChange` grows by at most the kind byte folded into padding.
- The hot local-write path swaps one thread-static access for another: chain entry gains one slot check; publish time loses the ambient source reads. Expected net effect within noise.
- Stamped (inbound) writes gain one equality comparison (the survival check) on a network-bound path.
- Gate: run the existing write-path benchmarks (`Namotion.Interceptor.Benchmarks`) before and after PR 1, per the repository benchmark conventions.

## Testing

PR 1:

- Port the behavioral tests from PR #348 as the semantic safety net: `SubjectCascadeLocalOriginTests`, `SubjectChangingHookTransformTests`, echo suppression tests, `DerivedPropertyLocalOriginTests`, and the manual-INPC-base semantics (outcomes only; the generator snapshot tests are dropped since PR 1 touches no generated code).
- New mechanism tests: stamp consumed exactly once; target mismatch (changing-hook cascade writes another property); `finally` clears on cancelled writes; batch apply through `ApplySubjectUpdate` with source; no leakage across sequential applies; transaction capture sees `Local` while commit replay sees `Confirmed`; revert notifications keep original origins; validators receive the attempted origin at capture and replay.
- Public API snapshot updates.

PR 2:

- The three-outcome correction matrix.
- Correction delivered only to the diverged source; other bound sources receive nothing.
- A correction fires no `OnXChanged`, no INPC raise, no timestamp mutation, and never reaches the observable.
- `Confirmed` writes never produce corrections.

## Issue and PR bookkeeping

- PR #348: closed unmerged with a comment pointing to this spec.
- PR 1 closes #345.
- PR 2 closes #365, with a closing comment noting that `TriggeredBy` and `Presumed` remain tracked in #342 as additive extensions.
