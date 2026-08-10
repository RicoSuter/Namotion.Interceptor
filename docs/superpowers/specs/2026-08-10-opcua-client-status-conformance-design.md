# OPC UA client: status conformance, batch starvation, read-after-write ordering

**Goal:** the client treats a `DataValue`'s status the way the standard expects on every inbound path, a failing write stops starving the writes behind it, and the read-after-write guard ranks values soundly.

**Base:** master at `f561d196`. Merges before the server write integrity PR, which answers bad statuses on refusal.

**Three commits, three risk profiles.** The inbound rule is small and uncontroversial. The batch-starvation fix is in shared code. The read-after-write change switches on a feature that is currently inert. Reviewable and revertible separately.

## Commit 1: one rule for an inbound value

### The defect

| Path | Good | Uncertain | Bad |
|---|---|---|---|
| Subscription (`Client/Connection/SubscriptionManager.cs:200-207`) | apply | apply | **apply** |
| Polling (`Client/Polling/PollingManager.cs:364-374`) | apply | **dropped, no log, no metric** | log + metric |
| Initial and reconnect load (`Client/OpcUaSubjectClientSource.cs:237`) | apply | skipped silently | skipped silently |
| Read-after-write (`Client/ReadAfterWrite/ReadAfterWriteManager.cs:323-326`) | apply | skipped silently | skipped silently |

Uncertain is routine in industrial servers, so this is already wrong against the installed base.

Three more in the same code:

- **Polling never converts.** `PollingManager.cs:390` and `:409-414` apply `dataValue.Value` raw while the other three paths call `ConvertToPropertyValue`. A `decimal`, `decimal[]` or `Uuid`-delivered `Guid` property therefore throws in the setter, is swallowed at `:424-427`, and never updates via polling, with a user converter simply not applied.
- **A throwing apply during the initial load wedges the source permanently.** `SubjectPropertyWriter.LoadInitialStateAndResumeAsync:111` invokes the load closure inside the lock with no `try`. A throw skips `_updates = null` (`:136`) and the transition to `Synchronized` (`:143`), propagates to `SubjectSourceBase.cs:215`, is caught at `:249`, and the connect retries. A deterministic throw means the source never reaches `Synchronized`.
- **The load reports success for properties it skipped**, twice: `OpcUaSubjectClientSource.cs:245` and `:254` both use the requested count, and the second claims properties were *updated*.

### The change

`StatusCode.IsNotBad(status)` is exactly Good plus Uncertain, so the rule is one SDK call at each site, not a new abstraction:

- three `IsGood` become `IsNotBad` (`PollingManager.cs:364`, `OpcUaSubjectClientSource.cs:237`, `ReadAfterWriteManager.cs:323`)
- one `IsNotBad` guard is added in the subscription loop (`SubscriptionManager.cs:200-207`)
- polling converts, after the equality check so the change-detection cache stays raw, and the status check stays ahead of `ProcessValueChange` so a rejected value never poisons `LastValue`
- polling gets an Uncertain arm for its metrics; today `RecordRead` fires only on Good and `RecordFailedRead` only on Bad
- a per-item `try` goes inside the initial-load closure. **Not** a wrapper at `SubjectPropertyWriter.cs:111`, which was an earlier idea and is actively harmful: the WebSocket load closure calls `ClaimPropertyOwnership()` *after* its apply (`WebSocketSubjectClientSource.cs:296-299`), and that is its only call site. Swallowing a throw there and falling through to `Synchronized` leaves the source owning no properties, so every outbound filter (`TryGetSource(out var s) && s == this`) fails and the connector sends nothing, forever, with a green status and no retry. Strictly worse than the wedge. The per-item `try` in the OPC UA closure is the correct instrument; MQTT is unaffected because its load returns null
- both load logs report the applied count

**No shared apply helper.** An earlier revision proposed one. The four sites differ in dispatch, pooling, cache updates, deferral and metrics, so the consolidation is break-even at best, and any helper taking a closure or a state object would add allocations to `OnFastDataChange`, which is allocation-free today by design (pooled list, value-tuple state, static lambda).

**Log Bad on transition, not per value.** Bad is sticky, so a faulted sensor reports it every sample. One warning on the first Bad per monitored item and one on recovery, rather than a `params object?[]` and a boxed `StatusCode` per notification.

## Commit 2: a failing batch stops condemning the rest of the flush

### What actually goes wrong

`SubjectSourceExtensions.cs:93-111`: when the source advertises a `WriteBatchSize` (OPC UA uses the server's `MaxNodesPerWrite`, `OutboundWriter.cs:33`) and a flush exceeds it, a failure in batch *k* returns the failed batch **plus the entire unprocessed remainder**. Batches *k+1..n* are never sent, every tick, for as long as the failure persists.

`WriteRetryQueue.RequeueChanges:214` makes it reachable: it does not collapse per property, so a continuously-written property the server keeps refusing accumulates one entry per flush tick until the flush crosses `MaxNodesPerWrite` and the benign regime becomes permanent starvation.

The ring eviction is **not** the harm. Requeued failures are inserted at index 0 (`:214`) and eviction drops from index 0 (`:73`), so a poison write evicts itself first and never other properties.

### The change

Two parts, and the order between them is load bearing.

**Collapse per property at dequeue time**, inside `FlushAsync`, not on the requeued span. This has to come first because it is what makes the second part safe.

Collapsing only the requeued span is insufficient, and it is what an earlier revision proposed. The buffer holds **two** entries for a continuously-written poison property, not one: the requeued failures go in at index 0 (`WriteRetryQueue.cs:170, 214`) and the same tick then appends its own merged changes at `SubjectSourceBase.cs:294`, because a failed flush enqueues rather than writes. `ChangeMerger` dedupes within a tick and never against the queue. Collapsing at dequeue covers both, and makes "the buffer holds at most one entry per property" provable rather than asserted.

**Then continue past a failing batch** instead of condemning the unprocessed remainder (`SubjectSourceExtensions.cs:87-115`).

Without the dequeue-time collapse this would **violate a documented guarantee**. `docs/connectors.md:791` states "Ordering within a single property is preserved". With `[P@rev5 … P@rev9]` in one flush, a wholesale failure of the batch carrying `rev5` (a timeout or channel error, not a per-item refusal) while the batch carrying `rev9` succeeds leaves `rev5` requeued alone and re-sent next tick, so **the source settles on the older value permanently**. Nothing rescues it: the retry-queue flush calls `WriteChangesInBatchesAsync` directly and runs no delivery filter. The spec's earlier justification, that a later batch applying while an earlier retries is already true across ticks, holds for different properties and is false for the same one.

**The exception path needs its accounting corrected too.** `SubjectSourceExtensions.cs:120-127` computes failures as `changes.Slice(confirmedCount)`, a prefix invariant that only holds while the loop stops at the first failure. Under "continue", batches that succeeded after an earlier failure would land in `FailedChanges`, and `SourceTransactionWriter` reverts only `sourceChanges \ failedSet` (`Transactions/SourceTransactionWriter.cs:173-180`), so a change that reached the source but was reported failed is never rolled back: the transaction reverts locally and the source keeps the new value. On the throw path, failures are the accumulated per-batch failures plus the throwing batch plus the unprocessed remainder, never a prefix slice.

Constraints on the collapse:

- **Do not reuse `SubjectSourceBase.CollapsePerProperty`.** It falls back to `WithoutRevision()` (`:418-423`), and a revision-less change makes the supersession check pass unconditionally at `ChangeDeliveryFilter.IsSupersededBy:115`, so a later reconcile could restore an older parked write over a newer local one. Merge only when both revisions are non-zero; otherwise keep both entries. Note this rule is stricter than the status quo: the queue's own other producers, the connect-window drain (`SubjectSourceBase.cs:379`) and the reconcile (`:504`), already enqueue `CollapsePerProperty` output and so can already park a revision-less change. That is a pre-existing gap this PR does not widen and does not fix.
- **Do not allocate per flush.** `CollapsePerProperty` allocates a list and a dictionary per call, and during an outage the flush fails every tick. Use a reusable index dictionary owned by the queue and cleared per use, the pattern `ChangeMerger._propertyIndices` already uses, compacting in place. The survivor keeps the first occurrence's position so ordering is not reshuffled.
- **Break rather than continue on cancellation**, and decide explicitly whether a source that throws rather than returning an error result also continues. OPC UA never throws out of `WriteChangesAsync` (`OutboundWriter.cs:66-69`); MQTT and WebSocket may.

Residual: if the number of distinct pending properties alone exceeds `MaxNodesPerWrite`, the continue is what keeps the rest moving.

Three existing tests pin the condemn-the-remainder contract and are rewritten: `SubjectSourceExtensionsTests.cs:101`, `:227`, `:326`.

## Commit 3: read-after-write ranks soundly

### It is inert today, and it is on by default

`OutboundWriter.cs:166` sends `SourceTimestamp = change.ChangedTimestamp`, a conforming server preserves it, and `WriteInterceptorFactory.cs:38` stores that same value as the property's write timestamp. So after a local write the guard at `ReadAfterWriteManager.cs:333` compares a value to itself and always skips. The feature has never fired against a timestamp-preserving server.

`EnableReadAfterWrite` defaults to **true** (`OpcUaClientConfiguration.cs:224`), and the documented recipe for a trigger variable arms it per property (`docs/connectors-opcua-client.md:338`). So this commit turns on a feature for the most behaviour-sensitive class of property, not a niche one. That is the real change here.

### Two questions, two domains

The existing comparison is **sound in the case the guard exists for**. When the last commit came from a subscription or polling apply, the stored write timestamp *is* the server's `SourceTimestamp`, so comparing it against the read-back's is server clock against server clock, the only correct way to rank two server-produced values. It is cross-clock only when the last commit was local, which is exactly the inert case.

So keep both comparisons rather than replacing them with one:

- **Did a local write land since we sent ours?** Capture the change's revision at write-build time, and skip if `TryGetWriteState(false, out localRevision, out _)` has advanced past it. Local ordering, no clock.
- **Otherwise, did a source commit land?** If `TryGetWriteState(true, ...)` exceeds the local revision, the last commit came from a source, so compare `TryGetWriteTimestamp() >= result.SourceTimestamp`. Server against server.

`includeSourceCommitsInRevision: true` alone would be wrong, and its own documentation says so (`PropertyReference.cs:113-118`): a notification sampled before our write but arriving after it commits at a higher source revision, the fresher `maxAge:0` read-back is skipped, and the model keeps the pre-write value with nothing to redeliver it.

**Use the change's own revision, never a fresh `TryGetWriteState` read at notify time.** `NotifyPropertiesWritten:188` runs after `session.WriteAsync` returns, so re-reading the property there would fold in a local write that committed while the request was in flight. `SubjectPropertyChange.Revision` is public (`:49`) and `NotifyPropertiesWritten` iterates the changes. A revision of 0 means the change was built outside a terminal write; fall back to applying. The revision rides on the existing value tuples (`ReadAfterWriteManager.cs:30, 33`), so this allocates nothing.

**Only schedule read-backs for writes that succeeded.** `OutboundWriter.cs:53-57` notifies for the whole batch including failures, contradicting the documentation (`docs/connectors-opcua-client.md:334`). Inert today; once the feature fires, a read-back would apply the server's pre-write value over a local write the retry queue still holds and will re-send, so the model flips.

**Per-item handling in `ProcessDueReadsAsync`.** A throw on one item currently aborts the rest of the batch and trips the circuit breaker at `:354`, attributing a local apply failure to the remote read.

**This does not close [#373](https://github.com/RicoSuter/Namotion.Interceptor/issues/373).** Subscriptions and polling still have no ordering guard, and their case does not generalise: an unsolicited notification carries no local before-point. #373 is updated, not closed.

## Out of scope

Value quality on the model or in diagnostics ([#299](https://github.com/RicoSuter/Namotion.Interceptor/issues/299)). Any server change. The general inbound ordering problem. New public counters.

## Acceptance criteria

1. All four inbound paths agree on Good, Uncertain and Bad, and all four convert.
2. A Good value behaves exactly as today on every path.
3. No public API change.
4. A persistently refused write does not stop later batches in the same flush from being sent.
5. A throwing apply during the initial load no longer prevents the source reaching `Synchronized`, on every connector.
6. No new allocation on `OnFastDataChange` or on a successful flush.

## Expected size

Commit 1 about 20-35, commit 2 about 30-45, commit 3 about 35-50. **Total roughly +85 to +130.** Tests 400-600.

## Test plan

Written red first, and driven end to end rather than through an extracted predicate. The harness can already emit real non-Good statuses today: `OpcUaSubjectServer.TryGetVariableNode` plus the server's `NodeManagerLock` let a test set a node's `StatusCode` and `ClearChangeMasks`, exactly as `OpcUaCrossStoreConvergenceTests.cs:44-60` already does for values. Acceptance criterion 1 is about four call sites agreeing and is not provable by testing a predicate. The outbound work uses refusals the SDK produces on its own, since writing to a setter-less property yields `BadNotWritable`, and the starvation test must lower `MaxNodesPerWrite` or use a stub source, because our own server advertises 4000 (`OpcUaServerConfiguration.cs:193`).

**Commit 1:** the status predicate over Good, Uncertain and Bad; a Bad subscription notification is not applied; an Uncertain polled value is applied; a polled Bad does not poison the change-detection cache; a `decimal` property updates via polling; a throwing apply during the initial load still reaches `Synchronized`; both load logs report the applied count.

**Commit 2:** a poison write does not stop later batches in the same flush being delivered, with `WriteBatchSize` smaller than the queue, since it passes on master otherwise; repeated requeues keep one entry per property; a change with revision 0 is not merged away; a failed flush allocates no list or dictionary.

**Commit 3:** a local write landing before the read-back wins; a subscription value landing before the read-back wins; a local write landing while the request is in flight wins; with nothing changed the read-back applies, which also proves the feature fires at all; a failed write schedules no read-back; one item's throw does not abort the batch or trip the breaker.

Conventions: `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange`, `// Act`, `// Assert`, `AsyncTestHelpers.WaitUntilAsync` rather than delays. The OPC UA suite binds a fixed port and cannot run concurrently with itself or the connector tester.

## Risks

- **Subscriptions stop applying Bad values.** Today they land in the model. Depending on that is depending on a value the server declared invalid, but it is a behaviour change for the release notes.
- **Polling starts applying Uncertain values and starts converting**, so values appear where they previously vanished and a custom converter starts being applied.
- **Read-after-write starts firing**, on properties configured by the documented trigger-variable recipe.
- **`IncomingChangesPerSecond` shifts** against a server that emits Bad.
- **A permanently Bad property freezes at its last good value** while the source still reports `Synchronized`, so consumers treat the model as trustworthy. Pre-existing on three of four paths, now uniform, and the honest argument that #299 is not really out of scope.
