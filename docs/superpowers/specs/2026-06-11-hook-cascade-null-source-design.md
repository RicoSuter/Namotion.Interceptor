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

The triggering write itself keeps its ambient scope; only the callback bodies are re-scoped. `SubjectChangeContext.WithLocalOrigin()` resets the source to null and preserves the ambient timestamps, so cascade timestamp sharing keeps working.

Cost principle: nobody pays for what they do not use. Hook scopes are emitted only for properties whose hooks are actually implemented (static fact, detected by the generator). The INPC scope is entered only when a subscriber exists (runtime fact, the existing null check).

## Scope API: WithLocalOrigin()

`SubjectChangeContext` gains a parameterless `WithLocalOrigin()` that resets the source to null while preserving the ambient changed and received timestamps (the same state `WithSource(null)` produces). The generator and the derived handler call it instead of `WithSource(null)` directly, so "this callback body is local-origin" is expressed as intent rather than as the `null` mechanism.

It is the forward-compatibility seam for the typed `ChangeOrigin` discriminator (#342, question 9). The helper already captures the ambient source (it must, to restore it on dispose), so when `ChangeOrigin` lands it can set `Kind = Local` and retain that source as causal metadata without changing its signature or any call site. An XML doc comment on the method records that it is the `ChangeOrigin.Kind.Local` seam.

`DerivedPropertyChangeHandler.NotifyDerivedPropertyChanged` (the one existing `WithSource(null)` call site) migrates to `WithLocalOrigin()` as well, so there is a single local-origin idiom and a single point the `ChangeOrigin` migration has to touch.

## Generator changes

Apart from the core `WithLocalOrigin()` helper above and the one-line derived-handler migration in `Namotion.Interceptor.Tracking`, all code changes are in `Namotion.Interceptor.Generator`. `SubjectChangeContext` lives in the core `Namotion.Interceptor` package, so generated code can reference `WithLocalOrigin()` directly.

### Hook implementation detection (SubjectMetadataExtractor)

While iterating the partial class declarations it already walks, the extractor also collects implementing partial method bodies named `On{Property}Changing` and `On{Property}Changed` (a `MethodDeclarationSyntax` with a body or expression body). Two new booleans on `PropertyMetadata` record the result per property.

Matching is by name only, deliberately over-approximate: a false positive costs one redundant scope around a call the compiler erases, while a false negative would silently restore source inheritance for that hook.

Incrementality note: the generator's syntax provider only selects class declarations that carry an attribute (`AttributeLists.Count > 0`), so a hook implemented in a separate, attribute-less partial file is not itself a tracked input. Detection stays correct today only because the pipeline captures the `SemanticModel` (and symbols and syntax nodes), which defeats caching and forces extraction to re-run on every build, re-reading all partials via `DeclaringSyntaxReferences`. If the generator is later made properly incremental (by not capturing the `SemanticModel`), hook detection must explicitly track hook-bearing partial files as inputs, or a hook added in a separate file would be silently missed and source inheritance restored for it.

### Setter emission (SubjectCodeGenerator.EmitProperty)

Each implemented hook call is wrapped in its own local-origin scope. Unimplemented hooks keep the bare call so the compiler erases it and the property pays nothing:

```csharp
set
{
    var newValue = value;
    var cancel = false;
    using (SubjectChangeContext.WithLocalOrigin())   // only emitted when OnXChanging is implemented
    {
        OnXChanging(ref newValue, ref cancel);
    }
    if (!cancel && SetPropertyValue(nameof(X), newValue, _x, ...))
    {
        using (SubjectChangeContext.WithLocalOrigin())   // only emitted when OnXChanged is implemented
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

    using (SubjectChangeContext.WithLocalOrigin())
    {
        handler.Invoke(this, PropertyChangedEventArgsCache.Get(propertyName));
    }
}
```

Per-base-class behavior of the raise call in the setter:

- Base is a generated subject: the inherited `RaisePropertyChanged` is already wrapped in the base class's generated code; nothing extra is emitted.
- Base manually implements `IRaisePropertyChanged` (rare): the user's method cannot be modified, so the interface-cast call site in the setter is wrapped instead.
- Own implementation: wrapped internally as shown above.

The existing inconsistency that `DerivedPropertyChangeHandler.NotifyDerivedPropertyChanged` raises INPC outside its local-origin scope fixes itself: the raise goes through `IRaisePropertyChanged` into the wrapped generated method, so the INPC path in `Namotion.Interceptor.Tracking` needs no extra change. The only Tracking change is migrating that handler's existing `WithSource(null)` to `WithLocalOrigin()` (see Scope API above).

## Resulting behavior changes

- Transaction commit: a hook cascade during the apply now publishes `Source = null` and is delivered to its bound source by the background queue. Previously it inherited the confirming source and was silently suppressed. Writing the cascade value explicitly into the transaction remains the way to get confirmed, atomic delivery; it is now optional for delivery at all.
- Inbound: a cascade triggered by an inbound source value is pushed back to the bound source instead of silently diverging. Divergence row 6 in #342 disappears.
- Echo loops are bounded by the value-equality short-circuit. Persistent loops require value-changing oscillation (for example lossy conversions), the same accepted risk class as derived recalculation ping-pong. Documented, not mitigated further.
- Public API gains one method, `SubjectChangeContext.WithLocalOrigin()`; the `Namotion.Interceptor` public API snapshot is re-accepted. Generated-code shape changes only for subjects with implemented hooks or INPC subscribers.

## Tests

`SubjectTransactionEchoSuppressionTests` (added by #344) pins the cascade source-scope behavior this design changes. This branch was created from `2511e6c8`, before #344, so master must be merged in for that file to be present. Then:

- Flip the two cascade tests `WhenCommitAppliedChangeTriggersCascade_*` and `WhenInboundSourceValueTriggersCascade_*` to expect `Source = null` (local origin) instead of inheriting the source. The derived recalculation test in the same file stays unchanged (it already expects `Source = null`). The source-marking tests (the triggering property still carries the confirming source) also stay unchanged.
- Add, in a focused `SubjectCascadeLocalOriginTests`: a pump-level inbound-cascade delivery test (a value arrives from the source; the cascade write to another source-bound property is delivered to the bound source by a `ChangeQueueProcessor` while the inbound value itself is echo-dropped), and an INPC write-back test (an INPC handler that writes back during a source-scoped apply publishes `Source = null`).
- Generator tests: a behavioral pin that an implemented hook runs under a local-origin scope, that a property without implemented hooks pays nothing, and that the manual-`IRaisePropertyChanged`-base raise is wrapped; refresh the standalone-subject snapshots affected by the `RaisePropertyChanged` shape change.

(Note: an earlier draft claimed the test file had been "removed by #341." That was a misdiagnosis caused by the stale branch base; the file is added by #344 and is present once master is merged.)

## Documentation

- `connectors.md`: update the "Change notification source semantics" section; the cascade row becomes null-source and an INPC row is added.
- `tracking-transactions.md`: replace the stage 2 cascade-inheritance rule.
- `generator.md`: note the local-origin scope (`WithLocalOrigin()`) where hooks are described.
- Callback-purity guidance: INPC handlers should react, not mutate; if they do mutate, the writes are local-origin. Re-entrancy and stack-overflow risks of mutating from change handlers exist regardless of source semantics.

## Forward compatibility: ChangeOrigin (#342)

`WithLocalOrigin()` is the placeholder encoding of "local origin"; today its body is `WithSource(null)`. When #342 introduces the typed `ChangeOrigin` discriminator, the semantic decision here stands: a hook-computed or INPC-written value is neither sent by a source (`FromSource`) nor accepted by one in stage 1 (`Confirmed`), so its only truthful kind is `Local`, and routing follows from the kind. What the discriminator improves is the encoding. Setting `Source = null` fixes routing by erasing information: the fact that a cascade was triggered by a value from a specific source is real provenance that null throws away. Because `WithLocalOrigin()` already captures the ambient source, the `ChangeOrigin` version can set `Kind = Local` while retaining that source as causal metadata, useful for diagnostics, loop analysis, and provenance-aware validation (which should validate locally computed values strictly, unlike source truth).

The generated callback scopes in this design mark exactly the boundary "framework-invoked callback body starts here", which the write pipeline cannot detect on its own; that boundary is where the future swap happens. Routing every local-origin scope through `WithLocalOrigin()` keeps that swap to a single method body. Expected rework when `ChangeOrigin` lands: the internals of `WithLocalOrigin()`, test assertions moving from `Source == null` to `Kind == Local`, and the documentation tables. Generated code and its snapshots do not change again. No redesign.

## Out of scope

- Arbitrary user code running inside a `WithState` scope (for example code called from a custom source implementation) keeps ambient inheritance. The rule is scoped to framework-invoked consequence callbacks.
- Lifecycle handlers stay as they are.
- #340 (convergence on failed revert) is orthogonal. #346 (flush-time echo filter) is code-orthogonal (it touches `ChangeQueueProcessor`, not the generator) but release-coupled; see Relationship to other issues.

## Relationship to other issues

- #343 defined and pinned the current semantics; this design revises the cascade half after the derived half turned out to already implement the proposed policy.
- #342 question 8 (consequence-write policy): this is the concrete answer for hook cascades and INPC; divergence row 6 disappears.
- #346 (flush-time echo filter): making hook cascades publish as local origin enrolls cascade-target properties into the set that has both local-origin and source-confirmed writes, which is the precondition for the stale-queued-write race #346 closes. This does not create a new bug, but it broadens that race's reach, so #345 should land in the same release bundle as (or after) #346 rather than alone. The epic (#347) already groups rows 2 and 3 for this reason.
- Lands after PR #344, on a fresh branch off master.
