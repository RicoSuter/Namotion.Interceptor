# OPC UA server: inbound write integrity

**Goal:** the node a client reads never holds a value the subject model does not, and a client whose value the model did not take is told so.

**Base:** master at `f561d196`. Merges before it: the client PR, because this one answers bad statuses on refusal and until that lands a refused property starves a Namotion client's later write batches.

## The invariant

At quiescence, for every mapped property:

- `node.Value == ConvertToNodeValue(subject value)` and the node's status is Good, **or**
- the node's status is `UncertainLastUsableValue`, meaning the model holds a value this server cannot represent.

The node's `SourceTimestamp` reflects the model's write timestamp, not the client's request time.

## The defects

Five ways a client write leaves the node serving a value the model rejected, all pre-existing, none caused by #420.

| How the apply fails | Reachable when | Node holds | Subject holds |
|---|---|---|---|
| Property not registered, silent `return` | Detach or structural mutation in flight | B | A |
| `ConvertToPropertyValue` throws | Client writes a value the converter rejects | B | A |
| `SetValueFromSource` throws | A validation interceptor rejects the value | B | A |
| An `OnChanging` hook sets `cancel` | Generated hook vetoes the write | B | A |
| Converter pair does not round-trip | Any scaling, unit or enum mapping converter | B | g(B) |

The node keeps the client's value, so a read-back confirms a write that never landed, and monitored items fire from the SDK's `OnStateChanged` field before the `StateChanged` event the apply hangs off (`NodeState.cs:2635-2636`), so subscribers see the refused value before the model is asked.

**Row 2 is worse than divergence.** `ConvertToPropertyValue` sits outside the `try` at `OpcUaSubjectServer.cs:424`, `UpdateProperty` is reached from `StateChanged` inside `ClearChangeMasks` at SDK `CustomNodeManager.cs:2051`, and nothing on that path catches. The throw escapes `CustomNodeManager2.Write`, so **every remaining node in the same Write request is never written**.

A hook that *transforms* rather than cancels is not in the table: the survival check demotes the origin to Local when the stored value differs from what the source sent (`SubjectChangeContextExtensions.cs:28-41`), which un-suppresses the echo and lets the outbound loop repair the node.

### A sixth: the SDK mutates the model's array in place

`ConvertToNodeValue` returns the property's array by reference (`OpcUaValueConverter.cs:87`), and the node holds that instance. The SDK's index range merge mutates the destination in place (`NumericRange.cs:763`, plus `:690-693` scalar ByteString, `:904` N-dimensional, `:930-937` nested ByteString).

So a client writing `myArray[2:4]` changes the model's data **from the SDK's write thread under `NodeManager.Lock`, while model readers hold no lock**, and publishes no change through the interceptor chain: no other connector, no derived property, no observer learns of it, and `PropertyValueEqualityCheckHandler` then compares the same reference to itself and short-circuits any later write of that instance. OPC UA subscribers on this server *do* see it, because the notification path deep-clones on read, so the two observers disagree permanently.

That is a data race on model state, not only a silent divergence.

## Design

Nodes are constructed at one site (`OpcUaNodeFactory.cs:227`), so they are ours to subclass. Add `SubjectVariableState : BaseDataVariableState`, holding only the server; the `PropertyReference` comes from `NodeState.Handle`, which already carries it (`CustomNodeManager.cs:372`). Override `WriteValueAttribute` (`protected virtual`, `NodeState.cs:4119`).

```
if (indexRange is not Empty && Value is Array original)     // copy before the merge
    masks = ChangeMasks; Value = CopyForMerge(original)

if (property does not resolve to a registered property)
    return BadNoCommunication                                // node untouched

result = base.WriteValueAttribute(...)                       // type check, merge, assignment
if (result is bad) { restore Value and ChangeMasks if copied; return result }

requested = null; applied = false
try {
    requested = ConvertToPropertyValue(this.Value)
    SetValueFromSource(server, this.Timestamp, now, requested)
    applied = true
}
catch (e) { log }

modelValue = registeredProperty.GetValue()
try {
    Value       = ConvertToNodeValue(modelValue)
    Timestamp   = model write timestamp
    StatusCode  = Good
}
catch (e) { StatusCode = UncertainLastUsableValue; log }

IncomingThroughput.Add(1)
ClearChangeMasks(context, includeChildren: false)

return applied && ValuesMatch(modelValue, requested) ? Good : BadOutOfRange
```

### Copy before the merge, not at every crossing

The merge is the only in-place mutator, and `WriteValueAttribute` is the only route to it. Copying the node's array immediately before `base` hands the merge a private instance, so the model's array is never touched however the node came to hold it.

This establishes the precondition locally at the one site that needs it, rather than maintaining a "never share" invariant across every crossing that would have to be re-proved whenever someone adds a fourth. It also costs **one `Array.Clone` per index range write** and nothing on the outbound path, at node creation, on ordinary writes, or for scalars.

`CopyForMerge` clones the outer array, and additionally clones the inner arrays of a `byte[][]`, because the nested ByteString merge mutates those in place (`NumericRange.cs:930-937`). Do not use `Opc.Ua.Utils.Clone`: it is deep but recurses through `Array.GetValue`/`SetValue`, boxing every element.

The restore on a rejected merge matters because the `Value` setter ORs in the change mask on a reference difference (`BaseVariableState.cs:531-534`) and `CustomNodeManager.Write` skips its flush on a bad result, so a pending mask would otherwise be dispatched later. `ChangeMasks` has a `protected set` (`NodeState.cs:261`).

**Residual risk, stated honestly:** this is correct only while the index range merge remains the only in-place mutator of a node value. The `NumericRange`, read and monitored-item paths were audited; the whole SDK was not. The test asserts the model's array is unmutated rather than asserting the mechanism, so a new mutator on this path fails it.

### The comparison decides the status, never the value

The node is set to the model's value unconditionally, so it can never serve a value the model rejected regardless of what the comparison answers. A wrong comparison mis-picks a status code; it cannot move data. That is what makes a read-back comparison safe here where gating the *value* on one would not be.

It also answers the `OnChanging` cancel case, which needs no signal from the apply: a cancelled write leaves the model at A, which does not match what was requested.

`applied` guards a false Good: if the inbound conversion throws, `requested` stays null, and without the flag a model legitimately holding null would compare equal. It only picks a status, never moves data.

**`ValuesMatch` must be enum-aware.** The SDK stores an enum-typed node as a boxed `int` (`TypeInfo.cs:1047-1053`), the model stores a boxed enum, and `Equals` between them is false, so every accepted enum write would answer Bad. This is reachable in shipped consumer code (`GpioPin.Mode`, `Motor`, `AnalogChannel`, all exposed through `HomeBlaze.OpcUa`). The repo already handles this exact box mismatch deliberately at `IWriteInterceptor.cs:303-333`; match it:

```csharp
if (Equals(modelValue, requested)) return true;
if (modelValue is Enum && requested is not null &&
    requested.GetType() == Enum.GetUnderlyingType(modelValue.GetType()))
    return Equals(modelValue, Enum.ToObject(modelValue.GetType(), requested));
return false;
```

**"Kept" means the model took the value, not that a read-back returns the client's bytes.** The comparison is in property space and the client asked in node space, so a non-round-tripping converter answers Good while the node then serves something different. That is deliberate: the model did accept the write.

### Uncertain, and how it clears

If `ConvertToNodeValue` throws on the model's own value, no node value is correct. The node keeps what it has and reports `UncertainLastUsableValue`, which is the field OPC UA provides for exactly this. Leaving it unwrapped instead would let the throw escape as `BadUnexpectedError` with the node still holding the client's value, which is the divergence this PR exists to remove.

It clears the next time a representable value arrives. The outbound loop must set `node.StatusCode = StatusCodes.Good` alongside `Value` and `Timestamp` (`OpcUaSubjectServer.cs:190-191`), because the `Value` setter resets the status only while `!m_valueTouched` (`BaseVariableState.cs:536-539`) and that flag is already true from node creation.

Clients act on it: the client PR teaches all four inbound paths that Uncertain is usable but degraded.

### Details that are load bearing

**The registration check is a per-write resolve**, not a field. A readonly field would never be null and row 1 would stay unfixed. `UpdateProperty` pays the same lookup today (`:421`), so this is not a regression.

**The timestamp comes from the model**, not from `sourceTimestamp` and not from what `base` assigned. `base` sets `m_timestamp = sourceTimestamp` at `:2048` and the `Value` setter does not touch it, so a refused write would otherwise serve `conv(A)` stamped now. The other two writers of these nodes already stamp from the model (`CustomNodeManager.cs:397-401`, `OpcUaSubjectServer.cs:191`), so this keeps one convention.

**Apply `this.Value`, not the `value` parameter.** For an index range write the parameter is only the client's fragment.

**`ClearChangeMasks` runs on every path**, so the corrected value is published whatever the answer. The SDK skips its own flush on a bad result (`:2022-2025`), and its good-path flush is additionally gated on the monitored-item manager type (`:2048`). It does not double-notify: `NodeState.cs:2633` guards on the mask, which we have cleared.

**`BadOutOfRange` is not in the client's `PermanentCodes`** (`OpcUaStatusCodeClassifier.cs:31-41`), so a permanent refusal is retried indefinitely. The client PR fixes that by making a failing batch stop condemning the rest of the flush, which is code-agnostic. No classification change is needed here.

### The outbound loop

Wrap the per-change conversion and assignment through `ClearChangeMasks` (`OpcUaSubjectServer.cs:186-193`), so one throwing converter costs one property rather than the whole merged batch, which is what happens today (`ChangeQueueProcessor.cs:330`, caught at `:336-339`). Increment `written` before `ClearChangeMasks`. Log the skipped property: the merger has already marked it published to source (`ChangeDeliveryFilter.cs:26, 38`), so there is no retry and the node stays stale until that property changes again. Do not swallow `OperationCanceledException`, and keep the flag reset in the outer `finally`.

A plain `try` block allocates nothing. It must not be a lambda or local function, which would capture per iteration.

### What gets deleted

- `IsWritingOwnNodeValues` (`:44`) and `SelfWrittenNodeValue` (`:55`), two `[ThreadStatic]` fields and roughly eighteen lines of comment
- their seven touch points (`:44, :55, :167, :192, :200, :201, :411`)
- the `try`/`finally` in the outbound loop that exists only to disarm the flag
- the `StateChanged` subscription (`CustomNodeManager.cs:408-416`), its only subscriber
- `SelfEchoReproTests.cs` entirely: both tests hand-construct a flush/guard race that cannot exist once no node write reaches the subject

Verified: one reader of the fields, one caller of `UpdateProperty`, one subscriber. Node values are written at creation, in the outbound loop through the property setter, and by the SDK write service (`NodeState.cs:3775`, reached from `CustomNodeManager.cs:2008`, `:2229`, and `NodeState.WriteChildAttribute:4513`, all virtual). Monitored items use the SDK's own `OnStateChanged` field. `NodeState.Clone()` throws by default (`:73-76`), so a subclass field cannot be silently lost.

### Comments and docs this PR must carry

- `docs/design/connector-delivery.md:62-64` argues `SourceValuesAreSettled` in terms of the deleted `StateChanged` path
- `docs/connectors-opcua-server.md:259` documents `IncomingChangesPerSecond` as "client writes to server"; it becomes writes to registered properties
- `OpcUaSubjectServer.cs:20-24` and `:174-176`, `CustomNodeManager.cs:140-143` and `:403-406`, `OpcUaServerSelfWriteTests.cs:10-15` and `:57-59` all describe the deleted handler or a mask that is cleared at `:406`

## Why not either event hook

`OnWriteValue` (`BaseVariableState.cs:1930`) returns at `:1961` before the type check and the index range merge, so a wrong-typed write would answer Good and a range write would apply the fragment as the whole value. `OnSimpleWriteValue` (`:2010`) refuses range writes at `:2016-2018` and its delegate carries no source timestamp (`:2021`, with `m_timestamp` assigned only at `:2048`). Overriding `WriteValueAttribute` keeps all three because it calls the SDK rather than working around it.

## Gaps this leaves

- A property whose converter throws sits at Uncertain until a representable value arrives, and nothing in `OpcUaServerDiagnostics` surfaces node quality ([#299](https://github.com/RicoSuter/Namotion.Interceptor/issues/299)).
- The client's own `DataValue.StatusCode` is discarded, since we always write Good or Uncertain.
- A wrong-typed or read-only write to an unregistered property now answers `BadNoCommunication` rather than `BadTypeMismatch` or `BadNotWritable`, because the registration check runs first. The window is small, since detach deletes the node.
- Model reads now run under `NodeManager.Lock`, so a slow derived getter holds it.
- A concurrent local write between the apply and the read-back yields a false Bad, which the client retries.
- Late-attached subjects still get no node (finding 1 in #442).

## Expected size

Deletions about 38. Additions: the subclass and override 55-70, the copy helper ~12, the factory line 1, the outbound wrap ~8, doc and comment updates ~8. **Net plus 45 to 60 production lines.** Tests 450-600, most on the live harness.

## Performance

**Inbound, per client write:** one model read (boxing a value type), one outward conversion, and one `Array.Clone` only when an index range is present. The registration resolve is two dictionary lookups, the same as today.

**Outbound, per change:** the thread-static store, the `StateChanged` dispatch and the `Equals` guard all leave the 20k/s path. Added: one `StatusCode` compare-and-store and a `try` block, neither of which allocates. Net improvement.

**Verification:** connector tester `opcua-load` two-process against master, CPU pinned, comparing `performance-{participant}.csv`; `cycles.csv` HeapMB for leaks; core benchmarks as a tripwire only.

## Test plan

Written red first.

| Test | Passes when |
|---|---|
| `WhenValidationRejectsAClientWrite_ThenTheNodeKeepsTheModelValueAndTheClientIsTold` | read-back returns A, subject holds A, client receives a bad status |
| `WhenAnOnChangingHookCancelsAClientWrite_ThenTheNodeKeepsTheModelValueAndTheClientIsTold` | same |
| `WhenTheInboundConverterThrows_ThenTheRestOfTheWriteRequestStillCompletes` | the other nodes in the same request are written; today the throw escapes |
| `WhenTheConverterPairDoesNotRoundTrip_ThenTheNodeHoldsTheConvertedModelValueAndTheClientReceivesGood` | asserts the status too, not only the value |
| `WhenAnEnumPropertyIsWritten_ThenTheClientReceivesGood` | the enum box mismatch does not produce a false Bad |
| `WhenThePropertyIsNotRegistered_ThenTheWriteIsRefusedBeforeTheNodeIsTouched` | node value unchanged |
| `WhenTheOutboundConversionThrows_ThenTheNodeReportsUncertain` | status is `UncertainLastUsableValue`, no exception escapes |
| `WhenTheModelLaterHoldsARepresentableValue_ThenTheStatusReturnsToGood` | requires the outbound reset |
| `WhenAClientWritesAnIndexRange_ThenTheSubjectsPreviousArrayIsNotMutated` | asserts the bug, not the mechanism. Red today |
| `WhenAClientWritesAnIndexRange_ThenAChangeIsPublishedThroughTheInterceptorChain` | needs a full-tracking context, or it passes vacuously |
| `WhenOneChangesConversionThrows_ThenTheRestOfTheBatchIsStillWritten` | red today |
| `WhenAWriteIsRefused_ThenTheNodeTimestampReflectsTheModel` | pins the timestamp rule |

Regression guards, green today: a wrong-typed write still answers `BadTypeMismatch`; an index range write still reaches the subject as the merged whole; a client omitting `SourceTimestamp` still succeeds; an accepted write leaves both stores holding it.

The index range tests need a raw `session.WriteAsync` with `WriteValue.IndexRange`; no such helper exists yet, though `Client.Source.CurrentSession` is public. Use a dedicated `OpcUaTestServer` with `OpcUaTestPortPool`, not `SharedServerTestBase`, since a second observer on the shared fixture leaks into the rest of the assembly.

Conventions: `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange`, `// Act`, `// Assert`, `AsyncTestHelpers.WaitUntilAsync` rather than delays. The OPC UA suite binds a fixed port and cannot run concurrently with itself or the connector tester.

## Risks

- **Reliance on base behaviour**, that `base.WriteValueAttribute` assigns before returning Good (`BaseVariableState.cs:2046-2048`).
- **The copy-before-merge audit** is not exhaustive across the whole SDK.
- **`BadNoCommunication` as a Write result** is assumed legal per Part 4 rather than verified from the spec text. If wrong, pick another transient code; nothing else depends on it.
