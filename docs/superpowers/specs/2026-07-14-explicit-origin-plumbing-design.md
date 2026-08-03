# Explicit Origin Plumbing: Parameters and Return Values Instead of Thread-Static State

Implements issue #369. Builds on #366 (merged) and #372 (assumed merged as-is; this work sits on top of `feature/change-origin-corrections`).

Sequencing: implementation starts once #372 merges, and lands immediately after it, before other write-path work (#342 and later) begins. Landing before #372 was considered and rejected: it would force rewriting #372's finished detection mechanism mid-flight, invalidate its review history, and ship the outcome plumbing without its only consumer (the correction test suite).

## Goal

Make the library more performant and less complex by replacing the thread-static origin plumbing with explicit dataflow, at the cost of breaking a small number of plumbing-level APIs.

- Origin is per-write information (nested writes never inherit it, per #366 semantics), so it becomes a parameter.
- Outcome is per-write result information, so it becomes a return value.
- Timestamps are genuinely dynamic-extent (one logical event shares one time across hook cascades and derived recalculations), so they stay ambient. Nothing else moves.

This is a pure mechanism swap: origin kinds, finalize/demote rules, the #372 correction matrix, echo suppression, and all behavioral semantics are unchanged. Issue #367 is explicitly out of scope.

## Why

The one-shot pending stamp (`PendingOrigin`) and the outcome slot are hidden state that polices itself: one-shot consumption, target matching so nested writes cannot steal the stamp, restore-on-dispose for re-entrancy, clear-on-entry so a cancelled write cannot misread a stale outcome, and a documented constructor side effect. Each rule prevents a real leak scenario, and these mechanics were the dominant review-finding class across #366/#372. Explicit dataflow eliminates the class: a nested setter simply never receives an origin, so "nested writes are Local" becomes syntax instead of an invariant.

Performance: measurements across #366/#372 showed no meaningful difference between mechanisms, so this is not an optimization. The honest performance story is a principle: today every local write pays a thread-static read plus a property-reference compare (`PendingOrigin.TryConsume` in the `PropertyWriteContext` constructor) even in applications that never connect a source. After this change, local writes touch no origin machinery at all, and stamped writes drop roughly six thread-static operations each.

A further hazard class disappears entirely: `PendingOrigin` is thread-static and documented "never across await" (thread-statics do not flow with `ExecutionContext`), so any future code that suspends between arming and consuming corrupts silently. Parameters and return values survive `await` trivially, so this constraint stops existing rather than needing enforcement.

A structural constraint narrows the design space: stamped writes must run the generated setter body (`OnXChanging`/`OnXChanged` hooks and `RaisePropertyChanged` live there; hook clamping of inbound values is the #372 correction scenario, and INPC-bound UIs must update on source writes), and a C# property setter cannot take parameters. Therefore the generator must emit an origin-accepting entry point per property, with the property setter forwarding to it. This shape is forced, not chosen.

## Core write path

### WriteOutcome

New public readonly struct in `Namotion.Interceptor`:

```csharp
public readonly struct WriteOutcome
{
    public bool IsWritten { get; }        // terminal write landed
    public bool ValueUnchanged { get; }   // equality handler suppressed the write
}
```

No `Origin` field: `SetValueFromOrigin` re-derives kind and source from its own argument (the current `PendingOrigin` outcome-slot comment already documents that a stored origin would be dead storage).

### AttemptedOrigin becomes the parameter carrier

`AttemptedOrigin` (existing internal readonly struct pairing `ChangeOrigin Origin` with `object? SentValue`) is promoted to public and becomes the single carrier for "who claims this write and what did they send." `default` is exactly `(Local, null)`. Passing it by `in` is copy-free because the struct is readonly, and `in` accepts rvalues (`default`) where `ref` would not.

The sent-value evidence rides inside the struct, which covers the transformed-apply case (`SubjectUpdateApplyContext` passes evidence that differs from the written value) with no extra parameter.

### Executor

```csharp
// before
bool SetPropertyValue<T>(string name, T newValue, T currentValue, Action<IInterceptorSubject, T> writeValue);
// after
WriteOutcome SetPropertyValue<T>(string name, T newValue, T currentValue, in AttemptedOrigin attempted, Action<IInterceptorSubject, T> writeValue);
```

The executor stores `attempted` into the write context at construction and assembles `WriteOutcome(context.IsWritten, context.ValueUnchanged)` after the chain returns. The internal cascade overload (pre-resolved raw timestamp) changes the same way.

### PropertyWriteContext

Both constructors take `in AttemptedOrigin` and store it in the existing `_attempted` field. The `PendingOrigin.TryConsume` side effect is deleted from both. `Origin`, `FinalizeOrigin` (demote-on-mismatch at the terminal write), and the timestamp machinery are untouched; they already read `_attempted` and do not care how it arrived.

The context gains one mutable bool, `ValueUnchanged`. `PropertyValueEqualityCheckHandler` replaces its thread-static `SetOutcome` calls with `context.ValueUnchanged = true` in the suppression branch (nothing in the written branch; the field defaults to false). The context already flows by ref through the chain; this is the same mechanism `IsWritten` uses.

### Outcome matrix (structural, no special code)

| Case | Outcome returned | Correction candidate? |
|---|---|---|
| Written normally | `(true, false)` | no |
| Equality-suppressed | `(false, true)` | yes, if FromSource and evidence diverges |
| `OnChanging` cancels | executor never called; caller sees `default` = `(false, false)` | no |
| Transaction captures | `(false, false)` (equality handler saw differing values) | no |
| No equality handler registered | write lands, `(true, false)` | no |

Same matrix #372 ships, minus `ClearOutcome`, `TryTakeOutcome`, and the "an outcome in hand proves the handler ran" argument, because there is no slot to leak.

## Generated code

The property setter becomes a forwarder; the origin method is the single real setter body (no duplication):

```csharp
set => SetNameWithOrigin(value, default);

private WriteOutcome SetNameWithOrigin(string value, in AttemptedOrigin attempted)
{
    var newValue = value;
    var cancel = false;
    OnNameChanging(ref newValue, ref cancel);
    if (cancel)
        return default;

    var outcome = SetPropertyValue(nameof(Name), newValue, _name, in attempted,
        static (o, v) => ((Person)o)._name = v);

    if (outcome.IsWritten)
    {
        OnNameChanged(_name);
        RaisePropertyChanged(nameof(Name));
    }
    return outcome;
}
```

Local-write performance is preserved by construction: the origin method is emitted with `[MethodImpl(MethodImplOptions.AggressiveInlining)]` so the forwarding setter compiles to the same shape as today's inline setter body. The added cost on a local write (one inlinable non-virtual call plus a 24-byte zero-init for `default(AttemptedOrigin)`) sits at or below the removed cost (the per-write thread-static read and property compare in `TryConsume`). The #366 isolation analysis pinned the measurable per-write cost to the context's `AttemptedOrigin` field, which exists identically on both sides of this refactor. The write micro-benchmarks are the gate; a tripped gate is inspected with BenchmarkDotNet's DisassemblyDiagnoser before shipping. The getter is untouched entirely.

Why a named per-property method (and not a direct executor call from the metadata delegate):

1. The hook/INPC wrapper must run on stamped writes too (correctness: hook clamping, UI updates).
2. The wrapper must exist exactly once, shared by the property setter (Local) and the metadata delegate (stamped).
3. Hooks must remain direct calls so unimplemented partial methods compile away to zero cost; hoisting them into delegates would add per-write invocation cost even when no hook exists.
4. A single per-subject dispatch method (switch over names) would force the property setter to box every local write; per-property methods keep the Local path fully typed and inlineable, identical to today's codegen cost.

Visibility: **private for non-virtual properties, `protected virtual` for virtual ones** (the virtual case is derived below). In the private case the only caller is the metadata delegate, generated in the same class. This avoids PublicApi snapshot churn across subject libraries, avoids collision engineering, and avoids the footgun of users stamping origins directly while silently bypassing `SetValueFromOrigin`'s timestamp scoping and correction detection. Origin-stamped writes keep exactly one public front door: the intent-level APIs.

Virtual properties: verified (empirically) that the generated `DefaultProperties` merge is last-wins, and derived entries precede the base concat, so the base class's metadata entry wins for an overridden property. Today a stamped write reaches the derived override only via virtual dispatch through the property setter. The origin method must therefore carry the virtualness: for `virtual` partial properties it is emitted `protected virtual`, and generated overrides emit `override`. A private method here would be a bug (stamped writes would run the base method against the base backing field while reads go through the derived getter). `VirtualPropertyPolymorphismTests` passing unchanged is the tripwire.

Two consequences to document:

1. Hand-written overrides of a virtual partial property change behavior: the property setter only sees local writes; stamped writes enter through the virtual origin method. The customization point becomes "override the origin method, not the setter" (both write kinds then converge on it, since the base setter forwards into the virtual method). This is the design's one semantic regression and it is new relative to master. Precise scope: it needs a generated virtual partial property, a non-generated subclass overriding the property setter with custom logic, and a source stamping that property. The failure mode is value-safe: the stamped write runs the base origin method against the real storage, so values stay consistent; only the override's added side logic is skipped on stamped writes. Mitigation: an analyzer diagnostic in the generator package flags a class that overrides the setter of a virtual subject property without overriding its origin method, turning the silent pitfall into a compile-time warning in the consumer's project.
2. `protected virtual` members are public API on unsealed classes, so virtual partial properties in shipped subject libraries gain a visible origin method in their inheritance surface. Non-virtual properties stay fully private with no API churn.

Rejected alternative: flipping the metadata merge so the derived entry wins (dictionary-level dispatch, keeping the method private). It changes duplicate-key semantics existing code relies on and does nothing for hand-written overrides, which have no generated metadata entry.

Naming: `Set{Name}WithOrigin` is the working name (private, so low-stakes; must not collide with plausible user methods such as `Set{Name}`).

Metadata delegate (named type; `Func` cannot express `in` parameters):

```csharp
public delegate WriteOutcome SubjectPropertySetter(IInterceptorSubject subject, object? value, in AttemptedOrigin attempted);
```

- Intercepted partial properties: `(o, v, in attempted) => ((Person)o).SetNameWithOrigin((string)v, in attempted)`
- Non-intercepted properties: assign through the plain setter, return a written outcome; origin is meaningless without an interception chain, matching today.
- Init-only properties: the metadata setter lambda is gated on `HasSetter`, not `HasInit`, so intercepted init-only properties have null `SetValue` today and sources cannot write them. The origin method is still emitted (it is the shared body; the `init` accessor forwards to it) but stays unwired from metadata, so no new mutation path appears.
- Interface default-implementation properties (`IsFromInterface`, `isIntercepted: false`): the delegate assigns through the interface cast and drops the origin, returning a written outcome. This is behavior-preserving: today a stamped write to such a property arms the pending stamp, no write context ever consumes it, and the scope discards it, so the origin is already silently lost. No origin method is emitted (the class owns no accessor body). Pin with a test so the behavior is not accidentally changed.

Emission matrix for the remaining shapes (modifier-mirroring only, no special logic): abstract properties are already excluded by the extractor; setter access modifiers are irrelevant to a private same-class method; `sealed override` and `new`-hiding mirror the property's modifiers onto the origin method (`new`-hidden non-virtual properties keep today's divergent base-entry dispatch, preserved not fixed); `[Derived]` get-only properties emit no origin method (recalculation rides the internal cascade path with a default attempted origin) while derived-with-setter gets a normal one and relies on the existing unconditional demote in `FinalizeOrigin`; generic subject classes need only snapshot coverage; `required` is orthogonal; the three `RaisePropertyChanged` emission variants move into the origin method unchanged.

The generated `SetPropertyValue` helper mirrors the executor signature; the `_context is null` fallback writes the field directly and returns a written outcome.

## Callers

### SetValueFromOrigin (intent-level core)

All three thread-static interactions disappear (`ClearOutcome`, `PendingOrigin.Set` with its scope, `TryTakeOutcome`; the write-timestamp baseline already moved off this hot path upstream, into the rare correction path inside the detection helper):

```csharp
WriteOutcome outcome;
using (SubjectChangeContext.WithTimestamps(changedTimestamp, receivedTimestamp))
{
    outcome = property.Metadata.SetValue?.Invoke(property.Subject, value,
        new AttemptedOrigin(origin, sentValue)) ?? default;
}

if (origin.Kind == ChangeOriginKind.FromSource && !outcome.IsWritten && outcome.ValueUnchanged)
    DetectAndEnqueueCorrection(property, origin.Source!, sentValue, changedTimestamp);
```

`DetectAndEnqueueCorrection` (including its internal concurrency baseline and fresh-timestamp synthesis) is untouched; it already takes `sentValue` and the inbound changed timestamp. Note the coherence: `sentValue` serves both origin survival (via `AttemptedOrigin` in the write context) and divergence judgment (in detection), and the explicit design threads it exactly once. Public signatures (`SetValueFromSource`, both `SetValueFromOrigin` overloads) are unchanged, so OPC UA, MQTT, WebSocket, and update-applier call sites do not move.

`FinalizeOrigin` carries null-survival semantics (a null sent value survives only against a null stored value, so a source clearing a nullable property keeps its origin). The refactor changes how the attempted origin arrives, never that logic; the implementation plan must move it verbatim.

### Transaction replay

The Local-versus-stamped branch (added for performance in 87666bf9) collapses to one line, because constructing an `AttemptedOrigin` is free for every origin kind:

```csharp
metadata.SetValue?.Invoke(change.Property.Subject, newValue, new AttemptedOrigin(change.Origin, newValue));
```

### Registry dynamic properties and the boxed path

- `RegisteredSubject.AddProperty`: the metadata wrapper threads the parameter through to the executor (one line). The user-facing `AddProperty` signature survives untouched; its `setValue` is the raw terminal-write delegate and stays an `Action`.
- `PropertyReferenceExtensions.SetPropertyValueWithInterception` (public, boxed): gains `in AttemptedOrigin`, returns `WriteOutcome`. In-repo callers pass `default`. The internal cascade overload (derived recalculations) passes `default` and stops paying today's TLS consume.

### Dynamic subjects (the one real wrinkle)

`DynamicSubjectFactory` builds metadata `SetValue` from reflection (`PropertyInfo.SetValue`), which tunnels: reflection, then Castle proxy setter, then `Intercept`, then executor. The thread-static mailbox passed under that tunnel; an explicit parameter cannot pass through `PropertyInfo.SetValue`.

Fix mirrors the generated-subject pattern: dynamic proxies get one origin-accepting entry of their own. The metadata delegate stops using reflection and calls a method on the dynamic-subject side (reachable via the proxy) that goes straight to the executor with the origin and the dictionary-backed read/write closures, exactly what `Intercept` already does for plain setter calls. Local writes through the proxy setter keep their current path and pass `default`. The plan must verify details against `DynamicSubject.cs` (how the metadata delegate reaches the interceptor's backing store); the shape stays inside the Dynamic assembly.

## Deletions

- `PendingOrigin.cs` whole: the pending frame (`HasValue`, `Target`, `Attempted`), `Set`/`TryConsume`/`Restore`, `PendingOriginScope`, and the outcome slot (`SetOutcome`/`ClearOutcome`/`TryTakeOutcome`).
- The constructor side effect (and its warning comment) in both `PropertyWriteContext` constructors.
- The clear-on-entry and outcome-read logic plus their hazard comments in `SetValueFromOrigin`.
- The equality handler's thread-static coupling and the "structural, not checked" dependency argument.
- The 87666bf9 Local-replay special case.
- `PendingOriginTests` and the outcome-slot mechanism tests.
- The "pending" stage of the documented origin lifecycle (docs simplify to attempted then finalized).

`AttemptedOrigin` survives, promoted to public.

## Breaking changes

| API | Change |
|---|---|
| `IInterceptorExecutor.SetPropertyValue<T>` | gains `in AttemptedOrigin`, returns `WriteOutcome` instead of `bool` |
| `SubjectPropertyMetadata.SetValue` (and constructors) | `Action<IInterceptorSubject, object?>` becomes `SubjectPropertySetter` |
| `PropertyWriteContext<T>` constructor | gains `in AttemptedOrigin`; TLS-consume side effect removed; gains mutable `ValueUnchanged` |
| `PropertyReferenceExtensions.SetPropertyValueWithInterception` | gains `in AttemptedOrigin`, returns `WriteOutcome` |
| New public types | `WriteOutcome`, `AttemptedOrigin`, `SubjectPropertySetter` |

Deliberately unchanged: `SetValueFromSource`, both `SetValueFromOrigin` overloads, `ApplySubjectUpdate`, `RegisteredSubject.AddProperty`, `IWriteInterceptor`, `ChangeOrigin`, `SubjectPropertyChange`.

Who breaks: implementers of `IInterceptorExecutor`, direct constructors of `SubjectPropertyMetadata`, hand-written subjects and test fakes calling the executor. Hand-written subjects remain feasible: the contract is the delegate signature plus the executor method, and a hand-writer either points the metadata delegate straight at the executor (no hooks; the fast path `DynamicSubject` uses today) or writes the same forwarder pattern the generator emits (hook logic present). A delegate that drops the origin degrades benignly: that subject's writes all publish as Local, corrections stop firing for it, and its source sees one redundant echo per inbound write; no wrong values.

PublicApi `.verified.txt` snapshots are updated for the core library.

## Test strategy

- All behavioral tests from #366/#372 pass unchanged (they are mechanism-agnostic by design). Any behavioral test that needs edits is a red flag to investigate, not patch.
- `PendingOriginTests` and outcome-slot mechanism tests are deleted with their subject. `OriginWriteContextTests` is triaged per test: origin-semantics tests survive (re-expressed through the constructor parameter where needed); mailbox-mechanics tests (frame restore, steal prevention, clear-on-entry) die with the mechanism.
- New tests: executor returns the correct `WriteOutcome` for the five-case matrix; a dynamic-proxy stamped write keeps its origin (guards the Dynamic wrinkle); generator snapshot tests updated for the new emission.

## Verification

- Full unit suite (`Category!=Integration`), plus OPC UA and MQTT integration tests since connector-adjacent plumbing moved.
- Benchmarks via `pwsh scripts/benchmark.ps1 -Stash`: expect local-write cost at or below the current branch (TryConsume gone from every write), stamped writes slightly cheaper, nothing regressed. The issue predicts local writes return to the pre-#366 baseline; the #366 isolation analysis and the PR #372 benchmark comment contradict that, attributing the residual fixed per-write cost to the `AttemptedOrigin` field in `PropertyWriteContext` (which this design keeps) rather than the thread-static read. Expected outcome: the TLS portion is recovered, WriteNoOp stays a few percent above pre-#366, and the refactor is justified by simplification plus the stamped-path and replay wins, not by the write micro-path.
- Docs: origin-lifecycle documentation drops the pending stage; `PendingOrigin` design commentary removed.

## Open items for the implementation plan

1. Pin init-only and interface-default-property behavior with snapshot and behavioral tests (design-time analysis done: init-only has null `SetValue`; interface defaults silently lose the origin today and keep doing so).
2. Verify how the dynamic-proxy metadata delegate reaches the interceptor's backing store; pick the concrete shape.
3. Confirm the origin-method name (`Set{Name}WithOrigin`) against real subject code for collisions.
4. Decide `WriteOutcome` construction shape (constructor vs factory) during implementation; two bools, no more.
5. Analyzer diagnostic for hand-written setter overrides of virtual subject properties (see the virtual-properties section). Decide whether it ships in this PR or as an immediate follow-up; the documentation caveat lands in this PR either way.
6. Sweep for direct `PropertyWriteContext` constructions outside the runtime (benchmarks, test fakes) and for executor or metadata-delegate usage in the extension packages (AspNetCore, GraphQL, Blazor); expected clean but must be verified, not assumed.
7. Add a `SetValueFromSource` micro-benchmark before the refactor: the existing suite measures local writes and outbound source paths, but the inbound stamped path is where the thread-static operations disappear, and without a benchmark there the PR cannot demonstrate its own perf claim.
