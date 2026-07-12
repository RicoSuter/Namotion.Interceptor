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
- **Origin becomes a one-shot, per-write stamp.** Set immediately before a single setter invocation, consumed by the matching write chain, cleared unconditionally by the frame that set it. Nothing downstream can inherit it.

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
- The enum is byte-backed deliberately, keeping `ChangeOrigin` a small struct. `SubjectPropertyChange` stores a single `ChangeOrigin Origin` field as the baseline: `WithOrigin`, `Create`, and `MergeWithNewer` carry it directly, with no flattened storage and no recompose-in-getter. A test asserts `Unsafe.SizeOf<SubjectPropertyChange>()` does not grow beyond one alignment slot (8 bytes) versus master's `object? Source` field. That possible 8-byte growth is the accepted baseline. If the benchmark gate later shows it matters on the hot path, flattening the origin into a padding-folded kind byte plus the existing source reference slot is the known optimization to apply then, not before. Do not widen the enum.
- The shape is extensible by design: further kinds (`Correction` in PR 2, `Presumed` if #342 question 4 is ever adopted) are purely additive, and a `TriggeredBy` field can be added source-compatibly. A future `TriggeredBy` does change struct layout, size, and copying cost; that is an accepted layout-affecting change at that point, not reserved storage now.
- `SubjectPropertyChange.Source` (`object?`) is replaced by `SubjectPropertyChange.Origin` (`ChangeOrigin`). Hard break, no compatibility shim. The struct's `WithSource` with-er becomes `WithOrigin`.

## The pending stamp mechanism

A thread-static slot in the core library holds the pending stamp:

```
(PropertyReference target, ChangeOrigin origin, object? sentValue)
```

The pending-origin API in core is internal by design: a public global one-shot set mechanism invites misuse, and every legitimate producer goes through intent-level APIs that perform the write themselves. Core's `InternalsVisibleTo` covers Tracking, the test project, and the benchmark project, but NOT Connectors, so `PendingOrigin.Set` is reachable from Tracking but not from the Connectors update appliers. Tracking therefore exposes one public intent-level primitive in `SubjectChangeContextExtensions`, `SetValueFromOrigin(this PropertyReference property, ChangeOrigin origin, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp, object? value)`, which sets the pending origin for any origin kind, enters the timestamp scope, and performs the write. Contract: when `receivedTimestamp` is null, `SetValueFromOrigin` preserves the ambient received timestamp exactly as `WithChangedTimestamp` does, rather than overwriting it with the null sentinel; only a non-null `receivedTimestamp` replaces the ambient value. `WithTimestamps` must therefore fall back to the previous state's received timestamp on a null argument, unlike the deleted `WithState`, which reset received to the sentinel. `SetValueFromSource` becomes a thin forwarder to it with `ChangeOrigin.FromSource(source)`. The raw slot stays internal; the public surface is intent-level and never hands a caller a bare pending slot to fill later, so the misuse rationale holds.

```csharp
internal static class PendingOrigin
{
    internal static PendingOriginScope Set(PropertyReference target, ChangeOrigin origin, object? sentValue);
    internal static bool TryConsume(in PropertyReference property, out ChangeOrigin origin, out object? sentValue);
}
```

`PendingOriginScope` is a ref struct that captures the previous slot state on `Set` and restores it on `Dispose`, forming a zero-allocation stack through nested ref structs (the same pattern as `SubjectChangeContextScope`). Restoring rather than clearing makes nested stamped writes correct: a changing hook that itself calls `SetValueFromSource` for another property sets a nested pending origin, and the outer stamp is back in place before the outer chain starts.

Rules:

- **Set per write.** `SetValueFromSource(property, source, changedTimestamp, receivedTimestamp, value)` keeps its public signature and forwards to `SetValueFromOrigin(property, ChangeOrigin.FromSource(source), changedTimestamp, receivedTimestamp, value)`. `SetValueFromOrigin` sets the slot with `(property, origin, value)`, enters the timestamp scope, invokes the setter, and disposes both scopes in `finally`. Batch applies set the pending origin once per property write in a loop through the same primitive, which is what all existing OPC UA and MQTT call sites already do via `SetValueFromSource`.
- **Consumed at `PropertyWriteContext` construction, only on target match.** Every `PropertyWriteContext<TProperty>` constructor consumes the stamp into the context if and only if the slot is armed and its target matches the chain's property; otherwise the context gets `ChangeOrigin.Local` and the slot is left untouched. There is the public constructor plus the internal cascade constructor (the `rawTimestamp` overload used by derived recalculations); all constructors consume identically, so a cascade context whose slot happens to be empty by the time it runs simply gets `Local`. `TryConsume` is destructive on match: it hands back the stamp and clears the slot in the same operation, so a second consume of the same property yields `Local`. This destructiveness is load-bearing for the nested-write reasoning below: the "already consumed" argument for `OnXChanged` and INPC cascades holds only because the triggering chain's construction cleared the slot before those callbacks run. Matching MUST use `PropertyReference.Equals`, which is subject reference equality plus an ordinal `Name` comparison (one reference compare plus one ordinal string compare), not a bare `ReferenceEquals` on the name: inbound applies carry non-interned property names deserialized from JSON, so a literal reference comparison on `Name` would never match and echo suppression would silently break. Consumption is pinned to the constructors, not to `InterceptorExecutor.ExecuteInterceptedWrite`, deliberately: the constructor runs exactly once per context, whereas `ExecuteInterceptedWrite` self-delegates to a single fallback context on the no-services path (`InterceptorSubjectContext.cs:185-190`) and would consume twice. Constructor placement is structurally immune to that double-consumption hazard.
- **Restored by the frame that set it regardless.** A cancelled write (`OnXChanging` sets `cancel`) never reaches the chain; the scope's `Dispose` restores the previous frame in `finally`, so an unconsumed stamp neither leaks into a later write nor destroys an outer pending stamp.
- **Same-property re-entry is unsupported, directly and transitively.** The direct case: a changing hook that writes its own property re-enters the whole setter and would consume the stamp on the inner invocation. The transitive case: a derived-with-setter property whose `OnXChanging` writes one of its own dependencies triggers a recalculation of the property itself, which target-matches and consumes the still-armed stamp, inverting origins. Target matching identifies the property, not the setter invocation, so it cannot separate either re-entrant write from the armed one. Both direct and transitive same-property re-entry from `OnXChanging` are declared unsupported (they risk recursion regardless of this design) rather than solved with an invocation token.

### Why target matching is required

On master the generated setter runs `OnXChanging` before `SetPropertyValue`, so the hook executes before the armed property's chain exists. Without target matching, a changing hook that writes another property Q would start Q's chain first, and Q would steal the stamp: Q publishes as `FromSource(S)` and gets echo-suppressed, then P publishes as `Local`. Both origins exactly inverted. With target matching, Q's chain sees a mismatch and is `Local`; P's chain matches and consumes. This is the only case that needs the check (`OnXChanged` and INPC cascades run after P's chain has consumed), but it is a real case. Target matching identifies the property, not the setter invocation: same-property re-entry from `OnXChanging`, direct or transitive, is unsupported (see the stamp rules), and that is the boundary of what the check guarantees.

### Why nested writes are Local structurally

- Changing-hook cascade writes: target mismatch (see above).
- Changed-hook cascades, INPC handler write-backs: the slot was already consumed by the triggering chain.
- Derived recalculations (`SetPropertyValueWithInterception`): the slot is empty by the time they run; the `WithSource(null)` anti-scope in `DerivedPropertyChangeHandler` is deleted with no replacement.
- Lifecycle handlers that write: same as any nested write, `Local`.
- A hand-written `IRaisePropertyChanged` implementation cannot get this wrong: handler writes are ordinary writes. The viral local-origin contract from PR #348 does not exist in this design.

Thread-static state is retained but shrinks to a synchronous, single-write handoff: set and consumed within one call frame, never held across `await`, never inherited.

The burden is symmetric, and worth stating plainly: per-write stamping removes the anti-scope obligation from every local callback, but in exchange it places an obligation on every inbound write site in the appliers to set the pending origin, and a forgotten set publishes `Local` and echoes the value straight back to the source it came from, so the tests must cover each applier write kind (currently 5 sites).

## PropertyWriteContext.Origin lifecycle

`PropertyWriteContext<TProperty>` grows two members: `ChangeOrigin Origin` and the internal `SentValue`, the value the source sent. `SentValue` arrives as `object?` from the inbound apply (connectors hand values across the `SetValueFromSource(object value)` boundary already boxed), so this design never boxes it a second time.

`Origin` has two phases, with one mutation point:

- **Before the terminal write executes**, `Origin` is the attempted origin: `FromSource(S)` or `Confirmed(S)` for stamped writes, `Local` otherwise. This is what validators and other mid-chain interceptors read: "this write is being driven by source S".
- **When the terminal write lands** (the same point `IsWritten` becomes true), the terminal write delegate finalizes `Origin` in place with the survival check: if `Kind != Local` and the final value does not equal `SentValue`, `Origin` becomes `Local`. Finalization and the survival check live in the terminal write delegates of `WriteInterceptorFactory` (both the no-interceptor variant and the chain variant), under `Subject.SyncRoot`, not in `InterceptorExecutor`. Equality uses `EqualityComparer<TProperty>.Default` computed in the generic terminal frame where `TProperty` is known, the same typed comparison the equality interceptor uses: on the generic path `NewValue` is compared as `TProperty` with no boxing (`SentValue` is cast down from the already-boxed inbound object), and on the non-generic path where `TProperty` is already `object` it degenerates to a boxed object comparison.
- If the write never lands (`IsWritten == false`), `Origin` keeps the attempted value. PR 2's correction detection depends on this.

There is no separate `EffectiveOrigin` property. Two alternatives were considered and rejected. A lazily-cached effective origin fails because a mid-chain reader would freeze the survival check before later interceptors finish rewriting `NewValue`, caching a wrong answer. A pair of properties (`AttemptedOrigin` immutable plus `EffectiveOrigin` finalized) removes the changes-across-`next()` subtlety but introduces a which-one-do-you-read ambiguity and extra context storage; since every consumer class has a fixed read point (validators mid-chain, publishers post-write), the single property with a sharp doc comment stating the two phases was chosen deliberately. Finalizing in place at the `IsWritten` transition removes the ordering hazard.

Construction has a side effect worth flagging: every `PropertyWriteContext<TProperty>` constructor consumes the thread-static pending stamp as part of construction (`TryConsume` on target match), the public constructor and the internal cascade (`rawTimestamp`) constructor alike. Any code that constructs the struct directly participates in that consumption and will swallow a pending stamp: not only the interceptor chain, but tests, benchmarks, and interceptor authors who new one up by hand. The constructors MUST carry a doc comment stating this, so a caller who builds a context by hand understands it drains the pending origin.

### What the survival check solves

`SentValue` must ride the stamp rather than being read from `NewValue` at chain entry, because `OnXChanging` runs before the chain and may already have transformed the value. The single check then unifies two scenarios:

- **Hook transforms (scenario 3):** the source sent 105, the hook clamped to 100, stored 100 differs from sent 105, so the write publishes as `Local` and the corrected value flows back to the source.
- **Interceptor rewrites (scenario 5):** a write interceptor that rewrites `NewValue` during a stamped write makes the stored value differ from the sent value, so it publishes as `Local`. The documented hole closes with no code specific to it.

Known residual wart, carried over unchanged and documented: a custom equality that treats distinct instances as equal keeps the stamp. In-place mutation of reference values remains undetectable by any mechanism.

## Publishers, transactions, and the generator

- `PropertyChangeQueue` and `PropertyChangeObservable` read `context.Origin` (finalized, since they publish after `next()` returns) and the existing `WriteTimestampForPublishing`. Their ambient reads of the source are deleted; the received timestamp continues to come from the ambient timestamp context.
- `SubjectPropertyChange.Create` takes a `ChangeOrigin` instead of `object? source`.
- `SubjectTransactionInterceptor` reads `context.Origin` at capture instead of thread-static state. Transaction capture therefore sees `Local` for user writes.
- Commit replay (`SubjectPropertyChangeOperations`) sets the pending stamp per applied change instead of wrapping in `WithState`: accepted commit values are stamped `Confirmed(source)` by `SourceTransactionWriter` (replacing `SubjectPropertyChange.WithSource` stamping with `WithOrigin(ChangeOrigin.Confirmed(source))`); revert applications set each change's stored `Origin` verbatim so revert notifications keep their original provenance.
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

`ApplySubjectUpdate(update, factory)` gains a required `ChangeOrigin origin` parameter. It is deliberately required and deliberately typed: nearly all production callers apply inbound updates and must provide their source, a forgotten optional argument would compile fine while silently breaking echo suppression, and an untyped `object? source` would reintroduce the null-means-local convention the typed model removes. A connector passes `ChangeOrigin.FromSource(connection)`; a local apply states `ChangeOrigin.Local` explicitly, which documents the intent at the call site; `Confirmed` batch applies come for free. Not every production caller is an inbound source: diagnostic and re-sync callers, for example the ConnectorTester `FailureDiagnostics` reference apply, pass `ChangeOrigin.Local`, so the required parameter covers non-source production callers as well, not only connectors applying inbound updates. This is a source-breaking change, acceptable at 0.0.x, and the compiler forces every call site to decide. The update appliers (`SubjectUpdateApplier`, `SubjectItemsUpdateApplier`) thread it down the tree walk and set the pending origin per property write. All five applier write sites are currently wrapped in `SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp)` (`SubjectUpdateApplier.cs:84,131,139` and `SubjectItemsUpdateApplier.cs:124,209`). The rewrite MUST pass `propertyUpdate.Timestamp` as the `changedTimestamp` argument of `SetValueFromOrigin` at every one of them: forgetting it compiles fine and silently replaces every inbound changed-timestamp with capture-time `UtcNow`, corrupting timestamp provenance. The three WebSocket apply sites replace `using (SubjectChangeContext.WithSource(...))` with `ChangeOrigin.FromSource(connection)` (server) and `ChangeOrigin.FromSource(this)` (client). The misleading thread-static lock comment in `WebSocketSubjectHandler` is corrected (the lock stays; if it is still needed, it is for apply serialization, not thread-static storage), to keep the PR focused. `PathExtensions` already applies per property and only swaps the internals of its `SetValueFromSource` call.

## Source lifecycle (PR 1)

PR 1 is deliberately lifecycle-neutral. It does not touch `SubjectSourceBase`, the `ChangeQueueProcessor` lifecycle, `PropertyChangeQueueSubscription`, or subscription creation. There is no subscription reorder, no enqueue-time filter parameter, no bounded load-window capacity cap, no drop counter, and no divergence warning.

There is a real gap here, but it is deliberately out of scope. A locally computed write-back produced while the initial state loads (a hook transforming an inbound initial value in PR 1; an equality-suppressed correction in PR 2) is dropped, because the change queue subscription does not yet exist while the initial snapshot is being applied. Three facts make deferring it correct:

- It is not one of the eight scenarios this design commits to solve.
- It exists on master in a worse form: master never delivered such write-backs at all.
- It is closed generally by open PR #355, not by this design. #355 makes the subscription source-lifetime (capture never stops), drains the buffered write-backs through the bounded `WriteRetryQueue` after the load, and reconciles them with an origin-agnostic 3-way policy: if the model equals the new value, send it; if it equals the old value, restore and send it; otherwise drop and let the source win.

Keeping PR 1 lifecycle-neutral makes the merge order free: #366 can merge before #355 with no conflicts, and #355 can merge first without any rework here.

Test note: after #355 lands, an integration test should verify that a hook-transformed initial-load value reaches the source through #355's drain and reconcile. That test belongs to whichever of #355 and this work lands second.

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

**Detection.** Correction detection lives in the Tracking primitive `SetValueFromOrigin`, which already brackets every stamped setter invocation: it sets the pending origin, enters the timestamp scope, invokes the setter, and reads a thread-static outcome record after the setter returns. A dedicated write interceptor was rejected: it would add an interceptor frame to every write and require `[RunsFirst]`/`[RunsBefore]` ordering against the equality handler, whereas the arming wrapper already brackets exactly the one write in question and adds nothing to the write chain.

Detection keys off an outcome record, not off timestamps or a process-global transaction flag. `PropertyValueEqualityCheckHandler` records the outcome itself, gated on the write being stamped (`context.Origin.Kind != Local`) so only stamped writes record anything. On suppression (the projected value equals the stored value, the handler stops the chain) it records `(isWritten: false, valueUnchanged: true)`; on pass-through (the values differ, the handler calls `next`) it records `(context.IsWritten` after `next`, `valueUnchanged: false)`. The thread-static outcome is a two-field record:

```
(IsWritten, valueUnchanged)
```

`valueUnchanged` is the equality handler's own `EqualityComparer<TProperty>.Default.Equals(CurrentValue, NewValue)` comparison in its generic `TProperty` frame, no boxing. `InterceptorExecutor` is NOT modified: PR 2 touches no core files, mirroring PR 1's untouched generator. Recording in the handler is consistent with detection already being gated on that handler being registered (see rule 3), so the gate and the recording live in the same place, and the transaction self-exclusion argument becomes exact because it now reasons about this handler's own comparison. The outcome record carries no finalized origin: `SetValueFromOrigin` re-derives kind and source from its own `origin` argument, so a stored origin would be dead storage. Only stamped writes record an outcome; every ordinary local write records nothing.

`SetValueFromOrigin` clears the thread-static outcome slot unconditionally on entry, before it invokes the setter, for every origin kind including `Confirmed`. This is what makes rule 1's cancelled-write self-exclusion structurally sound. A commit replay writes through `PendingOrigin.Set` directly rather than through `SetValueFromOrigin`, so its `Confirmed` outcome is recorded by the equality handler but never consumed by this primitive; without the clear-on-entry that stale outcome, or any earlier write's outcome on the same thread, could be misread by a later cancelled `FromSource` write as its own. Two invariant notes bound the leakage further. First, the generated setter already gates `OnXChanged` and the INPC raise on the write result, so a cancelled or suppressed write also fires no notifications; detection must never lean on that staying true, because the clear-on-entry is the primitive that actually guarantees no stale outcome survives into the next stamped apply, not the notification gating. Second, a null-context subject writes its backing field raw without ever reaching the interceptor chain, so no outcome is recorded for it at all, and clear-on-entry guarantees a preceding null-context write cannot be misattributed to a later stamped one.

After the setter returns, `SetValueFromOrigin` reads and clears the outcome and synthesizes a correction if and only if all of:

1. **An outcome exists** (a stamp was consumed, so the chain ran). A cancelled `OnXChanging` records nothing (the stamp was never consumed and no context was constructed), so it self-excludes structurally: no non-destructive peek of the pending slot is needed or added, and because the slot was cleared on entry (see above) the outcome read here can only be this write's own, never a leaked one.
2. **`!IsWritten`** (the terminal write never landed).
3. **`valueUnchanged`** (the equality handler suppressed the write because the projected value equalled the stored value). Transaction capture self-excludes here: the equality handler runs `[RunsFirst]`, so a transaction only captures when the values differ, which makes `valueUnchanged` false. Detection consults no process-global transaction flag, so an unrelated concurrent transaction on another thread has no effect on it. This self-exclusion holds only when `PropertyValueEqualityCheckHandler` is registered: because it runs `[RunsFirst]` it precedes transaction capture, so a captured write always carries differing values and `valueUnchanged` is false. Correction detection is therefore gated on the equality interceptor being present. `SetValueFromOrigin` resolves it once via `TryGetService` and caches the result per context, and synthesizes corrections only when it is registered. Without the equality handler a captured equal-value write could otherwise synthesize a spurious idempotent correction; gating removes that case entirely rather than guarding against it downstream. This is a hard dependency of the `Correction` kind on the equality check, documented as such.
4. **The sent value differs from the value the getter now returns.** The source's value was silently dropped. A pure echo or no-op has them equal and produces no correction.

Timestamps play no role in detection: no entry/exit write-timestamp comparison, no dependency on a monotonic clock. Under a frozen or fixed test clock, a null-timestamp scope (where `SetWriteTimestamp` stores 0), or same-tick writes, the outcome record is unaffected, so there are no duplicate corrections on transformed inbound writes and no missed corrections. The fresh local timestamp survives only for the synthesized correction itself (see Synthesis).

Because detection runs only inside `SetValueFromOrigin` on `FromSource` writes, an application with no source never triggers it. This is the diverged case from the #365 comment. The three-outcome matrix:

| Case | Outcome |
|---|---|
| stored value changed | normal write and change publication |
| stored value unchanged, sent value differs from stored | synthesize `Correction(S)` |
| stored value unchanged, sent value equals stored | suppress everything (pure echo or no-op) |

`Confirmed` writes never produce corrections: detection only runs for `FromSource`, and the commit protocol already guarantees the source holds the value.

**Synthesis.** The observable value is read after the setter returns and outside the subject lock, so a concurrent write can make it stale (thread 1 decides on 100, thread 2 writes and publishes 90, thread 1 enqueues a 100-correction after the 90 change, and dedup leaves the source diverged). `SetValueFromOrigin` must NOT resolve this by evaluating the property getter while holding `Subject.SyncRoot`: the getter may run read interceptors, and running interceptor code under the subject lock inverts the codebase's getters-outside-locks discipline (`DerivedPropertyChangeHandler` avoids exactly this against `LifecycleInterceptor`). Instead it captures the property's raw write-timestamp metadata before invoking the setter, reads the current observable value OUTSIDE the lock, and then, under `SyncRoot`, does the minimum: it compares the property's current raw write-timestamp against the value captured at entry. If the timestamp moved, a newer write landed while the decision was in flight, that write is already flowing outbound, and the correction is dropped. If the timestamp is unchanged, `SetValueFromOrigin` treats the window as clear, stamps a fresh write-timestamp, and builds the correction from the observable value it read outside the lock; the enqueue happens after releasing the lock. The write-timestamp comparison is necessary but NOT sufficient as a concurrency sentinel: write-timestamps have tick granularity and `GetTimestampFunction` is user-settable, so two writes can carry equal timestamps and an unchanged timestamp can hide a concurrent write. The safety rule is therefore: on any doubt (the timestamp is unchanged but the value read outside the lock cannot be proven current), DROP the correction. Dropping is always safe, because the only failure mode of a dropped correction is a missing correction, which leaves the source diverged until its next inbound event, never a wrong model value. The concurrency test asserts drop-or-fresh, never stale. Keeping the comparison under the lock still keeps interceptor code off the locked path while linearizing corrections against normal writes. Reading the observable value rather than a raw backing field is deliberate: `SubjectPropertyMetadata.GetValue` invokes the getter, which may pass through read interceptors, so the correction asserts what any consumer or source reading the model would see, no raw getter mechanism is introduced, and a test with a custom read interceptor pins the behavior as intentional. The correction is a `SubjectPropertyChange` with `Origin = Correction(S)`, old value equal to new value equal to the observable value, and the fresh timestamp (see Timestamps below). No model write occurs: no `OnXChanged`, no INPC raise, no backing-field write. A concurrency test pins the stale case (equality decision on 100, concurrent write to 90): the correction is either dropped (the write-timestamp moved) or carries the fresh post-write value, never the stale 100.

**Delivery.** `SetValueFromOrigin` injects the correction through a new internal `PropertyChangeQueue.EnqueueCorrection(in SubjectPropertyChange)` method, resolving the queue from the subject's context (`TryGetService<PropertyChangeQueue>()`); when no queue is registered there is no delivery target and no correction is produced. `SetValueFromOrigin` lives in the same assembly as the queue (`Namotion.Interceptor.Tracking`), so the method stays internal; it fans out to the queue subscriptions only and never touches `PropertyChangeObservable`. The observable therefore never sees corrections: a correction is not a model change, so app-level subscribers (UI, GraphQL) have nothing to react to; divergence observability is #342 tracker territory. Note that `PropertyChangeQueue` subscriptions are public API, so any direct queue subscriber (the performance profilers do this today) will see `Correction` changes and must filter on `Kind` if it only wants model mutations; this is documented. `ChangeQueueProcessor` gets one branch: `Correction` bypasses the own-source skip and is written like a normal outbound write by every processor whose property filter matches (see the echo suppression section for why delivery is not targeted by source identity). All other kinds keep the single-comparison skip.

Corrections are delivered only via the buffered flush path. The processor's flush-time dedup keeps one entry per property in its flush dictionary, resolved with an explicit kind branch:

- A normal change beats a correction regardless of queue order: a normal change supersedes a pending correction for the property (the correction is dropped, because the normal change already carries the authoritative value outbound), and a correction never replaces a queued normal change.
- When a normal change supersedes a correction, the normal change's own old value stays the diff baseline. A correction carries `old == new` and contributes nothing to a diff, so it must not be allowed to overwrite the baseline the superseding normal change needs.
- Corrections only coalesce with corrections, which is safe because two corrections for one property carry the same value by construction.

When a `ChangeQueueProcessor` runs with `bufferTime <= 0` (immediate mode, already discouraged and warned about in existing code), it does NOT write corrections: it drops each `Correction` change with a warning log instead. The immediate path has no dedup, so a stale correction enqueued after a concurrent normal change could otherwise leave the source at a WRONG value (the model moves to 90, the correction re-asserts a stale 100), violating the missing-never-wrong safety rule. Dropping the correction keeps the only failure mode a missing correction, never a wrong one; a buffered processor recovers the same divergence on its next flush.

**Send-time revalidation.** Immediately before a processor writes a `Correction` to its source, it re-reads the current model value through the property metadata getter and drops the correction if that value no longer equals the correction's value. This is the load-bearing guarantee for missing-never-wrong. The synthesis-time write-timestamp check has tick-granularity and frozen-clock blind spots, and within-flush dedup cannot stop a stale correction from winning across separate flushes: a correction synthesized in flush N, after a normal change already flushed in flush N-1, has no queued normal change in its own flush dictionary to lose to, so it would otherwise write a stale value to the source. Send-time revalidation closes this unconditionally by ordering. A write that lands before the re-read makes the current model value differ from the correction, so the correction is dropped. A write that lands after the re-read is behind the correction in the same serialized outbound queue, so its own normal change overwrites the source immediately afterward, restoring convergence. The synthesis-time timestamp check therefore stays as a cheap early-out but is no longer load-bearing for the missing-never-wrong guarantee.

**Timestamps.** A correction is a new local assertion event. `SetValueFromOrigin` generates a fresh local timestamp, updates only the property's write-timestamp metadata (`SetWriteTimestamp`), and publishes the correction with that same timestamp. It does not write the backing field and fires no hooks or INPC. Publishing the source's inbound timestamp instead would diverge model and source timestamps for the same value, and publishing the model's old timestamp could be rejected as stale; with the fresh timestamp, the source's echo returns the stored value with the same timestamp and is suppressed by the equality check, leaving both participants agreeing on value and time. Connector-level tests cover this (MQTT serializes `ChangedTimestamp`; OPC UA writes it as `SourceTimestamp`).

**Retry reconciliation.** A failed correction write must not be retried. Reapplying a correction through the property setter is a silent no-op (current equals old equals new, the equality check suppresses, no new queue event appears), and re-sending a stale correction raw could push the source to a value the model has since moved off of. Both hazards are removed by a single filter: `WriteRetryQueue.Enqueue` skips changes with `Origin.Kind == Correction` (with a debug log). A failed correction write is therefore dropped, and the source converges on its next inbound event, the same accepted outcome as every other correction-drop path in this design (immediate mode, dedup supersession, drop-on-doubt, send-time revalidation). This replaces the earlier kind-aware re-checks in both the reconnect (`ReapplyRetryQueue`) and steady-state (`WriteRetryQueue.FlushAsync`) retry paths, neither of which this design adds.

Once #355's origin-agnostic 3-way reconcile lands it handles corrections generically anyway (a correction has model equal to old equal to new, so its restore-and-send branch is a no-op), making this filter harmlessly redundant but still correct in any merge order.

**Boundary, documented.** A correction is produced only when the source actively sends a diverging value. Reasserting the model to a silently diverged source (no inbound traffic) remains a reconciliation concern owned by #342.

## Deletions and breaking changes

Deleted in PR 1:

- `SubjectChangeContext.Source` field, `WithSource`, and `WithState` (replaced by `WithTimestamps(changed, received)` for the timestamp half). `SubjectChangeContext` becomes a timestamps-only ambient context; the name stays.
- The `WithSource(null)` anti-scope in `DerivedPropertyChangeHandler`.
- Ambient source reads in `PropertyChangeQueue`, `PropertyChangeObservable`, `SubjectTransactionInterceptor`.
- The cascades-inherit-source semantics and its documentation. The "Change notification source semantics" section in `docs/connectors.md` is today one prose paragraph plus a two-bullet by-kind list (cascade writes inherit the source scope; derived recalculations publish with a null source); both collapse to "origin is stamped per write; everything else is Local".

Breaking public API (acceptable at 0.0.x, mostly internal consumers):

- `SubjectPropertyChange.Source` becomes `Origin` (`ChangeOrigin`); `WithSource` with-er becomes `WithOrigin`. `SubjectPropertyChange.MergeWithNewer` (used by the processor's dedup buffer) currently copies only the newer change's `Source`; it must copy the newer change's full `Origin`, or a deduplicated change silently loses its kind and reverts to `Local`.
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

- **TriggeredBy** (causal metadata on `Local` changes): shape-reserved, not built. No wrong scenario needs it; its consumer (read visibility for hooks and diagnostics) is tracked in #342 question 9. The correction case does not need it because `SetValueFromOrigin` still holds the origin and the sent value in hand when it decides.
- **Presumed** ("we wrote to the source and assume it holds the value"): only meaningful if the read-back optimization deferred in PR #349 is adopted; owned by #342 questions 4 and 9. No new issue needed.
- **In-place mutation** of reference-typed values: undetectable by any design.
- **Custom equality** treating distinct instances as equal keeps the source stamp: documented limitation.
- Renaming `SubjectChangeContext`: considered, rejected as churn; the name remains accurate for a timestamps-only context.

## Performance

- Local writes stay allocation-free: the pending stamp is thread-static fields, `ChangeOrigin` is a small struct, and `FinalizeOrigin` short-circuits on `Origin.Kind == Local` before it ever compares. The survival check on stamped writes uses `EqualityComparer<TProperty>.Default` in the generic terminal frame, so it does NOT box `NewValue`: `SentValue` arrives already boxed from the inbound apply and is compared down at `TProperty`, and on the non-generic path both operands are already boxed objects. The survival check introduces no value-type boxing on either path. In PR 2, correction detection's divergence check compares the sent value against the observable value; both are already `object?` on that path, so it is an ordinary reference or `object.Equals` comparison with no new boxing. `SubjectPropertyChange` stores the origin as a single `ChangeOrigin` field (see the model section); a test measures `Unsafe.SizeOf<SubjectPropertyChange>()` and the benchmark run validates the outcome. The plain field may cost one alignment slot (8 bytes) versus master's `object? Source`; that growth is the accepted baseline. If the benchmark gate shows it matters on the hot path, flattening the origin into a padding-folded kind byte plus the existing source reference slot is the known optimization to apply then, not before.
- The hot local-write path gains one thread-static slot check at chain entry (`TryConsume`). Publish time still reads `SubjectChangeContext.Current` for the received timestamp, so the ambient source read that is removed does not offset the added check: the stamp check is a small net addition, not a swap. Expected within noise, validated by the benchmark gate rather than assumed to be compensated.
- Stamped (inbound) writes gain one equality comparison (the survival check) on a network-bound path.
- PR 2 adds no interceptor frame and touches no core files: the write chain is identical to PR 1 and every write, local or stamped, runs the same interceptor frames. The one addition to the write path is inside the existing `PropertyValueEqualityCheckHandler`, which records the thread-static outcome only for stamped writes (`Origin.Kind != Local`); a local write takes a single predictable branch and records nothing, so the local write path cost is one branch. Correction detection itself lives inside `SetValueFromOrigin` and runs only after `FromSource` (stamped inbound) applies, which are network-bound paths. Its added cost there is reading and clearing the outcome record, one getter read of the observable value, and a few comparisons; the divergence check is the reference or `object.Equals` comparison over already-boxed values noted above. An application with no source never performs a `FromSource` apply and pays only the single-branch outcome check in the equality handler on its writes, with no registration to gate.
- Gate: run the existing write-path benchmarks (`Namotion.Interceptor.Benchmark`) before and after PR 1, per the repository benchmark conventions.

## Testing

PR 1:

- Port the behavioral tests from PR #348 as the semantic safety net: `SubjectCascadeLocalOriginTests`, `SubjectChangingHookTransformTests`, echo suppression tests, `DerivedPropertyLocalOriginTests`, and the manual-INPC-base semantics (outcomes only; the generator snapshot tests are dropped since PR 1 touches no generated code).
- New mechanism tests: stamp consumed exactly once; target mismatch (changing-hook cascade writes another property); `finally` clears on cancelled writes; batch apply through `ApplySubjectUpdate` with source; no leakage across sequential applies; transaction capture sees `Local` while commit replay sees `Confirmed`; revert notifications keep original origins; validators receive the attempted origin at capture and replay.
- Source lifecycle (see "Source lifecycle (PR 1)"): PR 1 is lifecycle-neutral, so no new lifecycle tests are added here, and steady-state echo suppression stays the existing dequeue-time skip (delivery behavior-identical to master). After #355 lands, an integration test (owned by whichever of #355 and this work lands second) verifies a hook-transformed initial-load value reaches the source through #355's drain and reconcile.
- Applier timestamp preservation: an applied update's published change carries the update's timestamp (`propertyUpdate.Timestamp`), not capture-time `UtcNow`.
- `MergeWithNewer` carries the full origin: merging a `FromSource(S)` change with a newer `FromSource(S)` change leaves the result `FromSource(S)` (both kind and source survive), so dedup never silently reverts a change to `Local`.
- Public API snapshot updates.

PR 2:

- The three-outcome correction matrix.
- Correction bypasses the own-source skip and reaches every processor whose property filter matches; a WebSocket-shaped test (origin identity differs from processor identity) proves corrections are not dropped.
- A correction fires no `OnXChanged`, no INPC raise, no backing-field write, and never reaches the observable; it updates the property's write-timestamp metadata and publishes that same fresh timestamp, and the returning source echo is equality-suppressed.
- Direct `PropertyChangeQueue` subscribers observe corrections and can filter on `Kind`.
- Dedup keeps corrections and normal changes separate: a newer normal change for the same property supersedes a pending correction (the correction is dropped) regardless of queue order and keeps its own old value as the diff baseline, and a correction never overwrites a queued normal change; corrections coalesce only with corrections.
- Immediate mode drops corrections: a processor with `bufferTime <= 0` never writes a `Correction` to its source and logs a warning, while a normal change on the same processor is still written.
- Send-time revalidation: a stale correction that survived synthesis and dedup, where the model value moved between synthesis and the correction's scheduled write, is dropped at send time and never written to the source.
- No spurious correction during transaction capture (inbound stamped write with an active transaction captures the value; no correction is emitted), pinning the `valueUnchanged` self-exclusion (a captured value differs from the stored value, so the outcome's `valueUnchanged` is false and no correction is synthesized).
- Concurrency: equality decision on a stale value with a concurrent write never publishes a stale correction.
- Retry: a failed correction write is not retried (`WriteRetryQueue.Enqueue` skips `Correction` kinds) and a later inbound event converges the source.
- Observable-value semantics: with a custom read interceptor registered, the correction carries the interceptor-observed value (deliberate, not a raw backing-field read).
- `Confirmed` writes never produce corrections.

## Issue and PR bookkeeping

- PR #348: closed unmerged with a comment pointing to this spec.
- PR 1 closes #345.
- PR 2 closes #365, with a closing comment noting that `TriggeredBy` and `Presumed` remain tracked in #342 as additive extensions.
