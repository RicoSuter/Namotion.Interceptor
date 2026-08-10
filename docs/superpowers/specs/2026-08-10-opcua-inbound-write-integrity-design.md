# OPC UA server: inbound write integrity

**Goal:** the node a client reads never claims to hold a value the subject model does not.

**Shape:** small, targeted PR. One connector, one code path, tests written red first.

**Base:** master at `f561d196`, which is #420 squash-merged.

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

Three aggravating facts:

1. **The lie is self-confirming.** The node keeps the client's value, so a client that reads back to verify its write gets its own value returned and the check passes.
2. **Subscribers see the refused value too.** Monitored items are notified from the SDK's separate `OnStateChanged` field, which fires before the `StateChanged` event the apply hangs off (`NodeState.cs:2635-2636`, both reached from `CustomNodeManager.Write:2051`). A refused write is published to every subscribed client before the model has even been asked.
3. **One row is not just wrong data.** `ConvertToPropertyValue` sits outside the `try` (`OpcUaSubjectServer.cs:424`). Nothing between `CustomNodeManager.Write:2051` and the `StateChanged` handler catches, so a throwing converter propagates into the SDK's service layer while holding `NodeManager.Lock`.

Properties without a setter are already read-only nodes (`CustomNodeManager.cs:375-378`), so the SDK rejects those writes first. Only writable properties are affected.

`StatusCode` is set to Good once at node creation (`OpcUaNodeFactory.cs:243`) and never touched again, so the server asserts good quality unconditionally, including in all five rows.

### Relationship to #420

Four rows predate #420. The round-trip row does not: master's outbound loop used to push every mapped change into the node, including the subject's own apply of a client write, and that redundant write kept overwriting the node with the model's value. #420 added the supersession check at `OpcUaSubjectServer.cs:177`, correctly skipping it, and the accidental repair went with it.

Recorded as findings 2, 3 and 9 in epic [#442](https://github.com/RicoSuter/Namotion.Interceptor/issues/442). One issue is filed for the cluster before implementation starts.

## Design

Nodes for subject properties are constructed at exactly one site (`OpcUaNodeFactory.cs:227`), so they are ours to subclass. Introduce `SubjectVariableState : BaseDataVariableState`, holding the `PropertyReference` and a reference to the server, and override `WriteValueAttribute` (`protected virtual` on `NodeState.cs:4119`, overridden non-sealed on `BaseVariableState.cs:1906`).

**The override, in order:**

1. Snapshot `Value`, `StatusCode` and `Timestamp`.
2. Read the property's commit revision: `TryGetWriteState(includeSourceCommitsInRevision: true, out var revisionBefore, out _)`. Must happen before the apply, because it cannot be recovered afterwards.
3. Call `base.WriteValueAttribute(...)`. Full SDK path: data type check, `ExtractValueFromVariant`, copy policy, index range merge, assignment. A bad result returns straight through, node unmodified.
4. Ask the server to apply **`this.Value`**, not the `value` parameter, passing `sourceTimestamp` through. For an index range write the parameter is only the client's fragment while `this.Value` is the merged whole. This is why index range writes keep working.
5. Read the subject back, convert outward, assign to `Value`, set `StatusCode` to Good, return Good.
6. If step 4 or 5 throws, read the revision again and branch on whether the model moved:
   - **Unchanged**, the model refused the write: restore the snapshot and return a bad status. The node is exactly where it was.
   - **Advanced**, the model took the write but we cannot represent what it now holds: leave the client's value in place, set `StatusCode` to `UncertainLastUsableValue`, and return Good. The write did land, so Good is honest; the quality flag carries the doubt.

Steps 4 to 6 complete before `CustomNodeManager.Write:2051` calls `ClearChangeMasks`, so no subscriber observes an intermediate value and the client's `Write` has not returned.

**Why the revision is the discriminator.** `SetValueFromSource` returns `void` and an `OnChanging` cancel is silent, so "did not throw" does not mean "committed". Comparing values instead would be worse: a wrong comparison would refuse a write the model accepted. A commit advances the revision and a cancel does not, with no values compared. Reading it costs two `Interlocked.Read` on the happy path, against a client write that already costs a network round trip.

Without this branch the recovery is knowably wrong in one direction or the other: restoring while the model has moved diverges the node, and keeping the client's value while the model refused diverges it the other way.

**Split of responsibility.** The node subclass owns SDK mechanics: snapshot, revision reads, base call, restore. The server owns model mechanics: converter, configuration, throughput counters. `UpdateProperty` is reshaped into a method that applies and returns the value to store, or signals failure, replacing today's void method that swallows both failure modes.

### The outbound loop

Two changes, both in `WriteChangesAsync`:

- Set `node.StatusCode = StatusCodes.Good` alongside the existing `Value` and `Timestamp` assignments (`OpcUaSubjectServer.cs:190-191`). This is what recovers a property from Uncertain once the model holds a representable value again.
- Wrap the per-change `ConvertToNodeValue` and assignment (:187-190) so a throw costs that one property instead of the batch, and set that node's `StatusCode` to `UncertainLastUsableValue` rather than leaving it claiming Good over a stale value.

The wrap is pre-existing rather than introduced here, and is folded in deliberately: without it, the recovery path has a hole. Today the throw propagates out of `WriteChangesAsync` into `ChangeQueueProcessor.cs:330`, where it is caught and logged (:336-339), so delivery continues but **the entire merged batch is discarded**, taking every unrelated property's change with it. It will be called out in the PR description rather than smuggled in.

### Why not either event hook

Both were evaluated and rejected. They look like the obvious answer, so the reasons are worth keeping.

`OnWriteValue` (`BaseVariableState.cs:1930`) returns early at :1961 and skips the data type check (:1971-2000), `ExtractValueFromVariant` (:2002), the copy policy (:2005) and index range merging (:2031-2043). Two consequences are disqualifying: a client writing a string to an int node would go from `BadTypeMismatch` to `Good`, since nodes carry a real `DataType` and `ValueRank` (`OpcUaNodeFactory.cs:239-240`) and the stock converter passes unknown shapes through (`OpcUaValueConverter.cs:50`); and an index range write would apply the client's fragment as the whole property value, which is silent data corruption worse than any row above.

`OnSimpleWriteValue` (:2010) runs after all of it and is safe, but costs two behaviours. It refuses index range writes with `BadIndexRangeInvalid` (:2016-2018), and its signature (`NodeState.cs:5011`) carries no `sourceTimestamp`, which cannot be recovered from the node either because `m_timestamp` is not assigned until :2048. Our own client sets `SourceTimestamp` from `change.ChangedTimestamp` (`OutboundWriter.cs:166`), so timestamp fidelity across an OPC UA hop is something the library provides today and the hook would silently drop on its own primary path.

Overriding `WriteValueAttribute` keeps the type check, the index range merge and the timestamp, because it calls the SDK rather than working around it.

### The cost of that choice

`base.WriteValueAttribute` assigns before returning, so the node is committed before we can refuse, and a bad status afterwards does not undo it. That is why steps 1 and 6 exist. It is compensating logic, the same category as the post-commit repair rejected earlier in design.

What makes it acceptable rather than a repeat: four lines in one method, under a lock already held, no concurrency, no cross-method protocol. The machinery being removed was a distributed invariant across two files coordinated by thread-static state. This is a local rollback.

It remains worse on this axis than `OnSimpleWriteValue`, where nothing is committed until the handler returns. That is the trade made deliberately: a local rollback in exchange for no behavioural regressions.

### Why ordinary refusals return `Good`

Validation, a cancelling hook, and a non-round-tripping converter all return `Good` with the model's value stored. Preventing the bad commit is what correctness needs and step 5 achieves it. Reporting the refusal is a separate goal that would cost a wire-visible contract and turn refusals that are invisible today into errors on upgrade day.

Clients are still better off: a read-back returns the model's value rather than confirming a write that never landed, and subscribers never see the refused value.

Bad statuses are returned on exactly two paths: an unregistered property, and a failure where the model did not move. Returning real errors on ordinary refusal stays available as a separable follow-up, alongside [#231](https://github.com/RicoSuter/Namotion.Interceptor/issues/231) for WebSocket, and adding it later does not change the invariant.

### What gets deleted

The concept of recognising our own reflection disappears rather than being reimplemented:

- `IsWritingOwnNodeValues` (:44) and `SelfWrittenNodeValue` (:55), two `[ThreadStatic]` fields with roughly eighteen lines of comment explaining the coordination
- their seven touch points (:44, :55, :167, :192, :200, :201, :411)
- the `try`/`finally` in the outbound loop, which exists only to disarm the flag
- the `StateChanged` subscription (`CustomNodeManager.cs:408-416`), which has exactly one subscriber

This is the machinery `b8ecc22f` had to repair, and identifying our own echo by comparing values loses data whenever the comparison is wrong.

Verified, not assumed. The two fields have one reader (:411), `UpdateProperty` has one caller (`CustomNodeManager.cs:414`), `StateChanged` has one subscriber (:408). Node values are written in exactly three places: creation (`CustomNodeManager.cs:395`, masks cleared at :406 before the handler is attached), the outbound loop (assigns through the property setter, never reaching `WriteValueAttribute`), and the SDK write service (now covered by the override). Monitored items use the SDK's own `OnStateChanged` field, not the `StateChanged` event, so removing the subscription cannot affect subscriptions. The subclass still satisfies the outbound loop's `data is BaseDataVariableState` check.

Echo suppression is unaffected because it is origin-based (`ChangeQueueProcessor.cs`), not revision-based. The outbound loop's per-write supersession recheck (`OpcUaSubjectServer.cs:177`) still closes the local-write race, because the apply advances the source-commit marker under the same lock hold.

### Comments that become wrong

Part of the change, not follow-up:

- `OpcUaSubjectServer.cs:20-24` states that a client write reaches the node before `UpdateProperty` applies it. That inverts. The `SourceValuesAreSettled` conclusion survives; its mechanism becomes "the write attribute settles the node before the lock releases".
- `CustomNodeManager.cs:403-406` loses its "before the handler is attached" rationale.
- The lock in `RemoveSubjectNodes` stays, but its comment narrows to monitored-item consistency, since misattribution is no longer possible.

## Explicitly out of scope

- Late-attached subjects never getting a node (finding 1)
- MQTT and WebSocket parity for refused writes
- Returning error status codes on ordinary refusal
- The client-side equivalent. `SubscriptionManager.cs:232-236` and `PollingManager.cs:422-427` swallow the same way, but the other store belongs to a foreign server, so the repair is write-back or resync and depends on the ownership contract that [#342](https://github.com/RicoSuter/Namotion.Interceptor/issues/342) and [#362](https://github.com/RicoSuter/Namotion.Interceptor/issues/362) exist to settle.
- The memory and retention cluster ([#281](https://github.com/RicoSuter/Namotion.Interceptor/issues/281), [#441](https://github.com/RicoSuter/Namotion.Interceptor/issues/441)), a different code path

## Acceptance criteria

1. **The invariant holds** for all five rows, and after recovery.
2. **No new problems.** Every existing OPC UA test passes unchanged. Type checking, index range writes and client source timestamps all keep working. No client that receives `Good` today receives an error afterwards, except on an unregistered property.
3. **No performance regression.** See below. The core library is untouched, so core benchmarks must be unmoved; a moved one means something was changed that should not have been.
4. **Simplification.** The two thread-static fields, their seven touch points and the `StateChanged` hookup are gone, or the reason they had to stay is written down.

## Performance

**Inbound, per client write:** one `GetValue()` through the read interceptor chain, one extra conversion, and one `TryGetWriteState` (two `Interlocked.Read`). Against a network round trip, deserialization, session lookup and taking `NodeManager.Lock`, this is well under a percent. The default `CopyPolicy` is `CopyOnRead` (`BaseVariableState.cs:64`), so the write-side clone at :2005 does not run, which makes the extra outbound conversion a genuinely new allocation for array properties rather than a second copy. That is the number worth measuring.

**Outbound, per change:** less work. Today every outbound change performs a `SelfWrittenNodeValue` thread-static store, a `StateChanged` dispatch into our closure, and a guard check calling `Equals`. All of it exists only to recognise our own reflection and all of it goes. Added back: one `StatusCode` assignment and one `try` per change. At 20k changes per second outbound against occasional inbound writes, this direction dominates.

**Verification:** connector tester on the opcua profile, comparing `IncomingThroughput` and `OutgoingThroughput` before and after, plus an allocation check on an array-heavy write. Microbenchmarks do not cover this code and are run only to confirm they have not moved.

**Fix the accounting first.** `IncomingThroughput.Add(1)` currently runs before the registration check (`OpcUaSubjectServer.cs:418`). Decide what the reshaped method counts and write it down before measuring, or the comparison measures the accounting change.

## Test plan

Written red first. Each asserts the invariant and fails on current code for the reason named.

**The five defect rows:**

| Test | Arranged with | Passes when |
|---|---|---|
| `WhenValidationRejectsAClientWrite_ThenTheNodeKeepsTheModelValue` | A validating interceptor that throws | Read-back returns A, subject holds A, status Good |
| `WhenAnOnChangingHookCancelsAClientWrite_ThenTheNodeKeepsTheModelValue` | A generated hook setting `cancel` | Read-back returns A, subject holds A, status Good |
| `WhenTheInboundConverterThrows_ThenTheServerStaysAliveAndTheNodeKeepsTheModelValue` | A converter throwing in `ConvertToPropertyValue` | No exception reaches the SDK, server still serves reads, read-back returns A |
| `WhenTheConverterPairDoesNotRoundTrip_ThenTheNodeHoldsTheConvertedModelValue` | A scaling converter where f(g(x)) is not x | Read-back equals `ConvertToNodeValue(subject value)`, stable across two reads |
| `WhenThePropertyIsNotRegistered_ThenTheWriteIsRefusedAndTheNodeIsUntouched` | A property reference with no registration | Client receives a bad status, node value unchanged |

**The two recovery branches, and the assumption they rest on:**

| Test | Arranged with | Passes when |
|---|---|---|
| `WhenTheApplyIsRefusedAndTheReadBackThrows_ThenTheNodeIsRestoredAndTheWriteIsRefused` | Cancelling hook plus a throwing `ConvertToNodeValue` | Client receives a bad status, node holds A with its original status, no exception escapes |
| `WhenTheApplyCommitsAndTheReadBackThrows_ThenTheNodeReportsUncertain` | Committing write plus a throwing `ConvertToNodeValue` | Client receives Good, node holds the client's value, status is `UncertainLastUsableValue` |
| `WhenTheModelLaterHoldsAConvertibleValue_ThenTheStatusReturnsToGood` | The above, then a model-side write of a convertible value | Node holds `f(new)`, status Good, subscribers notified |
| `WhenAnApplyIsCancelled_ThenTheCommitRevisionDoesNotAdvance` | A cancelling hook | Revision before equals revision after. Pins the discriminator both branches depend on |

**The outbound wrap:**

| Test | Arranged with | Passes when |
|---|---|---|
| `WhenOneChangesConversionThrows_ThenTheRestOfTheBatchIsStillWritten` | A batch where one property's `ConvertToNodeValue` throws | Every other property in the batch reaches its node; the failing one reports Uncertain |

**Regression guards.** These pass today and must keep passing. They are what the design choice buys:

| Test | Arranged with | Passes when |
|---|---|---|
| `WhenAClientWritesTheWrongType_ThenTheSdkStillReturnsBadTypeMismatch` | String written to an int node | `BadTypeMismatch`, neither store moves |
| `WhenAClientWritesAnIndexRange_ThenTheMergedArrayReachesTheSubject` | Write to `myArray[2:4]` | Subject holds the merged whole array, not the fragment |
| `WhenAClientSuppliesASourceTimestamp_ThenTheModelRecordsIt` | Write with an explicit `SourceTimestamp` | The subject's change timestamp matches what the client sent |
| `WhenAClientWriteIsAccepted_ThenBothStoresHoldIt` | Plain writable property | Subject and read-back both hold B, status Good |
| `WhenTheServerWritesItsOwnValue_ThenNoInboundApplyIsTriggered` | Model-side mutation flushed by the outbound loop | No inbound apply observed, no echo |

Conventions: `When<Condition>_Then<ExpectedBehavior>` naming, explicit `// Arrange`, `// Act`, `// Assert`, and `AsyncTestHelpers.WaitUntilAsync` rather than delays. Most rows need a live server and client and belong with the existing integration harness (`SharedServerTestBase`). The unregistered row and the revision-does-not-advance row are reachable without one.

The OPC UA suite binds a fixed port and cannot run concurrently with another instance of itself or with the connector tester.

## Risks and open items

- **The revision discriminator is load bearing and unverified.** The whole recovery branch assumes a cancelled apply does not advance the commit revision. Its test is listed above and must be written first; if the assumption is false the recovery design changes.
- **Reliance on base behaviour.** The design assumes `base.WriteValueAttribute` assigns before returning. That is behaviour, not contract. The regression guards would catch a change.
- **Reading the model inside the SDK write call.** `GetValue()` runs the property getter under `NodeManager.Lock`. Cheap for a stored property, not necessarily for a derived one.
- **The correction re-sets the change mask.** Assigning `Value` in step 5 or 6 sets it again after base already did, so the node reports a change even on a refusal. Whether subscribers see it depends on the `DataChangeFilter` trigger, since value-equal notifications are filtered for the default. Test rather than assume.
- **A stuck Uncertain.** If a property goes Uncertain and never changes again, the status persists. That is correct, since the node's value is still not known to match, but it should be visible in diagnostics.
- **Lock ordering is unchanged.** The apply already runs under `NodeManager.Lock` today by way of `StateChanged`.
- **An SDK backstop exists.** `NodeState.WriteAttribute` catches handler exceptions and returns `BadUnexpectedError` with the node untouched (`NodeState.cs:3773-3789`). The override still catches everything itself so outcomes are uniform, but a bug degrades to a clean per-write error rather than service-layer damage. This is a gain over the current path, where the escape through `ClearChangeMasks` has no catch anywhere.
- **Predefined nodes are unaffected.** `LoadPredefinedNodes` may create plain `BaseDataVariableState` instances, which carry no subject property and need no override.
