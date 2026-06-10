# Hook Cascade and INPC Null-Source Publishing: Design (#345)

This document is the agreed design for issue #345. It revises the consequence-write source semantics that #343 documented and pinned: generated property hooks and INPC handlers become local-origin, matching the policy that derived recalculations already implement.

## Problem

The ambient `SubjectChangeContext` is thread static. When a value is applied under a source scope (inbound `SetValueFromSource`, or a transaction commit apply after #343), any property write made synchronously inside that apply inherits the scope. For the two kinds of framework-invoked callbacks this produces opposite outcomes:

| Consequence write | Notification `Source` | Pushed to the owning source? |
|---|---|---|
| `OnXChanging`/`OnXChanged` cascade write | inherited (the source) | no, suppressed as an echo |
| Derived recalculation | always `null` (explicit reset) | yes, always |

The cascade row means a hook-computed value never reaches a source it is bound to when the trigger came from that source. The local model and the source silently diverge on that property (divergence row 6 in #342). INPC handlers that write back into the model inherit the scope as well, including the existing inconsistency that `DerivedPropertyChangeHandler` raises INPC outside its own null-source scope.

## Rule

`Source` on a change notification marks exactly the values a source confirmed (transaction stage 1) or sent (inbound). Everything the local model computes is local-origin (`Source = null`) and flows to bound sources like any local write.

Concretely, three callback sites become uniformly local-origin:

- `OnXChanging` / `OnXChanged` partial hooks: cascade writes inside them publish with `Source = null`.
- INPC (`PropertyChanged`) handlers: write-backs inside them publish with `Source = null`.
- Derived recalculations: already publish with `Source = null`, unchanged.

The triggering write itself keeps its ambient scope; only the callback bodies are re-scoped. `SubjectChangeContext.WithSource(null)` resets only the source and preserves the ambient timestamps, so cascade timestamp sharing keeps working.

Cost principle: nobody pays for what they do not use. Hook scopes are emitted only for properties whose hooks are actually implemented (static fact, detected by the generator). The INPC scope is entered only when a subscriber exists (runtime fact, the existing null check).

## Generator changes

All code changes are in `Namotion.Interceptor.Generator`. `SubjectChangeContext` lives in the core `Namotion.Interceptor` package, so generated code can reference it directly.

### Hook implementation detection (SubjectMetadataExtractor)

While iterating the partial class declarations it already walks, the extractor also collects implementing partial method bodies named `On{Property}Changing` and `On{Property}Changed` (a `MethodDeclarationSyntax` with a body or expression body). Two new booleans on `PropertyMetadata` record the result per property.

Matching is by name only, deliberately over-approximate: a false positive costs one redundant scope around a call the compiler erases, while a false negative would silently restore source inheritance for that hook. Incrementality is safe because any edit to a partial declaration of the class retriggers extraction.

### Setter emission (SubjectCodeGenerator.EmitProperty)

Each implemented hook call is wrapped in its own null-source scope. Unimplemented hooks keep the bare call so the compiler erases it and the property pays nothing:

```csharp
set
{
    var newValue = value;
    var cancel = false;
    using (SubjectChangeContext.WithSource(null))   // only emitted when OnXChanging is implemented
    {
        OnXChanging(ref newValue, ref cancel);
    }
    if (!cancel && SetPropertyValue(nameof(X), newValue, _x, ...))
    {
        using (SubjectChangeContext.WithSource(null))   // only emitted when OnXChanged is implemented
        {
            OnXChanged(_x);
        }
        RaisePropertyChanged(nameof(X));
    }
}
```

### INPC raise (EmitNotifyPropertyChangedImplementation)

The scope goes inside the generated `RaisePropertyChanged`, after the subscriber null check, so subjects without INPC subscribers pay nothing beyond the existing check:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
protected void RaisePropertyChanged(string propertyName)
{
    var handler = PropertyChanged;
    if (handler is null)
    {
        return;
    }

    using (SubjectChangeContext.WithSource(null))
    {
        handler.Invoke(this, PropertyChangedEventArgsCache.Get(propertyName));
    }
}
```

Per-base-class behavior of the raise call in the setter:

- Base is a generated subject: the inherited `RaisePropertyChanged` is already wrapped in the base class's generated code; nothing extra is emitted.
- Base manually implements `IRaisePropertyChanged` (rare): the user's method cannot be modified, so the interface-cast call site in the setter is wrapped instead.
- Own implementation: wrapped internally as shown above.

The existing inconsistency that `DerivedPropertyChangeHandler.NotifyDerivedPropertyChanged` raises INPC outside its null-source scope fixes itself: the raise goes through `IRaisePropertyChanged` into the wrapped generated method. No `Namotion.Interceptor.Tracking` changes are needed.

## Resulting behavior changes

- Transaction commit: a hook cascade during the apply now publishes `Source = null` and is delivered to its bound source by the background queue. Previously it inherited the confirming source and was silently suppressed. Writing the cascade value explicitly into the transaction remains the way to get confirmed, atomic delivery; it is now optional for delivery at all.
- Inbound: a cascade triggered by an inbound source value is pushed back to the bound source instead of silently diverging. Divergence row 6 in #342 disappears.
- Echo loops are bounded by the value-equality short-circuit. Persistent loops require value-changing oscillation (for example lossy conversions), the same accepted risk class as derived recalculation ping-pong. Documented, not mitigated further.
- No public API change. Generated-code shape changes only for subjects with implemented hooks or INPC subscribers.

## Tests

- Flip the two #343-pinned cascade tests in `SubjectTransactionEchoSuppressionTests` (`WhenCommitAppliedChangeTriggersCascade_ThenCascadeInheritsSourceScope` and `WhenInboundSourceValueTriggersCascade_ThenCascadeInheritsSourceScope`) to expect `Source = null` and delivery. The derived recalculation test stays unchanged.
- Add a pump-level inbound cascade delivery test: a value arrives from the source, the hook cascade write is pushed back out by the change queue.
- Add a pinning test that an INPC handler write-back during a source-scoped apply publishes `Source = null`.
- Generator tests: extend `PropertyHooksTests`, refresh affected snapshots, and pin that a property without implemented hooks generates no scope (the pay-nothing guarantee).

## Documentation

- `connectors.md`: update the "Change notification source semantics" section; the cascade row becomes null-source and an INPC row is added.
- `tracking-transactions.md`: replace the stage 2 cascade-inheritance rule.
- `generator.md`: note the null-source scope where hooks are described.
- Callback-purity guidance: INPC handlers should react, not mutate; if they do mutate, the writes are local-origin. Re-entrancy and stack-overflow risks of mutating from change handlers exist regardless of source semantics.

## Forward compatibility: ChangeOrigin (#342)

The null-source scope is the placeholder encoding of "local origin". When #342 introduces the typed `ChangeOrigin` discriminator, the semantic decision here stands: a hook-computed or INPC-written value is neither sent by a source (`FromSource`) nor accepted by one in stage 1 (`Confirmed`), so its only truthful kind is `Local`, and routing follows from the kind. What the discriminator improves is the encoding. `WithSource(null)` fixes routing by erasing information: the fact that a cascade was triggered by a value from a specific source is real provenance that null throws away. A future local-origin scope can set `Kind = Local` while retaining the triggering source as causal metadata, useful for diagnostics, loop analysis, and provenance-aware validation (which should validate locally computed values strictly, unlike source truth).

The generated callback scopes in this design mark exactly the boundary "framework-invoked callback body starts here", which the write pipeline cannot detect on its own; that boundary is where the future swap happens. Expected rework when `ChangeOrigin` lands: one scope API call in the generated code, test assertions moving from `Source == null` to `Kind == Local`, and the documentation tables. No redesign.

## Out of scope

- Arbitrary user code running inside a `WithState` scope (for example code called from a custom source implementation) keeps ambient inheritance. The rule is scoped to framework-invoked consequence callbacks.
- Lifecycle handlers stay as they are.
- #346 (flush-time echo filter) and #340 (convergence on failed revert) are orthogonal.

## Relationship to other issues

- #343 defined and pinned the current semantics; this design revises the cascade half after the derived half turned out to already implement the proposed policy.
- #342 question 8 (consequence-write policy): this is the concrete answer for hook cascades and INPC; divergence row 6 disappears.
- Lands after PR #344, on a fresh branch off master.
