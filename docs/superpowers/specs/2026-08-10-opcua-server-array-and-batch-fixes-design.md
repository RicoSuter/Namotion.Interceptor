# OPC UA server: array ownership, batch isolation, throughput accounting

**Goal:** three independent correctness fixes in the OPC UA server. No behaviour change visible to a client, no new types, no trade-offs.

**Base:** master at `f561d196`. Branch parent of the inbound write integrity PR. Independent of the client status conformance PR.

**Why separate:** these fix real bugs on master and carry none of the decisions the write-path redesign carries. Landing them first means the redesign is reviewed against a codebase where the array bug is already gone, and the silent data bug is fixed even if the redesign stalls.

## Fix 1: the node and the subject share array instances

`ConvertToNodeValue` returns the property's array **by reference** for any non-decimal element type (`OpcUaValueConverter.cs:87`). That instance is assigned to the node at creation (`CustomNodeManager.cs:395`) and on every outbound write (`OpcUaSubjectServer.cs:190`), and the inbound apply hands the node's array straight back to the model (`OpcUaSubjectServer.cs:428`).

The SDK's index range merge mutates the destination array **in place** (`NumericRange.cs:751-755`, reached from `BaseVariableState.cs:2031-2043`).

So a client writing `myArray[2:4]` today:

1. mutates the model's array directly, because the node holds the same instance
2. publishes no change, because `PropertyValueEqualityCheckHandler` compares with `EqualityComparer<T>.Default` (`PropertyValueEqualityCheckHandler.cs:16`), which for arrays is reference equality, so the write short-circuits before the terminal
3. therefore never reaches any other connector on that subject, never triggers a derived recomputation, never appears in a subscription

The data moves and nothing downstream learns. That is worse than losing the write, because every observer disagrees with the model and nothing indicates why.

**The rule: the node and the subject never share an array instance.** Enforced at every crossing:

- **node creation** (`CustomNodeManager.cs:395`): assign a copy
- **outbound write** (`OpcUaSubjectServer.cs:190`): assign a copy, but only when `value is Array && (AccessLevel & AccessLevels.CurrentWrite) != 0`
- **inbound apply** (`OpcUaSubjectServer.cs:428`): apply a copy

The outbound condition is necessary and sufficient. The only in-place mutation is the index range merge, and `WriteValueAttribute` returns `BadNotWritable` at `BaseVariableState.cs:1916-1919` before reaching it, while setter-less properties are `CurrentRead` only (`CustomNodeManager.cs:376-379`). Scalars and read-only arrays copy nothing, which keeps the 20k/s outbound path unchanged.

The inbound copy is unconditional. It happens once per client write, against a network round trip.

Copying at the crossings rather than inside `ConvertToNodeValue` is deliberate: that method is `public virtual`, so a custom converter would not honour a guarantee placed there.

## Fix 2: one bad conversion discards the whole batch

`ConvertToNodeValue` and the node assignment in the outbound loop (`OpcUaSubjectServer.cs:187-190`) are unwrapped. A throw propagates out of `WriteChangesAsync` to `ChangeQueueProcessor.cs:330`, is caught at `:336-339` and logged, and **the entire merged batch is discarded**, taking every unrelated property's change with it.

Wrap per change so a throwing converter costs one property's update instead of everyone's.

## Fix 3: throughput counts writes that never happen

`IncomingThroughput.Add(1)` runs at `OpcUaSubjectServer.cs:418`, before the registration check at `:421`. Writes to unregistered properties are counted as received traffic though nothing is applied.

Move it after the check. This matters beyond tidiness: it is the counter used for the before-and-after comparison when measuring the write-path redesign, and leaving it would make that comparison measure the accounting change.

## Out of scope

Everything about the inbound write path itself. This PR does not change what happens when an apply is refused, does not touch `UpdateProperty`'s failure handling, and does not change any status code a client sees.

## Acceptance criteria

1. An index range write reaches the model **and publishes a change**.
2. The node's array is never reference-equal to the subject's.
3. A throwing conversion costs one property, not a batch.
4. No behaviour change visible to a client, and no status code changes.
5. Scalar properties allocate nothing new on the outbound path.

## Expected size

Roughly 40 lines of production code, mostly a small copy helper and its three call sites.

## Test plan

Written red first.

| Test | Passes when |
|---|---|
| `WhenAClientWritesAnIndexRange_ThenAChangeIsPublishedToOtherConnectors` | a second connector on the same subject observes the merged array. Red today: no change is published at all |
| `WhenTheNodeHoldsAWritableArray_ThenItIsNotTheSubjectsInstance` | the two are not reference equal. Red today |
| `WhenOneChangesConversionThrows_ThenTheRestOfTheBatchIsStillWritten` | every other property in the batch reaches its node. Red today: all are discarded |
| `WhenAWriteTargetsAnUnregisteredProperty_ThenIncomingThroughputDoesNotCountIt` | the counter does not move. Red today |

**Regression guards, green today and must stay green:**

| Test | Passes when |
|---|---|
| `WhenAClientWritesAnIndexRange_ThenTheMergedArrayReachesTheSubject` | subject holds the merged whole, not the fragment |
| `WhenAScalarPropertyIsWrittenOutbound_ThenNoArrayCopyOccurs` | pins the narrow condition, so a later simplification cannot widen it into the hot path |
| `WhenAReadOnlyArrayIsWrittenOutbound_ThenNoCopyOccurs` | same |

Conventions: `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange`, `// Act`, `// Assert`, `AsyncTestHelpers.WaitUntilAsync` rather than delays. The index range and cross-connector tests need the live harness (`SharedServerTestBase`).

The OPC UA suite binds a fixed port and cannot run concurrently with itself or the connector tester.

## Performance

The only new cost is an array copy on outbound writes to **writable array** properties. Scalars and read-only arrays are untouched, which is the overwhelming majority of the 20k/s path.

Measured with the connector tester `opcua-load` in two-process mode against master, CPU pinned, comparing `performance-{participant}.csv`. Core benchmarks are a tripwire only, since nothing benchmarks this code.

## Risks

- **A consumer relying on the shared instance.** Anyone mutating an array in place and expecting the node to reflect it without a property write would stop seeing that. It never published a change, so nothing downstream ever saw it either, and depending on it means depending on the bug.
- **The narrow outbound condition** is correct only while the index range merge remains the sole in-place mutator. The regression guards pin the condition so a future SDK change surfaces as a test failure rather than silent corruption.
