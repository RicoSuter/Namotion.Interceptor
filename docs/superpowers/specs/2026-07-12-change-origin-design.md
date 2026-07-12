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

The ambient design also leaves a misleading concurrency artifact: `WebSocketSubjectHandler` carries a comment claiming a `lock` (not a `SemaphoreSlim`) is required because of thread-static state. The claim is inaccurate (a scope opened synchronously after an await composes fine), but the confusion it documents is a cost of the ambient design; the per-write stamp removes the scope there entirely.

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
- The enum is byte-backed deliberately. The CLR does not flatten nested structs into a container's padding, so `SubjectPropertyChange` does NOT store a `ChangeOrigin` field: it stores a flattened `_originKind` byte (which can fold into existing padding) plus the `_originSource` reference (occupying the slot the old `Source` field used), and exposes `Origin` as a computed property reconstructing the struct (internal constructor access via `InternalsVisibleTo`). A test asserts `Unsafe.SizeOf<SubjectPropertyChange>()` does not grow versus master. If that test reveals no free padding byte exists and the flattened layout forces the struct to grow by a full alignment slot, the 8-byte growth is the accepted outcome: it ships with a before/after benchmark comparison posted on the PR to justify it, and the layout is not contorted further to chase the byte back. Do not widen the enum.
- The shape is extensible by design: further kinds (`Correction` in PR 2, `Presumed` if #342 question 4 is ever adopted) are purely additive, and a `TriggeredBy` field can be added source-compatibly. A future `TriggeredBy` does change struct layout, size, and copying cost; that is an accepted layout-affecting change at that point, not reserved storage now.
- `SubjectPropertyChange.Source` (`object?`) is replaced by `SubjectPropertyChange.Origin` (`ChangeOrigin`). Hard break, no compatibility shim. The struct's `WithSource` with-er becomes `WithOrigin`.

## The pending stamp mechanism

A thread-static slot in the core library holds the pending stamp:

```
(PropertyReference target, ChangeOrigin origin, object? sentValue)
```

Arming API in core, internal by design: a public global one-shot arming mechanism invites misuse, and every legitimate producer goes through intent-level APIs that perform the write themselves. Core's `InternalsVisibleTo` covers Tracking, the test project, and the benchmark project, but NOT Connectors, so `PendingOrigin.Arm` is reachable from Tracking but not from the Connectors update appliers. Tracking therefore exposes one public intent-level primitive in `SubjectChangeContextExtensions`, `SetValueFromOrigin(this PropertyReference property, ChangeOrigin origin, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp, object? value)`, which arms the slot for any origin kind, enters the timestamp scope, and performs the write. Contract: when `receivedTimestamp` is null, `SetValueFromOrigin` preserves the ambient received timestamp exactly as `WithChangedTimestamp` does, rather than overwriting it with the null sentinel; only a non-null `receivedTimestamp` replaces the ambient value. `WithTimestamps` must therefore fall back to the previous state's received timestamp on a null argument, unlike the deleted `WithState`, which reset received to the sentinel. `SetValueFromSource` becomes a thin forwarder to it with `ChangeOrigin.FromSource(source)`. The raw slot stays internal; the public surface is intent-level and never hands a caller a bare armed slot to fill later, so the misuse rationale holds.

```csharp
internal static class PendingOrigin
{
    internal static PendingOriginScope Arm(PropertyReference target, ChangeOrigin origin, object? sentValue);
    internal static bool TryConsume(in PropertyReference property, out ChangeOrigin origin, out object? sentValue);
}
```

`PendingOriginScope` is a ref struct that captures the previous slot state on `Arm` and restores it on `Dispose`, forming a zero-allocation stack through nested ref structs (the same pattern as `SubjectChangeContextScope`). Restoring rather than clearing makes nested stamped writes correct: a changing hook that itself calls `SetValueFromSource` for another property arms an inner frame, and the outer stamp is back in place before the outer chain starts.

Rules:

- **Armed per write.** `SetValueFromSource(property, source, changedTimestamp, receivedTimestamp, value)` keeps its public signature and forwards to `SetValueFromOrigin(property, ChangeOrigin.FromSource(source), changedTimestamp, receivedTimestamp, value)`. `SetValueFromOrigin` arms the slot with `(property, origin, value)`, enters the timestamp scope, invokes the setter, and disposes both scopes in `finally`. Batch applies arm once per property write in a loop through the same primitive, which is what all existing OPC UA and MQTT call sites already do via `SetValueFromSource`.
- **Consumed at `PropertyWriteContext` construction, only on target match.** Both `PropertyWriteContext<TProperty>` constructors consume the stamp into the context if and only if the slot is armed and its target matches the chain's property; otherwise the context gets `ChangeOrigin.Local` and the slot is left untouched. `TryConsume` is destructive on match: it hands back the stamp and clears the slot in the same operation, so a second consume of the same property yields `Local`. This destructiveness is load-bearing for the nested-write reasoning below: the "already consumed" argument for `OnXChanged` and INPC cascades holds only because the triggering chain's construction cleared the slot before those callbacks run. Matching MUST use `PropertyReference.Equals`, which is subject reference equality plus an ordinal `Name` comparison (one reference compare plus one ordinal string compare), not a bare `ReferenceEquals` on the name: inbound applies carry non-interned property names deserialized from JSON, so a literal reference comparison on `Name` would never match and echo suppression would silently break. Consumption is pinned to the constructors, not to `InterceptorExecutor.ExecuteInterceptedWrite`, deliberately: the constructor runs exactly once per context, whereas `ExecuteInterceptedWrite` self-delegates to a single fallback context on the no-services path (`InterceptorSubjectContext.cs:185-190`) and would consume twice. Constructor placement is structurally immune to that double-consumption hazard.
- **Restored by the arming frame regardless.** A cancelled write (`OnXChanging` sets `cancel`) never reaches the chain; the scope's `Dispose` restores the previous frame in `finally`, so an unconsumed stamp neither leaks into a later write nor destroys an outer pending stamp.
- **Same-property re-entry is unsupported, directly and transitively.** The direct case: a changing hook that writes its own property re-enters the whole setter and would consume the stamp on the inner invocation. The transitive case: a derived-with-setter property whose `OnXChanging` writes one of its own dependencies triggers a recalculation of the property itself, which target-matches and consumes the still-armed stamp, inverting origins. Target matching identifies the property, not the setter invocation, so it cannot separate either re-entrant write from the armed one. Both direct and transitive same-property re-entry from `OnXChanging` are declared unsupported (they risk recursion regardless of this design) rather than solved with an invocation token.

### Why target matching is required

On master the generated setter runs `OnXChanging` before `SetPropertyValue`, so the hook executes before the armed property's chain exists. Without target matching, a changing hook that writes another property Q would start Q's chain first, and Q would steal the stamp: Q publishes as `FromSource(S)` and gets echo-suppressed, then P publishes as `Local`. Both origins exactly inverted. With target matching, Q's chain sees a mismatch and is `Local`; P's chain matches and consumes. This is the only case that needs the check (`OnXChanged` and INPC cascades run after P's chain has consumed), but it is a real case. Target matching identifies the property, not the setter invocation: same-property re-entry from `OnXChanging`, direct or transitive, is unsupported (see the stamp rules), and that is the boundary of what the check guarantees.

### Why nested writes are Local structurally

- Changing-hook cascade writes: target mismatch (see above).
- Changed-hook cascades, INPC handler write-backs: the slot was already consumed by the triggering chain.
- Derived recalculations (`SetPropertyValueWithInterception`): the slot is empty by the time they run; the `WithSource(null)` anti-scope in `DerivedPropertyChangeHandler` is deleted with no replacement.
- Lifecycle handlers that write: same as any nested write, `Local`.
- A hand-written `IRaisePropertyChanged` implementation cannot get this wrong: handler writes are ordinary writes. The viral local-origin contract from PR #348 does not exist in this design.

Thread-static state is retained but shrinks to a synchronous, single-write handoff: armed and consumed within one call frame, never held across `await`, never inherited.

The burden is symmetric, and worth stating plainly: per-write stamping removes the anti-scope obligation from every local callback, but in exchange it places an arming obligation on every inbound write site in the appliers, and a forgotten arm publishes `Local` and echoes the value straight back to the source it came from, so the tests must cover each applier write kind (currently 5 sites).

## PropertyWriteContext.Origin lifecycle

`PropertyWriteContext<TProperty>` grows two members: `ChangeOrigin Origin` and the internal `SentValue` (boxed only on the already-boxed non-generic path).

`Origin` has two phases, with one mutation point:

- **Before the terminal write executes**, `Origin` is the attempted origin: `FromSource(S)` or `Confirmed(S)` for stamped writes, `Local` otherwise. This is what validators and other mid-chain interceptors read: "this write is being driven by source S".
- **When the terminal write lands** (the same point `IsWritten` becomes true), the executor finalizes `Origin` in place with the survival check: if `Kind != Local` and the final value does not equal `SentValue`, `Origin` becomes `Local`. Equality uses `EqualityComparer<TProperty>.Default` on the generic path and `object.Equals` on the boxed path, matching the equality interceptor's semantics.
- If the write never lands (`IsWritten == false`), `Origin` keeps the attempted value. PR 2's correction detection depends on this.

There is no separate `EffectiveOrigin` property. Two alternatives were considered and rejected. A lazily-cached effective origin fails because a mid-chain reader would freeze the survival check before later interceptors finish rewriting `NewValue`, caching a wrong answer. A pair of properties (`AttemptedOrigin` immutable plus `EffectiveOrigin` finalized) removes the changes-across-`next()` subtlety but introduces a which-one-do-you-read ambiguity and extra context storage; since every consumer class has a fixed read point (validators mid-chain, publishers post-write), the single property with a sharp doc comment stating the two phases was chosen deliberately. Finalizing in place at the `IsWritten` transition removes the ordering hazard.

Construction has a side effect worth flagging: both public `PropertyWriteContext<TProperty>` constructors consume the thread-static pending stamp as part of construction (`TryConsume` on target match). Any code that constructs the struct directly, not only the interceptor chain but tests and benchmarks that new one up, participates in that consumption and will swallow an armed stamp. The constructors MUST carry a doc comment stating this, so a caller who builds a context by hand understands it drains the pending origin.

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
| `Correction(S)` (PR 2) | bypasses the own-source skip: flows through every processor whose property filter matches; topology decides recipients |

In PR 1 the kind is not consulted for the skip at all; the comparison `ReferenceEquals(change.Origin.Source, _source)` is behavior-identical to today. The kind exists for validators, observability, and PR 2. A `Correction` is not an echo of anything (no model change occurred), so it bypasses the own-source skip and is processed by every eligible processor; the identity in `Correction(S)` records which source diverged, it does not target delivery. This matters because origin identity and processor identity need not match: the WebSocket server stamps origins per connection while its processor identifies as the handler, so a "deliver only when the origin source equals the processor source" rule would silently drop every WebSocket correction. Single-owner sources (OPC UA, MQTT) converge to owner-only delivery via their property filters; WebSocket broadcasts the authoritative projection to all replicas, which is desirable; a redundant re-write of an unchanged value to another bound source is idempotent.

## Batch applies

`ApplySubjectUpdate(update, factory)` gains a required `ChangeOrigin origin` parameter. It is deliberately required and deliberately typed: nearly all production callers apply inbound updates and must provide their source, a forgotten optional argument would compile fine while silently breaking echo suppression, and an untyped `object? source` would reintroduce the null-means-local convention the typed model removes. A connector passes `ChangeOrigin.FromSource(connection)`; a local apply states `ChangeOrigin.Local` explicitly, which documents the intent at the call site; `Confirmed` batch applies come for free. This is a source-breaking change, acceptable at 0.0.x, and the compiler forces every call site to decide. The update appliers (`SubjectUpdateApplier`, `SubjectItemsUpdateApplier`) thread it down the tree walk and arm per property write. All five applier write sites are currently wrapped in `SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp)` (`SubjectUpdateApplier.cs:84,131,139` and `SubjectItemsUpdateApplier.cs:124,209`). The rewrite MUST pass `propertyUpdate.Timestamp` as the `changedTimestamp` argument of `SetValueFromOrigin` at every one of them: forgetting it compiles fine and silently replaces every inbound changed-timestamp with capture-time `UtcNow`, corrupting timestamp provenance. The three WebSocket apply sites replace `using (SubjectChangeContext.WithSource(...))` with `ChangeOrigin.FromSource(connection)` (server) and `ChangeOrigin.FromSource(this)` (client). The misleading thread-static lock comment in `WebSocketSubjectHandler` is corrected (the lock stays; if it is still needed, it is for apply serialization, not thread-static storage), to keep the PR focused. `PathExtensions` already applies per property and only swaps the internals of its `SetValueFromSource` call.

## Source lifecycle ordering (deferred to a follow-up)

`SubjectSourceBase` creates its `ChangeQueueProcessor` subscription only after `LoadInitialStateAndResumeAsync`, and `PropertyChangeQueue` drops changes when no subscription exists. Any locally computed write-back produced during initial load (a hook transforming an inbound initial value in PR 1; an equality-suppressed correction in PR 2) is therefore silently discarded, and the source stays diverged until it next pushes that property.

PR 1 does NOT fix this. It is a documented known limitation: because the queue subscription does not yet exist while the initial snapshot is being applied, a write-back computed during the load is lost. The limitation is recorded in `docs/connectors.md`, and a follow-up issue will design the subscription-before-load reorder together with the buffering it requires.

The reorder is deferred rather than folded into PR 1 for two reasons. First, it needs new buffering machinery that does not exist today: subscribing before the load lets the entire initial snapshot accumulate in a pre-processing window, and a single unbounded FIFO queue (today's `PropertyChangeQueueSubscription._queue`) can neither bound the echo backlog nor guarantee that no local write-back is dropped. Second, the drop-safety rationale a naive segregated buffer would rely on is wrong. It is NOT universally safe to drop buffered `FromSource` changes on the assumption that processing would skip them for their own source anyway, because origin identity need not equal processor identity: the WebSocket server stamps origins per connection while its processor identifies as the handler, so a dropped inbound client update would never be broadcast to the other replicas. A correct design must segregate on the actual delivery decision, not on the mere presence of a source. That work is out of scope here and is tracked as the follow-up.

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

**Detection.** In a dedicated `SourceCorrectionDetector` write interceptor ordered before the equality check (`[RunsFirst]` plus `[RunsBefore(typeof(PropertyValueEqualityCheckHandler))]`, so the detector wraps the equality handler): the equality handler runs before the queue interceptor and suppresses unchanged writes by not calling `next`, so the queue interceptor never executes for them and cannot be the detection point. After the detector's `next()` returns, `IsWritten == false` alone does NOT prove equality suppression: any inner interceptor that returns without calling `next` leaves it false, notably transaction capture (`SubjectTransactionInterceptor` captures and stops the chain), and a naive condition would emit a spurious correction for the old model value while the new value sits captured in the transaction. The detector must prove the equality decision explicitly:

```csharp
context.Origin.Kind == ChangeOriginKind.FromSource
&& !context.IsWritten
&& EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue) // projection landed on the stored value, so the equality check suppressed
&& !Equals(context.SentValue, context.NewValue)                                       // and the source sent something else
```

The third clause also excludes the transaction case structurally: when the projected value equals the stored value, the `[RunsFirst]` equality handler suppresses before the transaction interceptor ever runs. This is the diverged case from the #365 comment. The three-outcome matrix:

| Case | Outcome |
|---|---|
| stored value changed | normal write and change publication |
| stored value unchanged, sent value differs from stored | synthesize `Correction(S)` |
| stored value unchanged, sent value equals stored | suppress everything (pure echo or no-op) |

`Confirmed` writes never produce corrections: the commit protocol already guarantees the source holds the value.

**Synthesis.** `context.CurrentValue` was captured before the chain and outside the subject lock, so a concurrent write can make it stale (thread 1 decides on 100, thread 2 writes and publishes 90, thread 1 enqueues a 100-correction after the 90 change, and dedup leaves the source diverged). The detector must NOT resolve this by evaluating the property getter while holding `Subject.SyncRoot`: the getter may run read interceptors, and running interceptor code under the subject lock inverts the codebase's getters-outside-locks discipline (`DerivedPropertyChangeHandler` avoids exactly this against `LifecycleInterceptor`). Instead the detector captures the property's raw write-timestamp metadata at chain entry (before `next()`), reads the current observable value OUTSIDE the lock, and then, under `SyncRoot`, does the minimum: it compares the property's current raw write-timestamp against the value captured at entry. If the timestamp moved, a newer write landed while the detector was deciding, that write is already flowing outbound, and the correction is dropped. If the timestamp is unchanged, the detector treats the window as clear, stamps a fresh write-timestamp, and builds the correction from the observable value it read outside the lock; the enqueue happens after releasing the lock. The write-timestamp comparison is necessary but NOT sufficient as a concurrency sentinel: write-timestamps have tick granularity and `GetTimestampFunction` is user-settable, so two writes can carry equal timestamps and an unchanged timestamp can hide a concurrent write. The safety rule is therefore: on any doubt (the timestamp is unchanged but the value read outside the lock cannot be proven current), DROP the correction. Dropping is always safe, because the only failure mode of a dropped correction is a missing correction, which leaves the source diverged until its next inbound event, never a wrong model value. The concurrency test asserts drop-or-fresh, never stale. Keeping the comparison under the lock still keeps interceptor code off the locked path while linearizing corrections against normal writes. Reading the observable value rather than a raw backing field is deliberate: `SubjectPropertyMetadata.GetValue` invokes the getter, which may pass through read interceptors, so the correction asserts what any consumer or source reading the model would see, no raw getter mechanism is introduced, and a test with a custom read interceptor pins the behavior as intentional. The correction is a `SubjectPropertyChange` with `Origin = Correction(S)`, old value equal to new value equal to the observable value, and the fresh timestamp (see Timestamps below). No model write occurs: no `OnXChanged`, no INPC raise, no backing-field write. A concurrency test pins the stale case (equality decision on 100, concurrent write to 90): the correction is either dropped (the write-timestamp moved) or carries the fresh post-write value, never the stale 100.

**Delivery.** The detector injects the correction through a new internal `PropertyChangeQueue.EnqueueCorrection(in SubjectPropertyChange)` method. The detector lives in the same assembly as the queue (`Namotion.Interceptor.Tracking`), so the method stays internal; it fans out to the queue subscriptions only and never touches `PropertyChangeObservable`. The observable therefore never sees corrections: a correction is not a model change, so app-level subscribers (UI, GraphQL) have nothing to react to; divergence observability is #342 tracker territory. Note that `PropertyChangeQueue` subscriptions are public API, so any direct queue subscriber (the performance profilers do this today) will see `Correction` changes and must filter on `Kind` if it only wants model mutations; this is documented. `ChangeQueueProcessor` gets one branch: `Correction` bypasses the own-source skip and is written like a normal outbound write by every processor whose property filter matches (see the echo suppression section for why delivery is not targeted by source identity). All other kinds keep the single-comparison skip. The processor's flush-time dedup (`SubjectPropertyChange.MergeWithNewer`) must never merge a correction with a normal change for the same property: a newer normal change supersedes a pending correction (the correction is dropped, because the newer change already carries the authoritative value outbound), and a correction never overwrites a queued normal change. Corrections and normal changes only coalesce within their own kind, so the merge is guarded by a kind check.

**Timestamps.** A correction is a new local assertion event. The detector generates a fresh local timestamp, updates only the property's write-timestamp metadata (`SetWriteTimestamp`), and publishes the correction with that same timestamp. It does not write the backing field and fires no hooks or INPC. Publishing the source's inbound timestamp instead would diverge model and source timestamps for the same value, and publishing the model's old timestamp could be rejected as stale; with the fresh timestamp, the source's echo returns the stored value with the same timestamp and is suppressed by the equality check, leaving both participants agreeing on value and time. Connector-level tests cover this (MQTT serializes `ChangedTimestamp`; OPC UA writes it as `SourceTimestamp`).

**Retry reconciliation.** A failed correction write enters the `WriteRetryQueue`, but reapplying it through the property setter is a silent no-op: current equals old equals new, the equality check suppresses, no new queue event appears, and the drained retry entry is gone while the source stays diverged. `ReapplyRetryQueue` therefore handles corrections by kind: if the current model value still equals the correction value, re-enqueue the correction directly (no setter, no hooks, no INPC); if the model changed meanwhile, drop the stale correction, because the newer model change is already flowing outbound. Note the asymmetry with detection: `SubjectSourceBase` lives in `Namotion.Interceptor.Connectors` and cannot reach Tracking's internal `PropertyChangeQueue.EnqueueCorrection`, so a still-valid retry does NOT re-inject into the global Tracking queue. It re-inserts the correction into the `ChangeQueueProcessor`'s own local pending buffer, a Connectors-level path that hands the change straight to the write pump, bypassing the setter entirely. Tests cover the full cycle: correction fails into the retry queue, reconnect, re-enqueue without model side effects, eventual delivery, and stale-drop when the model moved.

**Boundary, documented.** A correction is produced only when the source actively sends a diverging value. Reasserting the model to a silently diverged source (no inbound traffic) remains a reconciliation concern owned by #342.

## Deletions and breaking changes

Deleted in PR 1:

- `SubjectChangeContext.Source` field, `WithSource`, and `WithState` (replaced by `WithTimestamps(changed, received)` for the timestamp half). `SubjectChangeContext` becomes a timestamps-only ambient context; the name stays.
- The `WithSource(null)` anti-scope in `DerivedPropertyChangeHandler`.
- Ambient source reads in `PropertyChangeQueue`, `PropertyChangeObservable`, `SubjectTransactionInterceptor`.
- The cascades-inherit-source semantics and its documentation. The "Change notification source semantics" section in `docs/connectors.md` is today one prose paragraph plus a two-bullet by-kind list (cascade writes inherit the source scope; derived recalculations publish with a null source); both collapse to "origin is stamped per write; everything else is Local".

Breaking public API (acceptable at 0.0.x, mostly internal consumers):

- `SubjectPropertyChange.Source` becomes `Origin` (`ChangeOrigin`); `WithSource` with-er becomes `WithOrigin`. `SubjectPropertyChange.MergeWithNewer` (used by the processor's dedup buffer) currently copies only the newer change's `Source`; it must copy the full flattened origin (both `_originKind` and `_originSource`) from the newer change, or a deduplicated change silently loses its kind and reverts to `Local`.
- `SubjectChangeContext.WithSource` and `WithState` removed; `WithTimestamps` added.
- `IPropertyValidator.Validate` signature change to `PropertyValidationContext<TProperty>`.
- `ApplySubjectUpdate` gains a required `ChangeOrigin origin` parameter.
- New public core types: `ChangeOrigin`, `ChangeOriginKind`; `PropertyWriteContext.Origin` added. `PendingOrigin` and its scope are internal.
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

- Local writes stay allocation-free: the pending stamp is thread-static fields, `ChangeOrigin` is a small struct, and `FinalizeOrigin` short-circuits on `Origin.Kind == Local` before it ever compares. The one caveat is stamped writes: the survival check (and, in PR 2, correction detection) calls `object.Equals` against the boxed `SentValue`, which boxes a value-type `NewValue` for the comparison. That boxing happens only on stamped inbound and cold paths, never on the local write path. `SubjectPropertyChange` stores the origin flattened (kind byte plus source reference, see the model section), targeting no size growth versus master; a test measures `Unsafe.SizeOf<SubjectPropertyChange>()` and the benchmark run validates the outcome rather than assuming it. If no padding byte is free and the struct grows by one alignment slot (8 bytes) regardless, that growth is the accepted outcome, justified by the before/after benchmark on the PR rather than by further layout contortion.
- The hot local-write path gains one thread-static slot check at chain entry (`TryConsume`). Publish time still reads `SubjectChangeContext.Current` for the received timestamp, so the ambient source read that is removed does not offset the added check: the stamp check is a small net addition, not a swap. Expected within noise, validated by the benchmark gate rather than assumed to be compensated.
- Stamped (inbound) writes gain one equality comparison (the survival check) on a network-bound path.
- PR 2 adds `SourceCorrectionDetector` as a `[RunsFirst]` write interceptor that executes on every write, including local ones. It fast-rejects on `Origin.Kind != FromSource`, but still adds one interceptor frame per write; the PR 2 benchmark run covers this.
- Gate: run the existing write-path benchmarks (`Namotion.Interceptor.Benchmark`) before and after PR 1, per the repository benchmark conventions.

## Testing

PR 1:

- Port the behavioral tests from PR #348 as the semantic safety net: `SubjectCascadeLocalOriginTests`, `SubjectChangingHookTransformTests`, echo suppression tests, `DerivedPropertyLocalOriginTests`, and the manual-INPC-base semantics (outcomes only; the generator snapshot tests are dropped since PR 1 touches no generated code).
- New mechanism tests: stamp consumed exactly once; target mismatch (changing-hook cascade writes another property); `finally` clears on cancelled writes; batch apply through `ApplySubjectUpdate` with source; no leakage across sequential applies; transaction capture sees `Local` while commit replay sees `Confirmed`; revert notifications keep original origins; validators receive the attempted origin at capture and replay.
- Initial-load write-back loss is a documented known limitation in PR 1, not a tested behavior: because the queue subscription is created only after the load, a hook-transformed inbound value applied during `LoadInitialStateAsync` is dropped. The subscription-before-load reorder and its buffering are deferred to a follow-up (see "Source lifecycle ordering"); PR 1 records the limitation in `docs/connectors.md`.
- Applier timestamp preservation: an applied update's published change carries the update's timestamp (`propertyUpdate.Timestamp`), not capture-time `UtcNow`.
- `MergeWithNewer` carries the full origin: merging a `FromSource(S)` change with a newer `FromSource(S)` change leaves the result `FromSource(S)` (both kind and source survive), so dedup never silently reverts a change to `Local`.
- Public API snapshot updates.

PR 2:

- The three-outcome correction matrix.
- Correction bypasses the own-source skip and reaches every processor whose property filter matches; a WebSocket-shaped test (origin identity differs from processor identity) proves corrections are not dropped.
- A correction fires no `OnXChanged`, no INPC raise, no backing-field write, and never reaches the observable; it updates the property's write-timestamp metadata and publishes that same fresh timestamp, and the returning source echo is equality-suppressed.
- Direct `PropertyChangeQueue` subscribers observe corrections and can filter on `Kind`.
- Dedup keeps corrections and normal changes separate: a newer normal change for the same property supersedes a pending correction (the correction is dropped), and a correction never overwrites a queued normal change.
- No spurious correction during transaction capture (inbound stamped write with an active transaction captures the value; no correction is emitted).
- Concurrency: equality decision on a stale value with a concurrent write never publishes a stale correction.
- Retry reconciliation: failed correction re-enqueued without model side effects when still valid, dropped when the model changed.
- Observable-value semantics: with a custom read interceptor registered, the correction carries the interceptor-observed value (deliberate, not a raw backing-field read).
- `Confirmed` writes never produce corrections.

## Issue and PR bookkeeping

- PR #348: closed unmerged with a comment pointing to this spec.
- PR 1 closes #345.
- PR 2 closes #365, with a closing comment noting that `TriggeredBy` and `Presumed` remain tracked in #342 as additive extensions.
