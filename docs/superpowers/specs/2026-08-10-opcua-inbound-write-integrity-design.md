# OPC UA server: inbound write integrity

**Goal:** the node a client reads never claims to hold a value the subject model does not.

**Shape:** second of two stacked PRs.

**Base:** stacked on the client status conformance PR. The order is a hard constraint, not a preference: this PR makes the server return bad statuses on refusal, and without the client fix a topology with this library at both ends stalls its entire outbound write stream behind the first permanent refusal.

## The invariant

At quiescence, for every mapped property, one of these holds:

- `node.Value == ConvertToNodeValue(subject value)` and `node.StatusCode` is Good, or
- `node.StatusCode` is Uncertain, and the node is therefore declaring its value untrustworthy.

There is no third case. The design is judged against this statement.

## The defect

The SDK commits a client's write into the node, answers `Good`, and only then raises `StateChanged`, which is where `OpcUaSubjectServer.UpdateProperty` tries to apply that value to the subject. Every way the apply can fail leaves the node holding a value the model rejected, with nothing that repairs it and the quality flag still reading Good.

| How the apply fails | Reachable when | Node holds | Subject holds |
|---|---|---|---|
| Property not registered, silent `return` | Detach or structural mutation in flight | B | A |
| `ConvertToPropertyValue` throws | Client writes a value the converter rejects | B | A |
| `SetValueFromSource` throws | A validation interceptor rejects the value | B | A |
| An `OnChanging` hook sets `cancel` | Generated hook vetoes the write | B | A |
| Converter pair does not round-trip | Any scaling, unit or enum mapping converter | B | g(B) |

Aggravating facts:

1. **The lie is self-confirming.** The node keeps the client's value, so a client that reads back to verify its write gets its own value returned and the check passes.
2. **Subscribers see the refused value.** Monitored items fire from the SDK's `OnStateChanged` field, which runs before the `StateChanged` event the apply hangs off (`NodeState.cs:2635-2636`, both from `CustomNodeManager.Write:2051`). A refused write is published to every subscribed client before the model is asked.
3. **One row is not just wrong data.** `ConvertToPropertyValue` sits outside the `try` (`OpcUaSubjectServer.cs:424`), and nothing between `CustomNodeManager.Write:2051` and the handler catches, so a throwing converter propagates into the SDK's service layer holding `NodeManager.Lock`.
4. **`StatusCode` is set Good once at creation** (`OpcUaNodeFactory.cs:243`) and never touched, so the server asserts good quality unconditionally, including in all five rows.

### A sixth defect, found during review

The node and the subject **share array instances**. `OpcUaValueConverter.ConvertToNodeValue` returns the property's array by reference for any non-decimal element type (`OpcUaValueConverter.cs:87`), and that instance is assigned to the node at creation (`CustomNodeManager.cs:395`) and on every outbound write (`OpcUaSubjectServer.cs:190`).

The SDK's index range merge mutates the destination array **in place** (`NumericRange.cs:753-756`, reached from `BaseVariableState.cs:2033-2041`). So a client writing `myArray[2:4]`:

- mutates the subject's array directly, bypassing the interceptor chain entirely
- publishes no change, so no other connector on that subject ever learns of it
- leaves `PropertyValueEqualityCheckHandler` comparing the same reference to itself with `EqualityComparer<T>.Default`, so any later write of that instance short-circuits before the terminal

Pre-existing, invisible, and it defeats two mechanisms this design depends on. Fixed here because the design cannot be correct without it.

### Relationship to #420

Four of the original rows predate #420. The round-trip row does not: master's outbound loop used to push every mapped change into the node, including the subject's own apply of a client write, and that redundant write kept overwriting the node with the model's value. #420 added the supersession check at `OpcUaSubjectServer.cs:177`, correctly skipping it, and the accidental repair went with it.

Recorded as findings 2, 3 and 9 in epic [#442](https://github.com/RicoSuter/Namotion.Interceptor/issues/442), which the PR references. The finished findings are struck from the epic after merge.

## Design

Nodes for subject properties are constructed at one site (`OpcUaNodeFactory.cs:227`), so they are ours to subclass. Introduce `SubjectVariableState : BaseDataVariableState` and override `WriteValueAttribute` (`protected virtual` on `NodeState.cs:4119`, overridden non-sealed on `BaseVariableState.cs:1906`).

### The outcome matrix

Two independent facts decide everything. Did the model accept the write, and can we represent what the model now holds?

| Model accepted | Representable | Node value | Node status | Returned |
|---|---|---|---|---|
| yes | yes | conversion of the model's value | Good | Good |
| yes | no | the client's value | Uncertain | Good |
| no | yes | conversion of the model's value | Good | Bad |
| no | no | the snapshot, restored | Uncertain | Bad |
| not registered | n/a | the snapshot, restored | unchanged | `BadNodeIdUnknown` |

The rule reads in one line: **return Good exactly when the model accepted the write, and leave the node holding the best available representation of what the model holds.**

Returning Bad on refusal is what a conformant server does, and it is safe because of the stacked client PR.

### The steps

1. Snapshot `Value`, `StatusCode` and `Timestamp`.
2. Call `base.WriteValueAttribute(...)`. Full SDK path: type check, `ExtractValueFromVariant`, copy policy, index range merge, assignment. A bad result returns straight through.
3. Ask the server to apply a **copy of `this.Value`**, using `this.Timestamp`. The copy is what keeps the node's array out of the model. `this.Value` rather than the `value` parameter, because for an index range write the parameter is only the client's fragment while `this.Value` is the merged whole.
4. The apply reports **explicitly** whether the model took the value. It does not infer.
5. Read the model back, convert outward, assign a copy to `Value`, set `StatusCode`, and return per the matrix.
6. On any bad return, call `ClearChangeMasks` before returning.

**Why the timestamp comes from `this.Timestamp`, not the parameter.** A client that omits `SourceTimestamp` sends `DateTime.MinValue` with `Kind = Unspecified`, and `NodeState.WriteAttribute` forwards it unnormalized (`NodeState.cs:3775-3781`). Converting that to `DateTimeOffset` throws on any host with a positive UTC offset, so on a CET server every write from a client that does not set a timestamp would be refused. Base normalizes its own copy at `BaseVariableState.cs:1964-1968, 2048`, which is what `this.Timestamp` reads. Today's code is accidentally immune because `CustomNodeManager.cs:414` already passes the normalized `variableNode.Timestamp`.

**Why the apply reports rather than the caller infers.** An earlier draft compared the property's commit revision before and after. That is wrong three ways: a write stopped by the equality check consumes nothing either (`InterceptorExecutor.cs:24-27` says so outright), a concurrent model-side write to the same property advances it, and `SetWriteState` uses `Interlocked.Exchange` rather than a max, so a race can move it backwards. Inferring state from a shared counter is the kind of distributed invariant this PR exists to delete.

**Why step 6 exists.** `CustomNodeManager.Write` skips `ClearChangeMasks` when the result is bad (`:2022-2025`), while `base` has already set the change mask and our assignments set it again. Without flushing it ourselves, the corrected state is not published and a dirty mask leaks until some unrelated actor flushes it.

### Array ownership

The rule is that **the node and the subject never share an array instance**. Enforced by copying at the boundary in both directions, at every node value assignment and before every apply, rather than inside `ConvertToNodeValue`, because that method is `public virtual` and a custom converter would not honour the guarantee.

This is what makes the snapshot meaningful, makes the equality check see a real difference, and makes index range writes publish a change like any other write.

The cost is an array allocation per array-valued write in each direction, and the outbound direction is the 20k/s one. It is measured, not assumed. If it proves material, the outbound copy can be skipped for read-only nodes, since only writable nodes can receive an index range merge.

### The outbound loop

- Set `node.StatusCode = StatusCodes.Good` alongside `Value` and `Timestamp` (`OpcUaSubjectServer.cs:190-191`). This is what recovers a property from Uncertain. It is genuinely required: the `Value` setter resets the status only while `!m_valueTouched` (`BaseVariableState.cs:536-539`), and that flag is already true from node creation.
- Wrap the per-change conversion and assignment so a throw costs one property rather than the batch, and mark that node Uncertain. Today the throw reaches `ChangeQueueProcessor.cs:330`, is caught at :336-339, and **the whole merged batch is discarded**, taking unrelated properties with it.

Pre-existing, folded in deliberately, and called out in the PR description rather than smuggled.

### Why not either event hook

`OnWriteValue` (`BaseVariableState.cs:1930`) returns early at :1961, skipping the type check (:1971-2000), `ExtractValueFromVariant` (:2002), the copy policy (:2005) and index range merging (:2031-2043). A string written to an int node would go from `BadTypeMismatch` to Good, and an index range write would apply the client's fragment as the whole value.

`OnSimpleWriteValue` (:2010) runs after all of it, but refuses index range writes with `BadIndexRangeInvalid` (:2016-2018) and its signature (`NodeState.cs:5011`) carries no `sourceTimestamp`, which cannot be recovered because `m_timestamp` is not assigned until :2048. Our own client sets `SourceTimestamp` (`OutboundWriter.cs:166`), so that would silently drop timestamp fidelity across an OPC UA hop.

Overriding `WriteValueAttribute` keeps all three because it calls the SDK rather than working around it.

### The cost of that choice

`base.WriteValueAttribute` assigns before returning, so the node is committed before we can refuse, and a bad status does not undo it. That is why the snapshot and restore exist. It is compensating logic, four lines in one method, under a lock already held, with no concurrency and no cross-method protocol. That is a different thing from the distributed invariant being removed, but it is a real cost and it is worse on this axis than a pre-commit hook would be.

### What gets deleted

- `IsWritingOwnNodeValues` (:44) and `SelfWrittenNodeValue` (:55), two `[ThreadStatic]` fields with roughly eighteen lines of comment
- their seven touch points (:44, :55, :167, :192, :200, :201, :411)
- the `try`/`finally` in the outbound loop, which exists only to disarm the flag
- the `StateChanged` subscription (`CustomNodeManager.cs:408-416`), its only subscriber

Verified: the fields have one reader (:411), `UpdateProperty` one caller (`CustomNodeManager.cs:414`), `StateChanged` one subscriber (:408). Node values are written at creation (`CustomNodeManager.cs:395`), in the outbound loop (through the property setter, never reaching `WriteValueAttribute`), and by the SDK write service (`NodeState.cs:3775`, whose only callers are `CustomNodeManager.cs:2008` and `:2229`). Monitored items use the SDK's own `OnStateChanged` field, so removing the `StateChanged` subscription cannot affect subscriptions.

### Comments that become wrong

- `OpcUaSubjectServer.cs:20-24` says a client write reaches the node before `UpdateProperty` applies it. That inverts; the `SourceValuesAreSettled` conclusion survives but its mechanism changes.
- `CustomNodeManager.cs:403-406` loses its "before the handler is attached" rationale.
- The `RemoveSubjectNodes` lock comment narrows to monitored-item consistency.

## Explicitly out of scope

- Late-attached subjects never getting a node (finding 1)
- MQTT and WebSocket parity for refused writes
- The client-side equivalent swallow, which needs the ownership contract [#342](https://github.com/RicoSuter/Namotion.Interceptor/issues/342) settles
- Exposing node quality in diagnostics ([#299](https://github.com/RicoSuter/Namotion.Interceptor/issues/299))
- The memory and retention cluster ([#281](https://github.com/RicoSuter/Namotion.Interceptor/issues/281), [#441](https://github.com/RicoSuter/Namotion.Interceptor/issues/441))

## Acceptance criteria

1. **The invariant holds** for all six defects, and after recovery.
2. **No new problems.** Type checking, index range writes and client source timestamps all keep working. A write from a client that omits `SourceTimestamp` succeeds.
3. **Three existing tests change, and that is expected.** `Integration/SelfEchoReproTests.cs:61-62` and `:127-128` reference the deleted thread-static fields and will not compile. `SelfEchoReproTests` and `Integration/OpcUaCrossStoreConvergenceTests.cs:56-64` simulate a client write by assigning `node.Value` directly, which no longer routes through the apply. They are rewritten to drive a real client write against the live server, and the cross-store one is load bearing because it is the only test pinning why the server selects `SourceValuesAreSettled`.
4. **`Server/OpcUaServerDeliveryRuleTests.cs:44-63` still passes.** It greps `OpcUaSubjectServer.cs` and asserts exactly one `ChangeDeliveryRule.` line and that every `IsSuperseded(` call ends `, DeliveryRule)`. Any restructuring that moves ranking out of that file fails it.
5. **No performance regression.** See below.
6. **Simplification of coupling, not of volume.** Line count grows.

## Performance

**Inbound, per client write:** one model read, one conversion outward, and two array copies for array-valued properties. Against a network round trip, deserialization, session lookup and taking `NodeManager.Lock`, the first two are well under a percent.

**Outbound, per change:** the thread-static store, the `StateChanged` dispatch and the `Equals` guard all go. Added: a `StatusCode` assignment, a `try`, and an array copy for array-valued properties. The copy is the only term that could matter, because this is the 20k/s direction.

Earlier drafts of this spec claimed the outbound conversion was already allocating for arrays. It is not: `ConvertToNodeValue` returns the same instance and allocates only for `decimal[]`. The copy is therefore genuinely new allocation, and it is the number to measure.

**Verification:** connector tester `opcua-load` in two-process mode, comparing `performance-{participant}.csv` before and after, with the CPU pinned. Leak detection reads `cycles.csv` HeapMB, which is post-GC. Core benchmarks are a tripwire only, since nothing benchmarks this code; movement there means something was edited that should not have been.

**Fix the accounting first.** `IncomingThroughput.Add(1)` currently runs before the registration check (`OpcUaSubjectServer.cs:418`). Decide what the reshaped method counts before measuring.

## Test plan

Written red first.

**The six defects:**

| Test | Passes when |
|---|---|
| `WhenValidationRejectsAClientWrite_ThenTheNodeKeepsTheModelValue` | read-back returns A, subject holds A, client gets Bad |
| `WhenAnOnChangingHookCancelsAClientWrite_ThenTheNodeKeepsTheModelValue` | read-back returns A, subject holds A, client gets Bad |
| `WhenTheInboundConverterThrows_ThenNoExceptionReachesTheSdkAndTheNodeKeepsTheModelValue` | server still serves reads, read-back returns A |
| `WhenTheConverterPairDoesNotRoundTrip_ThenTheNodeHoldsTheConvertedModelValue` | read-back equals the conversion of the subject's value, stable across two reads |
| `WhenThePropertyIsNotRegistered_ThenTheWriteIsRefusedAndTheNodeIsUntouched` | client gets `BadNodeIdUnknown`, node unchanged |
| `WhenAClientWritesAnIndexRange_ThenAChangeIsPublishedToOtherConnectors` | a second connector on the same subject observes the merged array. Fails today for the aliasing reason, not the status reason |

**The matrix's two hard branches:**

| Test | Passes when |
|---|---|
| `WhenTheModelAcceptsButTheValueCannotBeRepresented_ThenTheNodeReportsUncertain` | client gets Good, node reports `UncertainLastUsableValue` |
| `WhenTheModelRefusesAndTheValueCannotBeRepresented_ThenTheNodeIsRestored` | client gets Bad, node holds A, no exception escapes |
| `WhenTheModelLaterHoldsARepresentableValue_ThenTheStatusReturnsToGood` | node holds the conversion, status Good, subscribers notified |
| `WhenAWriteIsRefused_ThenTheChangeMaskIsFlushed` | subscribers observe the corrected value despite the bad return |

**Regression guards, passing today and must keep passing:**

| Test | Passes when |
|---|---|
| `WhenAClientWritesTheWrongType_ThenTheSdkStillReturnsBadTypeMismatch` | `BadTypeMismatch`, neither store moves |
| `WhenAClientWritesAnIndexRange_ThenTheMergedArrayReachesTheSubject` | subject holds the merged whole, not the fragment |
| `WhenAClientOmitsTheSourceTimestamp_ThenTheWriteSucceeds` | no exception, value applied. Pins the timestamp fix; fails if the parameter is used |
| `WhenAClientSuppliesASourceTimestamp_ThenTheModelRecordsIt` | the subject's change timestamp matches |
| `WhenAClientWriteIsAccepted_ThenBothStoresHoldIt` | subject and read-back both hold B, status Good |
| `WhenTheServerWritesItsOwnValue_ThenNoInboundApplyIsTriggered` | no inbound apply, no echo |
| `WhenOneChangesConversionThrows_ThenTheRestOfTheBatchIsStillWritten` | every other property reaches its node |
| `WhenTheNodeHoldsAnArray_ThenItIsNotTheSubjectsInstance` | the two are not reference equal. Pins the ownership rule directly |

Conventions: `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange`, `// Act`, `// Assert`, `AsyncTestHelpers.WaitUntilAsync` rather than delays. Most need the live harness (`SharedServerTestBase`).

The OPC UA suite binds a fixed port and cannot run concurrently with itself or with the connector tester.

## Risks and open items

- **`ClearChangeMasks` from inside `WriteValueAttribute`.** We hold `NodeManager.Lock` and the outbound loop already calls it under the same lock, and `Monitor` is reentrant, but this is a new call site inside an SDK service call and must be confirmed rather than assumed.
- **Reading the model inside the SDK write call.** The getter runs under `NodeManager.Lock`. Cheap for a stored property, not necessarily for a derived one.
- **Reliance on base behaviour.** The design assumes `base.WriteValueAttribute` assigns before returning. Verified at `BaseVariableState.cs:2046-2048`, but it is behaviour, not contract.
- **A stuck Uncertain** persists until the property changes again. Correct, but invisible: `OpcUaServerDiagnostics` exposes nothing about node quality.
- **Lock ordering is unchanged and remains unsafe.** A client write holds `NodeManager.Lock` while running model code; if that code detaches a subject, `RemoveSubjectNodes` takes `_structureLock` while another thread can hold `_structureLock` and wait on `Lock` (`CustomNodeManager.cs:137, 144`). Reachable today through the `StateChanged` path. This design neither creates nor closes it, and it should be filed.
- **A client can park a non-Good status** on a node today, because `NodeState.WriteAttribute` only rejects non-Good status codes for non-value attributes (`:3791-3798`). The outbound loop's unconditional reset to Good now overwrites it. Marginal, but the two writers of that field now contend.
