# OPC UA server: array ownership, batch isolation, throughput accounting

**Goal:** three correctness fixes in the OPC UA server, none of which changes a status code a client sees.

**Base:** master at `f561d196`. Branch parent of the inbound write integrity PR. Independent of the client PR.

**Why separate:** these fix real bugs and carry none of the decisions the write-path redesign carries. Landing them first means the redesign is reviewed against a codebase where the array bug is gone, and the silent data bug is fixed even if the redesign stalls. An earlier revision billed this as "no trade-offs, no decisions"; that was too strong, and the two decisions are named below.

## Fix 1: the node and the subject share array instances

`ConvertToNodeValue` returns the property's array **by reference** for any non-decimal element type (`OpcUaValueConverter.cs:87`). That instance is assigned to the node at creation (`CustomNodeManager.cs:395`) and on every outbound write (`OpcUaSubjectServer.cs:190`), and the inbound apply hands the node's array back to the model (`:428`).

The SDK's index range merge mutates the destination **in place** (`NumericRange.cs:761-764` for the 1-D case; also `:690-693` scalar ByteString, `:904` N-dimensional, `:930-937` nested ByteString).

So a client writing `myArray[2:4]`:

1. mutates the model's array directly, because the node holds the same instance
2. reaches OPC UA subscribers on this server, because the notification path deep-clones on read under the default `CopyPolicy = CopyOnRead` and therefore sees a change
3. but publishes **no change through the interceptor chain**, so no other connector, no derived property and no observer learns of it

Subscribers and the model permanently disagree, and nothing indicates why. That is the bug.

Point 3 holds only with `WithEqualityCheck()` in the context (`InterceptorSubjectContextExtensions.cs:44`), which `WithFullPropertyTracking()` includes but the server does not require. Without it the write reaches the terminal and a change is published with `OldValue` and `NewValue` being the same instance. **The acceptance test must run on a full-tracking context or it passes vacuously.**

### The rule

The node and the subject never share an array instance. One predicate, three crossings:

- **node creation** (`CustomNodeManager.cs:395`)
- **outbound write** (`OpcUaSubjectServer.cs:190`), and the same instance must also be stored in `SelfWrittenNodeValue` at `:192`
- **inbound apply** (`OpcUaSubjectServer.cs:428`)

Three crossings are necessary and sufficient. Dropping the inbound copy lets a full client write re-alias the model; dropping the outbound copy re-aliases on every model change; dropping the creation copy leaves the initial state aliased.

**The `SelfWrittenNodeValue` detail is critical, not incidental.** The self-write guard at `:411` is `Equals(value, SelfWrittenNodeValue)`, which for arrays is reference equality, and the SDK's getter returns the field uncloned. Storing the original while assigning a copy makes the guard miss on every outbound array write: the node's flush is treated as a client write, the copy is applied back into the model, and a spurious change is published to every other connector. The fix would become a worse bug than the one it fixes. Compute the copy once and use that instance in both places.

**Decision 1: the copy is shallow, and `byte[][]` is knowingly excluded.** `Array.Clone` covers every array type this repo maps today. It does not cover a jagged ByteString array, where the nested merge mutates the inner `byte[]` in place (`NumericRange.cs:930-937`). No such property exists in the repo, so this is latent. `Opc.Ua.Utils.Clone` would be deep and is the alternative if that changes.

**Only writable array nodes are protected.** The outbound copy is gated on `value is Array && (AccessLevel & AccessLevels.CurrentWrite) != 0`, which is necessary and sufficient because the merge is the only in-place mutator and `WriteValueAttribute` rejects on access level first (`BaseVariableState.cs:1915-1919`); setter-less properties are `CurrentRead` only (`CustomNodeManager.cs:376-379`). Read-only array nodes stay aliased with the model deliberately, which keeps the hot path clean and is safe because nothing can merge into them.

Copying at the crossings rather than inside `ConvertToNodeValue` is deliberate: that method is `public virtual`, so a custom converter would not honour a guarantee placed there.

### Alternatives that do not work

- **`CopyPolicy = VariableCopyPolicy.Always` does not fix this.** `CopyOnWrite` clones the incoming *fragment* (`BaseVariableState.cs:2004-2008`) before the merge; `m_value` is still mutated in place at `:2033-2034`. This is the first thing an SDK-literate reader will suggest.
- **An `OnSimpleWriteValue` handler** prevents the aliasing but returns `BadIndexRangeInvalid` for every index range write (`:2015-2019`), which is a status code change.
- **Skipping the copy when the converter returned a different instance** is unsound: a custom converter can return an array aliasing some other part of the model.

## Fix 2: one bad conversion discards the whole batch

`ConvertToNodeValue` and the node assignment in the outbound loop (`OpcUaSubjectServer.cs:187-190`) are unwrapped. A throw propagates to `ChangeQueueProcessor.cs:330`, is caught at `:336-339`, and the entire merged batch is discarded with every unrelated property's change in it.

Wrap per change. The skipped change **must be logged with the property identity**: by the time the write loop runs, `ChangeMerger.SuppressSupersededChanges` has already marked the property published to source (`ChangeDeliveryFilter.cs:25, 36`), so there is no retry path and the node stays stale until that property changes again. A converter throwing on every change of a hot property will then log at flush rate rather than once per batch; that is still the right trade.

The per-change `catch` must not swallow `OperationCanceledException`, and the `IsWritingOwnNodeValues` and `SelfWrittenNodeValue` reset stays the outer `finally`'s responsibility (`:198-202`).

## Fix 3: throughput counts writes that never happen

`IncomingThroughput.Add(1)` runs at `OpcUaSubjectServer.cs:418`, before the registration check at `:421`.

**Decision 2, and it is a genuine one.** Counting received traffic that was dropped is arguably right for diagnosing "my data is not arriving". Moving it after the registration check makes it "writes to registered properties", which is still not "writes applied", since the counter fires even when the apply throws at `:430-433`. This spec moves it after the registration check and updates `docs/connectors-opcua-server.md:259`, which documents `IncomingChangesPerSecond` as "client writes to server". The reason to move it at all is that the stacked PR's before-and-after measurement uses this counter.

## Out of scope

The inbound write path itself: what happens when an apply is refused, `UpdateProperty`'s failure handling, and any status code a client sees.

## Acceptance criteria

1. An index range write reaches the model **and publishes a change through the interceptor chain**.
2. No **writable** array node shares an instance with the subject. Read-only array nodes deliberately still do.
3. An outbound array write is not applied back into the model.
4. A throwing conversion costs one property, not a batch, and is logged.
5. No status code a client sees changes, and no change to the value read back from a written node.
6. Scalar properties allocate nothing new on the outbound path.

## Expected size

Roughly 30 to 35 production lines. Tests 250 to 400, which is the bulk.

## Test plan

Written red first.

| Test | Passes when |
|---|---|
| `WhenAClientWritesAnIndexRange_ThenAChangeIsPublishedThroughTheInterceptorChain` | a property-changed subscription on a full-tracking context observes the merged array. Red today |
| `WhenTheNodeHoldsAWritableArray_ThenItIsNotTheSubjectsInstance` | not reference equal. Red today |
| `WhenAWritableArrayIsWrittenOutbound_ThenNothingIsAppliedBackToTheSubject` | pins the `SelfWrittenNodeValue` detail. Would be red against a naive fix |
| `WhenOneChangesConversionThrows_ThenTheRestOfTheBatchIsStillWrittenAndTheSkipIsLogged` | every other property reaches its node. Red today |
| `WhenAWriteTargetsAnUnregisteredProperty_ThenIncomingThroughputDoesNotCountIt` | the counter does not move. Red today |

**Regression guards, green today and must stay green:**

| Test | Passes when |
|---|---|
| `WhenAClientWritesAnIndexRange_ThenTheMergedArrayReachesTheSubject` | subject holds the merged whole, not the fragment |
| `WhenAReferenceTypedScalarIsWrittenOutbound_ThenNoCopyOccurs` | `Assert.Same` on a `string` property. Not an `int`: boxing makes `ReferenceEquals` false either way, so an `int` version would assert nothing |
| `WhenAReadOnlyArrayIsWrittenOutbound_ThenNoCopyOccurs` | pins the narrow condition against a later well-meaning widening |

The index range tests need a raw `session.WriteAsync` with `WriteValue.IndexRange`; `client.Source.CurrentSession` is public and already used this way (`OpcUaReadWriteTests.cs:30`).

Use a dedicated `OpcUaTestServer` with `OpcUaTestPortPool`, as `OpcUaServerSelfWriteTests` does, **not** `SharedServerTestBase`: attaching a second observer to the shared fixture's subject leaks into every other test in the assembly.

Conventions: `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange`, `// Act`, `// Assert`, `AsyncTestHelpers.WaitUntilAsync` rather than delays. The OPC UA suite binds a fixed port and cannot run concurrently with itself or the connector tester.

## Performance

Three new costs, not one:

- **Node creation:** a permanent per-array-property memory doubling. Today the node aliases the model for free.
- **Outbound:** an O(n) copy per write to a **writable array** property. Scalars and read-only arrays are untouched, which is the overwhelming majority of the 20k/s path. A large, frequently-changing writable array is a new allocation hot spot.
- **Inbound:** an unconditional copy per client write. For `decimal[]` this double-allocates, since `ConvertToPropertyValue` already returns a fresh array (`OpcUaValueConverter.cs:41-46`); symmetrically the outbound copy copies a copy for `decimal[]` (`:79-84`).

Measured with the connector tester `opcua-load` in two-process mode against master, CPU pinned, comparing `performance-{participant}.csv`. Core benchmarks are a tripwire only.

## Risks

- **A consumer relying on the shared instance** for writable arrays would stop seeing in-place mutations reflected in the node. It never published a change either, so depending on it means depending on the bug.
- **The narrow outbound condition** is correct only while the index range merge remains the sole in-place mutator. The regression guards pin it so an SDK change surfaces as a test failure rather than silent corruption.
- **`IncomingChangesPerSecond` changes meaning**, documented in the same PR.
