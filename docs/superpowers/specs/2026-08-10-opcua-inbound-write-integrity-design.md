# OPC UA server: inbound write integrity

**Goal:** the node a client reads never holds a value the subject model does not, and a client whose value was not kept is told so.

**Base:** stacked on the array ownership PR, its branch parent. Requires the client retry PR to have merged first: until then any bad status this server returns stalls a Namotion client's outbound stream (`WriteRetryQueue.cs:152-172`, `SubjectSourceBase.cs:290-295`).

## The invariant

At quiescence, for every mapped property, `node.Value == ConvertToNodeValue(subject value)`. No exceptions.

## The defect

The SDK commits a client's write into the node, then raises `StateChanged`, where `UpdateProperty` tries to apply it to the subject. Every way the apply can fail leaves the node serving a value the model rejected.

| How the apply fails | Reachable when | Node holds | Subject holds |
|---|---|---|---|
| Property not registered, silent `return` | Detach or structural mutation in flight | B | A |
| `ConvertToPropertyValue` throws | Client writes a value the converter rejects | B | A |
| `SetValueFromSource` throws | A validation interceptor rejects the value | B | A |
| An `OnChanging` hook sets `cancel` | Generated hook vetoes the write | B | A |
| Converter pair does not round-trip | Any scaling, unit or enum mapping converter | B | g(B) |

The node keeps the client's value, so a read-back confirms a write that never landed. And monitored items fire from the SDK's `OnStateChanged` field before the `StateChanged` event the apply hangs off (`NodeState.cs:2635-2636`), so subscribers see the refused value before the model is asked.

A hook that **transforms** rather than cancels is not in the table: `SetValueFromOrigin`'s survival check demotes the origin to Local when the stored value differs from what the source sent (`SubjectChangeContextExtensions.cs:28-41`), which un-suppresses the echo and lets the outbound loop repair the node.

**None of these are caused by #420.** For the round-trip row, `SetValueFromSource` passes one object as both value and survival evidence (`SubjectChangeContextExtensions.cs:26`), so the survival check passes, the origin stays `FromSource(server)`, and `ChangeQueueProcessor.cs:214` drops it. That was true before #420 and after it. An earlier revision of this spec argued this from the wrong evidence, claiming the filter predated #420 (its `NeedsWriteBack` conjunct did not) and that the server's own apply never reaches the loop (a demoted origin does). The conclusion holds, the reasoning did not. Epic #442's finding 3 carries the same wrong claim and is corrected there.

## Design

Nodes are constructed at one site (`OpcUaNodeFactory.cs:227`), so they are ours to subclass. Add `SubjectVariableState : BaseDataVariableState` holding the server and the registered property in readonly fields, and override `WriteValueAttribute` (`protected virtual` on `NodeState.cs:4119`, overridden non-sealed on `BaseVariableState.cs:1906`).

```
if the property is not registered
    return BadNoCommunication                 // before base runs, so the node is never touched

result = base.WriteValueAttribute(...)        // type check, index range merge, assignment
if (result is bad) return result

requested = null
try {
    requested = ConvertToPropertyValue(copy(this.Value))
    SetValueFromSource(server, this.Timestamp, now, requested)
}
catch (e) { log }                             // no restore, no conclusion drawn

modelValue = registeredProperty.GetValue()    // unconditional
Value      = copy(ConvertToNodeValue(modelValue))
StatusCode = StatusCodes.Good
ClearChangeMasks(context, includeChildren: false)

return Equals(modelValue, requested) ? Good : BadOutOfRange
```

### The comparison decides the status, never the value

This is the whole design and the distinction that makes it sound. The node is set to the model's value **unconditionally**, so it can never serve a value the model rejected regardless of what the comparison says. A wrong comparison mis-picks a status code; it cannot move data.

That is what separates this from the read-back comparison rejected earlier in design, where the comparison would have gated whether to keep the client's value and a wrong answer would have manufactured divergence.

It also closes the cancel case, which two earlier revisions gave up on. A cancelled write leaves the model at A, `A != requested`, so the client is told Bad. No signal from the apply is needed.

### Why not discriminate on which block threw

An earlier revision used two `try` blocks and concluded "the model never took it" from a throw in the first. That is unsound, and it is a regression rather than an incomplete fix. The generated setter commits inside `SetPropertyValue` and *then* runs user code unguarded (`SubjectCodeGenerator.cs:345-349`: `OnXChanged`, `RaisePropertyChanged`), as do the change observers (`PropertyChangeSubscription.cs:138`). So a post-commit throw reaches that catch with the model already updated. Restoring on it would leave the node at A, the model at B, and the client told Bad for a write that landed. Today's code catches and logs, leaving both at B, which is consistent.

### Accepted caveats of the comparison

- Equality is over property-space values. For arrays the model stores the very instance passed in, so reference equality holds.
- A derived property with a setter never round-trips and would always answer Bad. Deliberate: the client's value genuinely is not what the model holds.
- A concurrent local write between the apply and the read-back yields a false Bad, which the client retries. The design already inherits that race for the value it publishes.

### Details that are load bearing

**The registration check moves ahead of `base`.** That is what removes the snapshot and restore entirely: an unregistered property has nothing to read back and nothing to convert, so the only correct action is to refuse before the node is touched. `BadNoCommunication` rather than `BadNodeIdUnknown` because the condition is transient, and it is a legal Write result unlike `BadOutOfService`.

**`ClearChangeMasks` runs on every path**, not only the bad one, so the corrected value is published whatever the answer. `CustomNodeManager.Write` skips its own flush when the result is bad (`:2022-2025`), and its good-path flush at `:2051` is additionally gated on the monitored item manager type (`:2048`); ours is unconditional. Pass `includeChildren: false`.

**Timestamp from `this.Timestamp`, not the parameter.** A client omitting `SourceTimestamp` sends `DateTime.MinValue` with `Kind = Unspecified`, forwarded unnormalized (`NodeState.cs:3775-3781`); converting that to `DateTimeOffset` throws on any positive-UTC-offset host. Base normalizes its own copy (`BaseVariableState.cs:1964-1968, 2048`).

**Apply `this.Value`, not the `value` parameter.** For an index range write the parameter is only the client's fragment.

**The array copies** come from the branch parent's ownership rule.

### What gets deleted

- `IsWritingOwnNodeValues` (`:44`) and `SelfWrittenNodeValue` (`:55`), two `[ThreadStatic]` fields with roughly eighteen lines of comment
- their seven touch points (`:44, :55, :167, :192, :200, :201, :411`)
- the `try`/`finally` in the outbound loop, which exists only to disarm the flag
- the `StateChanged` subscription (`CustomNodeManager.cs:408-416`), its only subscriber
- `SelfEchoReproTests.cs` in full. Both its tests pin a race that cannot exist once a client write no longer travels through `ClearChangeMasks`; rewriting them would manufacture a scenario the code no longer has.

Verified: one reader of the fields (`:411`), one caller of `UpdateProperty` (`CustomNodeManager.cs:414`), one subscriber (`:408`). Node values are written at creation, in the outbound loop through the property setter, and by the SDK write service (`NodeState.cs:3775`, callers `CustomNodeManager.cs:2008`, `:2229`, and the unreached-but-public `NodeState.WriteChildAttribute:4522`). Monitored items use the SDK's own `OnStateChanged` field. Deleting the subscription also removes a hazard: `RemoveSubjectNodes`' `DeleteNode` (`:156, 168`) currently reaches `UpdateProperty` through it.

Nothing clones these nodes, so the `MemberwiseClone` constraint does not apply and the dependencies live in readonly fields: the only `.Clone()` sites on `NodeState` are `:97` and `:194`, neither reachable for predefined nodes.

### Documentation this PR must carry

`docs/design/connector-delivery.md:60-64` argues the `SourceValuesAreSettled` soundness case in terms of the `StateChanged` path this PR deletes. The conclusion survives; the mechanism becomes "the write attribute settles the node before the lock releases". Also `OpcUaSubjectServer.cs:20-24` and `OpcUaServerSelfWriteTests.cs:10-15`, both of which name the deleted handler.

## Why not either event hook

`OnWriteValue` (`BaseVariableState.cs:1930`) returns early at `:1961`, skipping the type check (`:1971-2000`) and index range merging (`:2031-2043`): a string written to an int node would go from `BadTypeMismatch` to Good, and an index range write would apply the fragment as the whole value. `OnSimpleWriteValue` (`:2010`) refuses index range writes with `BadIndexRangeInvalid` (`:2016-2018`) and carries no `sourceTimestamp` (`NodeState.cs:5011`), unrecoverable because `m_timestamp` is not assigned until `:2048`.

## Gaps this leaves

- **A permanently Bad-answering property freezes the client's view** of it, with nothing exposing node quality in diagnostics. `OpcUaServerDiagnostics` has no such surface and [#299](https://github.com/RicoSuter/Namotion.Interceptor/issues/299) is out of scope.
- **The client's own `DataValue.StatusCode` is discarded**, since we always write Good. Defensible under the invariant, and a behaviour change worth a release note.
- **Model reads now run under `NodeManager.Lock`**, so a derived getter takes that lock plus its own. Same ordering the write already establishes, so no new inversion.
- **Late-attached subjects still get no node** (finding 1 in #442).
- **The `NodeManager.Lock` / `_structureLock` ordering hazard remains** (`CustomNodeManager.cs:137, 144`).

## Expected size

Deletions about 38. Additions: the subclass and override 55-70, factory threading 8-12, the reshaped apply 15-20, doc and comment updates 5-8. **Net plus 55 to 75 production lines.** Eight tests, most on the live harness, 350-450 test lines.

## Acceptance criteria

1. The invariant holds on every path.
2. Type checking, index range writes and client source timestamps keep working; a client omitting `SourceTimestamp` still succeeds.
3. `SelfEchoReproTests.cs` is deleted and `OpcUaCrossStoreConvergenceTests.cs:56-60` is rewritten to drive a real client write. The latter is load bearing as the only test pinning why the server selects `SourceValuesAreSettled`.
4. `Server/OpcUaServerDeliveryRuleTests.cs:44-63` still passes.
5. No performance regression.

## Test plan

Written red first.

| Test | Passes when |
|---|---|
| `WhenValidationRejectsAClientWrite_ThenTheNodeKeepsTheModelValueAndTheClientIsTold` | read-back returns A, subject holds A, client receives Bad |
| `WhenAnOnChangingHookCancelsAClientWrite_ThenTheNodeKeepsTheModelValueAndTheClientIsTold` | same. This is the case two earlier designs could not answer |
| `WhenTheInboundConverterThrows_ThenNoExceptionReachesTheSdkAndTheNodeKeepsTheModelValue` | server still serves reads, read-back returns A |
| `WhenTheConverterPairDoesNotRoundTrip_ThenTheNodeHoldsTheConvertedModelValue` | read-back equals the conversion of the subject's value, stable across two reads |
| `WhenThePropertyIsNotRegistered_ThenTheWriteIsRefusedBeforeTheNodeIsTouched` | node value unchanged, transient status returned |
| `WhenPostCommitUserCodeThrows_ThenBothStoresStillHoldTheWrittenValue` | pins the regression the two-`try` design would have introduced |
| `WhenAClientOmitsTheSourceTimestamp_ThenTheWriteSucceeds` | pins the timestamp fix |
| `WhenAClientWriteIsAccepted_ThenBothStoresHoldItAndTheClientReceivesGood` | the happy path |

Not included, because they already exist or arrive with the parent: the index range regression guard (branch parent's spec), and `OpcUaServerSelfWriteTests.WhenTheServerWritesItsOwnNodes_ThenNothingIsAppliedBackToTheSubject`.

Conventions: `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange`, `// Act`, `// Assert`, `AsyncTestHelpers.WaitUntilAsync` rather than delays. The OPC UA suite binds a fixed port and cannot run concurrently with itself or the connector tester.

## Performance

**Inbound, per client write:** one model read and one conversion outward. Against a network round trip, noise.

**Outbound, per change:** the thread-static store, the `StateChanged` dispatch and the `Equals` guard all go. Nothing is added.

**Verification:** connector tester `opcua-load` two-process against master, CPU pinned; `cycles.csv` HeapMB for leaks; core benchmarks as a tripwire. The parent PR's throughput accounting fix must be in place first.

## Risks

- **Reliance on base behaviour**, that `base.WriteValueAttribute` assigns before returning Good (`BaseVariableState.cs:2046-2048`). Behaviour, not contract.
- **A throw from `ClearChangeMasks` or the read-back** escapes into `NodeState.WriteAttribute`'s catch and becomes `BadUnexpectedError` (`:3773-3788`), leaving the node at the model's value or the client's. Contained, not silent.
- **Reading the model under `NodeManager.Lock`.**
