# OPC UA server: inbound write integrity

**Goal:** the node a client reads never holds a value the subject model does not, and a client whose write was refused is told so whenever the server can know it.

**Base:** stacked on the array ownership PR, which is its branch parent and whose array rule this design depends on. Also requires the client status conformance PR to have merged first, for a behaviour reason given below.

## The invariant

At quiescence, for every mapped property, `node.Value == ConvertToNodeValue(subject value)` and the node's status is Good, **or** the node's status is Uncertain and it is declaring its value untrustworthy.

Sync is the primary goal and it holds on every path. The status a write returns is a second, separate question: whether the client learns its value was not kept.

## The defect

The SDK commits a client's write into the node, then raises `StateChanged`, where `UpdateProperty` tries to apply it to the subject. Every way the apply can fail leaves the node serving a value the model rejected.

| How the apply fails | Reachable when | Node holds | Subject holds |
|---|---|---|---|
| Property not registered, silent `return` | Detach or structural mutation in flight | B | A |
| `ConvertToPropertyValue` throws | Client writes a value the converter rejects | B | A |
| `SetValueFromSource` throws | A validation interceptor rejects the value | B | A |
| An `OnChanging` hook sets `cancel` | Generated hook vetoes the write | B | A |
| Converter pair does not round-trip | Any scaling, unit or enum mapping converter | B | g(B) |

Aggravating: the node keeps the client's value, so a read-back confirms a write that never landed; and monitored items fire from the SDK's `OnStateChanged` field before the `StateChanged` event the apply hangs off (`NodeState.cs:2635-2636`), so subscribers see the refused value before the model is asked.

An `OnChanging` hook that **transforms** rather than cancels is already handled and is not in the table: `SetValueFromOrigin`'s survival check demotes the origin to Local when the stored value differs from what the source sent (`SubjectChangeContextExtensions.cs:28-41`), which un-suppresses the echo.

**None of these are caused by #420.** An earlier revision claimed the round-trip row was, on the grounds that #420 removed a repairing re-write. That is false: `ChangeQueueProcessor.cs:213` drops any change whose origin is the processor's own source and the server passes `source: this` (`OpcUaSubjectServer.cs:133`), so the server's own apply has never reached the outbound loop, and that filter predates #420. The same wrong claim is in epic #442's finding 3 and is corrected there.

## Design

Nodes are constructed at one site (`OpcUaNodeFactory.cs:227`), so they are ours to subclass. Add `SubjectVariableState : BaseDataVariableState` and override `WriteValueAttribute` (`protected virtual` on `NodeState.cs:4119`, overridden non-sealed on `BaseVariableState.cs:1906`).

```
snapshot = (Value, StatusCode, Timestamp)

result = base.WriteValueAttribute(...)        // type check, index range merge, assignment
if (result is bad) return result              // node untouched by us

try {
    apply a copy of this.Value to the model, using this.Timestamp
}
catch, or the property is not registered {
    restore snapshot
    ClearChangeMasks(context, includeChildren: false)
    return the mapped bad status
}

try {
    read the model back, convert, assign a copy to Value
    StatusCode = Good
}
catch {
    StatusCode = UncertainLastUsableValue     // the apply completed; we cannot represent the result
    log
}

return Good
```

### Two `try` blocks are the whole discrimination mechanism

This is what lets the design know whether the model took the value, with no signal from the apply and no counter:

- **first block throws** — the model never took it. Restoring is right, and a bad status is honest.
- **second block throws** — the apply completed, so the client's value is the better guess and restoring would rewind past a change the model made.
- **neither throws** — the node gets the model's value.

Two earlier revisions tried to get this another way and both were wrong. A commit-revision delta conflates refusal with the equality-check short-circuit (`InterceptorExecutor.cs:24-27` says a write stopped by the equality check consumes nothing) and can move backwards under a race. An explicit outcome report from the apply cannot be built at all: `SetValueFromOrigin` ends at `property.Metadata.SetValue?.Invoke(...)` (`SubjectChangeContextExtensions.cs:52`), an `Action` returning void, and on a cancel no interceptor runs so there is nothing to report from.

### What the status answers, and what it cannot

| Refusal | Detectable | Answer |
|---|---|---|
| Property not registered | yes | `BadOutOfService`, a **transient** code: the condition is a detach or structural mutation in flight and clears on reattach |
| `ConvertToPropertyValue` throws | yes | `BadOutOfRange`, the server declined the value |
| `SetValueFromSource` throws (validation) | yes | `BadOutOfRange` |
| `OnChanging` sets `cancel` | **no** | `Good`. Silent by construction, no exception, no signal |
| Converter does not round-trip | not a refusal | `Good`, node holds the converted model value |

The cancel row is the one honest gap. Sync still holds, the node is corrected, and the client sees the settled value on its next read or notification. Closing it would need a signal from the core write path that does not exist today.

**Why this does not need the client to classify our exact codes.** The client PR bounds retries rather than relying on classification, so any persistently failing write is eventually dropped with a diagnostic whether it was classified permanent or transient. That is the more robust fix, because the classifier's own definition of permanent is "cannot change without a new session", and a model validation rule is in-process and mutable, so our refusals would otherwise be classified transient and retried forever. The cross-PR contract is therefore one sentence: **the client must bound retries.** Without it, any bad status from this server stalls that client's entire outbound stream (`WriteRetryQueue.cs:154-172`, `SubjectSourceBase.cs:292-296`), which is why the merge order is a hard requirement.

### Details that are load bearing

**`ClearChangeMasks` on the bad path.** `CustomNodeManager.Write` skips it when the result is bad (`:2022-2025`), while `base` has set the change mask and the restore sets it again. Without flushing ourselves the correction is not published and a dirty mask leaks. Pass `includeChildren: false`; the SDK's own call at `:2051` passes `true` and would flush attribute children as a side effect.

**Timestamp from `this.Timestamp`, not the parameter.** A client omitting `SourceTimestamp` sends `DateTime.MinValue` with `Kind = Unspecified`, forwarded unnormalized (`NodeState.cs:3775-3781`). Converting that to `DateTimeOffset` throws on any positive-UTC-offset host, so on a CET server every such write would fail. Base normalizes its own copy (`BaseVariableState.cs:1964-1968, 2048`), which `this.Timestamp` reads.

**Apply `this.Value`, not the `value` parameter.** For an index range write the parameter is only the client's fragment; `this.Value` is the merged whole.

**Subclass construction.** `MemberwiseClone` calls `Activator.CreateInstance(GetType(), Parent)` (`BaseVariableState.cs:512-516`), so the subclass needs a `(NodeState parent)` constructor and must not hold server state in fields a clone would lose. The `PropertyReference` is resolved from `Handle`, which already carries it (`CustomNodeManager.cs:372`).

**The array copies** come from the parent PR's ownership rule. Without it the restore silently does nothing, because the snapshot captures a reference to an array the SDK has already mutated in place.

### What gets deleted

- `IsWritingOwnNodeValues` (`:44`) and `SelfWrittenNodeValue` (`:55`), two `[ThreadStatic]` fields with roughly eighteen lines of comment
- their seven touch points (`:44, :55, :167, :192, :200, :201, :411`)
- the `try`/`finally` in the outbound loop, which exists only to disarm the flag
- the `StateChanged` subscription (`CustomNodeManager.cs:408-416`), its only subscriber

Verified: one reader of the fields (`:411`), one caller of `UpdateProperty` (`CustomNodeManager.cs:414`), one subscriber (`:408`). Node values are written at creation, in the outbound loop through the property setter (never reaching `WriteValueAttribute`), and by the SDK write service (`NodeState.cs:3775`, whose only callers are `CustomNodeManager.cs:2008` and `:2229`). Monitored items use the SDK's own `OnStateChanged` field, so removing the subscription cannot affect subscriptions. It also removes a hazard: `RemoveSubjectNodes`' `DeleteNode` (`:156, 168`) currently reaches `UpdateProperty` through that subscription.

### Why not either event hook

`OnWriteValue` (`BaseVariableState.cs:1930`) returns early at `:1961`, skipping the type check (`:1971-2000`), `ExtractValueFromVariant` (`:2002`), the copy policy and index range merging (`:2031-2043`). A string written to an int node would go from `BadTypeMismatch` to Good, and an index range write would apply the fragment as the whole value.

`OnSimpleWriteValue` (`:2010`) runs after all of it but refuses index range writes with `BadIndexRangeInvalid` (`:2016-2018`), and its signature (`NodeState.cs:5011`) carries no `sourceTimestamp`, unrecoverable because `m_timestamp` is not assigned until `:2048`. Our own client sets it (`OutboundWriter.cs:166`), so that would drop timestamp fidelity across a hop.

Overriding `WriteValueAttribute` keeps all three because it calls the SDK rather than working around it.

### The cost of that choice

`base` assigns before returning, so the node is committed before we can refuse, and a bad status does not undo it. That is why the snapshot and restore exist. It is compensating logic, and it is the axis on which this is weaker than a pre-commit hook would have been. Four lines, one method, under a lock already held, no concurrency and no cross-method protocol.

## Gaps this leaves

Stated plainly rather than buried in risks:

- **A silent cancel answers Good.** Sync holds, honesty does not.
- **A cancel combined with a throwing read-back** leaves the client's value against an unchanged model, flagged Uncertain. Doubly exotic, logged, and the only case where the invariant's first clause fails.
- **A stuck Uncertain is invisible.** `OpcUaServerDiagnostics` exposes nothing about node quality, and [#299](https://github.com/RicoSuter/Namotion.Interceptor/issues/299) is out of scope.
- **Late-attached subjects still get no node** (finding 1 in #442), so their changes are dropped by the write loop's mapping guard.
- **The `NodeManager.Lock` / `_structureLock` ordering hazard remains** (`CustomNodeManager.cs:137, 144`), neither created nor closed here.

## Who this helps

Consumers using validation, an `OnChanging` hook, a custom converter, or running structural mutations against a live server. A consumer with none of those gets no correctness benefit and pays a marginally longer inbound path. That is the honest trade, and it is why the array and batch fixes were split out: those help everyone.

## Expected size

Roughly plus 75 lines of production code: the subclass and override (~60), the construction swap (~5), the reshaped apply (~10), minus the 36 deleted. Test code grows more.

## Acceptance criteria

1. **The invariant holds** on every path.
2. **No new problems.** Type checking, index range writes and client source timestamps keep working; a client that omits `SourceTimestamp` still succeeds.
3. **Two existing tests rewritten.** `SelfEchoReproTests.cs` references the deleted fields (`:62, :63, :71, :72, :128, :146, :153`) and will not compile. `OpcUaCrossStoreConvergenceTests.cs:56-64` references none of them but simulates a client write by assigning `node.Value` directly, which no longer routes through the apply; it is load bearing as the only test pinning why the server selects `SourceValuesAreSettled`.
4. **`Server/OpcUaServerDeliveryRuleTests.cs:44-63` still passes.** It greps `OpcUaSubjectServer.cs` for exactly one `ChangeDeliveryRule.` line.
5. **No performance regression.**

## Test plan

Written red first.

**The five defects:**

| Test | Passes when |
|---|---|
| `WhenValidationRejectsAClientWrite_ThenTheNodeKeepsTheModelValue` | read-back returns A, subject holds A, client receives a bad status |
| `WhenAnOnChangingHookCancelsAClientWrite_ThenTheNodeKeepsTheModelValue` | read-back returns A, subject holds A, client receives Good |
| `WhenTheInboundConverterThrows_ThenNoExceptionReachesTheSdkAndTheNodeKeepsTheModelValue` | server still serves reads, read-back returns A |
| `WhenTheConverterPairDoesNotRoundTrip_ThenTheNodeHoldsTheConvertedModelValue` | read-back equals the conversion of the subject's value, stable across two reads |
| `WhenThePropertyIsNotRegistered_ThenTheNodeIsRestoredAndTheStatusIsTransient` | node holds A, and the returned code is one the client will retry |

**The two branches and their consequences:**

| Test | Passes when |
|---|---|
| `WhenTheReadBackThrows_ThenTheNodeReportsUncertain` | status is `UncertainLastUsableValue`, client receives Good |
| `WhenTheModelLaterHoldsARepresentableValue_ThenTheStatusReturnsToGood` | node holds the conversion, status Good, subscribers notified |
| `WhenAWriteIsRefused_ThenTheChangeMaskIsFlushed` | subscribers observe the corrected value despite the bad return |
| `WhenACancelledWriteIsRefused_ThenTheClientReceivesGood` | pins the known gap, so a future change to it is deliberate |

**Regression guards, green today and must stay green:**

| Test | Passes when |
|---|---|
| `WhenAClientWritesTheWrongType_ThenTheSdkStillReturnsBadTypeMismatch` | neither store moves |
| `WhenAClientWritesAnIndexRange_ThenTheMergedArrayReachesTheSubject` | subject holds the merged whole |
| `WhenAClientOmitsTheSourceTimestamp_ThenTheWriteSucceeds` | pins the timestamp fix |
| `WhenAClientSuppliesASourceTimestamp_ThenTheModelRecordsIt` | the subject's change timestamp matches |
| `WhenAClientWriteIsAccepted_ThenBothStoresHoldIt` | both hold B, status Good |
| `WhenTheServerWritesItsOwnValue_ThenNoInboundApplyIsTriggered` | no inbound apply, no echo |

Conventions: `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange`, `// Act`, `// Assert`, `AsyncTestHelpers.WaitUntilAsync` rather than delays. Most need the live harness (`SharedServerTestBase`). The OPC UA suite binds a fixed port and cannot run concurrently with itself or the connector tester.

## Performance

**Inbound, per client write:** one model read and one conversion outward. Against a network round trip, noise.

**Outbound, per change:** the thread-static store, the `StateChanged` dispatch and the `Equals` guard all go. Nothing is added.

**Verification:** connector tester `opcua-load` two-process against master, CPU pinned, comparing `performance-{participant}.csv`; `cycles.csv` HeapMB for leaks; core benchmarks as a tripwire only. The throughput accounting fix in the parent PR must be in place first or the comparison measures the accounting change.

## Risks

- **Reliance on base behaviour.** Assumes `base.WriteValueAttribute` assigns before returning Good, verified at `BaseVariableState.cs:2046-2048`. Behaviour, not contract.
- **Reading the model under `NodeManager.Lock`.** Cheap for a stored property, not necessarily for a derived one.
- **`ClearChangeMasks` from inside a service call.** The SDK holds `lock (Lock)` across the batch (`CustomNodeManager.cs:1876`), `Monitor` is reentrant, and the repo already calls it under that lock (`OpcUaSubjectServer.cs:193`). Confirmed rather than assumed, but it is a new call site.
- **An exception escaping the override** is contained: `NodeState.WriteAttribute` converts a throw to `BadUnexpectedError` (`:3773-3788`). The override still catches everything so outcomes are uniform.
