# Inbound batch continuation

Carved out of `2026-08-30-property-sync-state-design.md`, which stays as the material for the follow-up divergence-tracking work. This piece stands alone: it is a defect fix with no new public API and no bookkeeping.

## The defect

`SubjectUpdateApplier.ApplyPropertyUpdates` loops over an update's properties with no per-property try/catch (`:42-68`). One property that throws therefore aborts every property after it in the same update, and the caller sees a single exception with no indication of how much landed.

The per-property inbound paths do not behave this way. OPC UA's subscription catches per change (`SubscriptionManager.cs:252`), polling per update (`PollingManager.cs:416-424`), the OPC UA server per property (`OpcUaSubjectServer.cs:432-439`), and MQTT per message. Only the batch applier aborts, and it is the path WebSocket uses for everything.

`docs/connectors.md:223` already documents the intended behaviour: *"if an individual update fails (the action throws an exception), the error is logged and the update is dropped"*, with *"Individual update failures don't block other updates from being applied."* In context that sentence is about one `SubjectPropertyWriter.Write` action, which on the WebSocket paths is a whole `SubjectUpdate`, so the doc is ambiguous rather than plainly false. Either way the applier is the odd one out.

## The fix

**Catch around the whole per-property body of `ApplyPropertyUpdate`.** That covers every update kind and every step that can throw, including `ConvertValue` and `TransformValueBeforeApply` (`SubjectUpdateApplier.cs:96-102`), which run before `SetPropertyValue` and would otherwise still abort the batch. Because `ApplyPropertyUpdate` is re-entered recursively for nested subjects and for collection and dictionary items, the rule applies at every depth with no per-depth special case, and a nested failure is contained at its own level rather than unwinding the composite around it.

An earlier draft restricted this to the `Value` arm, arguing that composites must abort because they mutate child subjects before writing the parent. That argument does not survive: the mutation has already happened when the exception fires, so aborting leaves exactly the same partial state and merely also drops the unrelated siblings. Examined per kind, no case gains anything from aborting:

- `ApplyObjectUpdate` with an existing item (`:135-141`) performs no parent write at all, only recursion, so a failure inside it is a nested per-property failure already covered by the rule.
- `ApplyObjectUpdate` with a new item (`:143-153`) builds the subject fully and then writes it, so skipping that write discards an unattached object and leaves no partial state at all.
- Collection and dictionary rebuilds mutate items in place (`SubjectItemsUpdateApplier.cs:104`) before writing the collection (`:124`), so the partial state is identical whether the batch continues or stops.

The honest consequence, unchanged from today but now stated: a failed composite can leave a child subtree partially mutated while the parent still references its previous value. This work does not fix that, and doing so would need the applier to stage changes before publishing them, which is a much larger change.

**Rethrow `OperationCanceledException` immediately** rather than collecting it, so a shutdown is not reported as an apply failure at the end of the batch.

**Catch every other exception, not only refusals.** The applier does not need to tell a validator's refusal from a converter's bug, and every sibling inbound path already catches broadly. This also keeps Connectors free of any dependency on how refusal is signalled, which the follow-up work can add if it needs the distinction.

**Collect and rethrow at the end.** Apply every property that can be applied, accumulate the failures, and throw once at the end if any occurred, wrapping in `AggregateException` when there is more than one. This keeps the error surface identical for callers while changing what lands in the model:

| Caller | Today | After |
|---|---|---|
| `WebSocketSubjectHandler.cs:217` (server) | aborts at the failure, logs, sends an error frame | applies the rest, logs, sends the same error frame |
| `WebSocketSubjectClientSource.cs:634` (partial update) | aborts, caught and logged by `SubjectPropertyWriter.Write` | applies the rest, same catch and log |
| `WebSocketSubjectClientSource.cs:343` (initial load) | aborts, propagates, drives a reconnect and reload | unchanged: still throws, still retries |

No signature changes: `ApplySubjectUpdate` stays `void` and still throws on failure. The applier has no logger and gaining one would change public API, so the exception remains the reporting channel.

## Why the initial load is deliberately left alone

`SubjectPropertyWriter.cs:143` invokes the load's apply action with no try/catch, so an exception there propagates out of `LoadInitialStateAndResumeAsync` and drives a reconnect and a fresh reload. An earlier draft proposed catching it, on the grounds that a deterministic failure turns this into a reconnect loop.

That is wrong, twice over.

**The throw is the retry mechanism.** Catching it means a transient failure during the initial load is never retried, and the property stays unset until the source sends it again, which for MQTT without retained messages may be never.

**A reconnect loop is the alarm, not the bug.** It is visible in reconnect metrics and logs. A silently missing property is not. Trading a loud failure for a quiet one is backwards for a library whose position is that silent divergence is the thing to remove.

So the loop stays. It is pre-existing, unchanged by this work, and the right place to address it is a give-up policy for persistently failing loads, which is its own decision.

The applier change does not need it. Its value is on the paths with no retry at all, where an aborted batch loses those properties permanently:

- the WebSocket server applying a client update, which is one shot
- the WebSocket client's partial updates, which `SubjectPropertyWriter.Write` catches and logs without retrying

On the initial-load path the applier now applies what it can before throwing, which the reload overwrites anyway, so that path is behaviour-neutral.

## Not in scope

No divergence tracking, no `SourceState.Diverged`, no new public API, no hot-path changes, so **no benchmark gate**. Staging composite updates so a failure cannot leave a child subtree partially mutated is out of scope.

## Verification

- Refuse property 3 of N in a `SubjectUpdate`; assert 4..N applied and that the caller still sees an exception. Fails on master.
- More than one failing property produces an `AggregateException` naming both.
- A failing property nested inside a collection item is skipped, its siblings apply, and the collection is still written.
- A failing `Object`, `Collection` or `Dictionary` property is skipped like any other, and its siblings still apply.
- An `OperationCanceledException` propagates immediately and is not collected.
- A refusal in the WebSocket welcome snapshot still throws and still drives a reload, pinning the retry semantics against regression.
- The WebSocket server still sends its error frame after a partial apply.
- `Connectors.Tests`, `Tracking.Tests`, `Tests`, `Validation.Tests`.
- **Connector suites required**, run locally since CI path filters skip them for shared-library changes: OPC UA, MQTT, WebSocket. The applier is shared by all three.
- Connector Tester not required: no transport or connection-lifecycle change at all.
