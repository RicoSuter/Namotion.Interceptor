# OPC UA client: status conformance and retry-queue collapse

**Goal:** the client treats a `DataValue`'s status the way the standard expects, identically on every inbound path, and a write the server keeps refusing stops evicting other properties' pending writes.

**Base:** master at `f561d196`. Independent of the two server PRs, no shared files. Must merge before the server write integrity PR.

**Three commits, three risk profiles.** The inbound rule is uncontroversial. The retry-queue collapse is a small behaviour change in shared code. The read-after-write change switches on a feature that is currently inert. Reviewable and revertible separately.

## Why this is not just scaffolding

`UncertainLastUsableValue`, `UncertainSensorNotAccurate` and `UncertainSubstituteValue` are routine in industrial servers: a sensor fault, a value held over from a failed poll, a substituted value. So the behaviour below is already wrong against the installed base today, with no change to our own server.

## Commit 1: one rule for an inbound value

### The defect

| Path | Good | Uncertain | Bad |
|---|---|---|---|
| Subscription (`Client/Connection/SubscriptionManager.cs:200-207`) | apply | apply | **apply** |
| Polling (`Client/Polling/PollingManager.cs:364-374`) | apply | **dropped, no log, no metric** | log + metric |
| Initial and reconnect load (`Client/OpcUaSubjectClientSource.cs:237`) | apply | skipped silently | skipped silently |
| Read-after-write (`Client/ReadAfterWrite/ReadAfterWriteManager.cs:323-326`) | apply | skipped silently | skipped silently |

The subscription path never inspects the status, so it applies Bad values.

Two more in the same code:

- **The initial load lies twice.** `OpcUaSubjectClientSource.cs:245` logs `"Successfully read {Count}"` and `:254` logs `"Updated {Count} properties"`, both using the requested count. The second is worse because it claims properties were updated.
- **A throwing apply during the initial load wedges the source permanently.** `SubjectPropertyWriter.LoadInitialStateAndResumeAsync:111` invokes the load closure inside `lock (_lock)` with no `try`. A throw skips `_updates = null` (`:136`) and the transition to `Synchronized` (`:143`), propagates to `SubjectSourceBase.cs:215`, is caught at `:249`, and the connect is retried. A deterministically throwing apply means the source **never reaches `Synchronized`**, forever.

### The rule

| Status | Action |
|---|---|
| Good | apply |
| Uncertain | apply |
| Bad | do not apply, log |

Uncertain means usable but of degraded quality, so discarding it loses data the server was willing to give.

The predicate is `StatusCode.IsBad(status)`, a one-line SDK call. It does **not** go on `OpcUaStatusCodeClassifier`: that type answers "can this write be retried", a different question with a write-specific code set, and sharing it would conflate "not retryable" with "not usable".

### The shared apply

The four sites collapse on the status decision, the apply and the exception handling. What stays per-site because it genuinely differs: the timestamp source, the dispatch (`_propertyWriter.Write` versus direct), the pooled-list return (`SubscriptionManager.cs:240-241`), the change-detection cache update (`PollingManager.cs:398-406`), the closure deferral, and the metrics.

Two constraints on the refactor:

- **Conversion stays where it is.** Moving `ConvertToPropertyValue` from `SubscriptionManager.cs:204` into the buffered write callback would defer it to replay time during a reconnect, and `OpcUaValueConverter` is publicly overridable and takes the registered property, so a user converter would see different state. Wrap the existing per-item conversion in place instead; that is two lines and gets the same protection.
- **The polling status check stays ahead of `ProcessValueChange`.** Today it does (`:364-374`), so a rejected value never reaches `LastValue`. Calling the shared helper from inside `ProcessValueChange` would poison the change-detection cache and permanently suppress the next Good value with the same content.

**Expected size: roughly break-even.** An earlier revision claimed a net reduction. What actually collapses is a small predicate plus a `TryApply` wrapper; the sites keep their differences. Take it for the consistency.

## Commit 2: collapse the retry queue on requeue

### What actually goes wrong

An earlier revision claimed a refused write blocks all outbound writes indefinitely. Traced, that is wrong: tick N parks the new change, tick N+1 flushes `[poison, parked]` in one request, only the poison is requeued (`WriteRetryQueue.cs:171` requeues `result.FailedChanges` alone), and the parked change reaches the server. The steady state is a **one-tick lag**, not a block.

The real harm is that the requeue path does not collapse per property, unlike the main write path (`SubjectSourceBase.CollapsePerProperty:403`). A continuously-written poison property accumulates one queue entry per tick until the ring evicts **other properties'** pending writes. That is the data loss.

### The fix

Collapse per property in `WriteRetryQueue.RequeueChanges` (`:210-217`), the same way the main path already does.

That removes the eviction, needs no attempt counter, no per-change identity, no configuration, and no public API change. The one-tick lag and the five-second warning remain, and both are cosmetic.

### Why not a retry bound

An earlier revision proposed bounding attempts and dropping on exhaustion. It is unsafe. `FlushAsync` runs on every buffer tick, and during an outage the whole queue fails wholesale, so attempts are driven by local write rate rather than by server refusals. At the default 8ms `BufferTime` that is roughly 125 attempts per second per queued item, and any bound small enough to catch a poison write empties the queue within about 50ms of a network blip, destroying the buffering the queue exists for (`WriteRetryQueue.cs:9-10`).

A bound would need to count only attempts where the server answered with a per-item refusal, and `WriteResult` cannot express that today: it documents "failed means unconfirmed, not rejected" (`WriteResult.cs:13-17`). That is a larger change than this PR should carry.

**Consequence for the stacked server PR:** the poison entry still sits in the queue forever. What the server PR needs is that it stops evicting other work, which this delivers.

## Commit 3: read-after-write ranks by revision

### It is currently inert, not merely mis-ordered

`OutboundWriter.cs:166` sends `SourceTimestamp = change.ChangedTimestamp`, a conforming server preserves it, and `WriteInterceptorFactory.cs:39` stores that same value as the property's write timestamp. So the guard at `ReadAfterWriteManager.cs:333` compares `T >= T` and **always skips**. Read-after-write is a complete no-op against any timestamp-preserving server, including ours.

It is also opt-in and off by default: `DefaultSamplingInterval` is null (`OpcUaClientConfiguration.cs:118`) and `RegisterProperty` requires exactly `0` (`ReadAfterWriteManager.cs:91`). So the blast radius is small, and so is the value.

Switching to revisions **turns the feature on for the first time**. That is the real change, and it is bigger than "rank by revision instead of clock".

Separately, the comparison it replaces is the only cross-clock decision in the connector surface, and a skewed remote clock would decide it. The other `TryGetWriteTimestamp` uses are data rather than decisions (`CustomNodeManager.cs:397`, `SubjectUpdateFactory.cs:139`, `MqttSubjectServer.cs:475`), and `WriteRetryQueue.cs:156` uses `Environment.TickCount64`.

### The change

Capture the property's commit revision **at write-build time**, not when the response arrives. `OutboundWriter.NotifyPropertiesWritten:188` runs after `session.WriteAsync` returns, so a local write committing while the request was in flight would already be baked into a baseline captured there, and the read-back would overwrite it. `SubjectPropertyChange.Revision` is already public (`SubjectPropertyChange.cs:49`) and `NotifyPropertiesWritten` iterates the changes, so pass `span[i].Revision` through. A `Revision` of 0 means the change was built outside a terminal write (`SubjectSourceBase.cs:418-422`); fall back to applying.

Carry it on the pending record (`:30`, `:33`) and at `:333` skip if the current revision has advanced.

**Read the revision with `includeSourceCommitsInRevision: true`.** This matters and is not obvious. With `false`, a subscription notification landing between schedule and apply would not count, and the read-back would overwrite it, which is the exact case the guard was written for (`ReadAfterWriteManager.cs:331`). `TryGetWriteState`'s own documentation warns against `true` "for anything reached over a wire" (`PropertyReference.cs:113-118`), but that warning is scoped to the delivery filter deciding what to send; this is a local staleness check and the warning does not apply.

**This does not close [#373](https://github.com/RicoSuter/Namotion.Interceptor/issues/373).** Subscriptions and polling have no ordering guard at all, and their case does not generalise: an unsolicited notification carries no local before-point. #373 is updated, not closed.

### A separate defect in the same file, fixed here

`ReadAfterWriteManager.cs:340` is **not** unwrapped, contrary to an earlier revision of this spec: the loop is wrapped at `:297-362`. The real defect is that a throw on item *i* aborts the remaining items in the batch **and** trips the circuit breaker at `:354`, attributing a local apply failure to the remote read. Per-item handling fixes both.

## Out of scope

Exposing value quality on the model or in diagnostics ([#299](https://github.com/RicoSuter/Namotion.Interceptor/issues/299)). Any server change. The general inbound ordering problem (#373). New public counters: this PR logs and uses the metrics types that already exist per path, so `OpcUaClientDiagnostics` does not move. Dropping a poison write from the retry queue, which needs a "the server answered per-item" signal `WriteResult` cannot express.

## Acceptance criteria

1. All four inbound paths agree on Good, Uncertain and Bad.
2. A Good value behaves exactly as today on every path.
3. No public API change. `WriteResult`, `ISubjectSource`, `OpcUaClientConfiguration` and `OpcUaClientDiagnostics` are untouched.
4. A persistently refused write no longer evicts other properties' queued writes.
5. A throwing apply during the initial load no longer prevents the source reaching `Synchronized`.

## Expected size

Roughly +90 to +130 production lines: break-even on the inbound consolidation, about 10 for the collapse, about 30 for the revision capture. Tests 500 to 700.

## Test plan

Written red first. Our own server does not emit Uncertain until the stacked PR, so the inbound rule is tested at unit level; the outbound work uses refusals the SDK produces on its own, since writing to a property with no setter yields `BadNotWritable`.

**Commit 1:**

| Test | Passes when |
|---|---|
| `WhenAnInboundValueIsUncertain_ThenItIsApplied` | the value reaches the property |
| `WhenAnInboundValueIsBad_ThenItIsNotApplied` | the property is untouched |
| `WhenAnApplyThrows_ThenTheNextValueIsStillApplied` | one bad property does not abort the rest |
| `WhenAConversionThrows_ThenTheRestOfTheNotificationIsStillApplied` | the subscription path's per-item conversion |
| `WhenASubscriptionNotificationIsBad_ThenTheValueIsNotApplied` | the behaviour change; today it is applied |
| `WhenAPolledValueIsUncertain_ThenItIsApplied` | today silently dropped |
| `WhenAPolledValueIsBad_ThenTheChangeDetectionCacheIsNotPoisoned` | pins the ordering constraint |
| `WhenAnApplyThrowsDuringInitialLoad_ThenTheSourceStillReachesSynchronized` | today it never does |
| `WhenPropertiesAreSkippedDuringInitialLoad_ThenBothLogsReportTheAppliedCount` | today both report the requested count |

**Commit 2:**

| Test | Passes when |
|---|---|
| `WhenAPoisonWriteIsRequeuedRepeatedly_ThenOnlyOneEntryPerPropertyIsKept` | the queue does not grow |
| `WhenAPoisonWriteIsQueued_ThenOtherPropertiesPendingWritesAreNotEvicted` | the reason this commit exists |
| `WhenAWriteFailsOnceAndThenSucceeds_ThenOrderingIsPreserved` | the deliberate blocking still blocks |

**Commit 3:**

| Test | Passes when |
|---|---|
| `WhenALocalWriteLandsBeforeTheReadBack_ThenTheReadBackIsNotApplied` | the newer local value survives |
| `WhenASubscriptionValueLandsBeforeTheReadBack_ThenTheReadBackIsNotApplied` | pins `includeSourceCommitsInRevision: true` |
| `WhenALocalWriteLandsWhileTheWriteRequestIsInFlight_ThenTheReadBackIsNotApplied` | pins the capture point |
| `WhenNothingChangedSinceTheWrite_ThenTheReadBackIsApplied` | the guard does not over-trigger, and proves the feature now fires at all |
| `WhenOneItemsApplyThrows_ThenTheRestOfTheBatchIsAppliedAndTheCircuitBreakerDoesNotTrip` | the separate defect |

Conventions: `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange`, `// Act`, `// Assert`, `AsyncTestHelpers.WaitUntilAsync` rather than delays. The OPC UA suite binds a fixed port and cannot run concurrently with itself or the connector tester.

## Risks

- **Subscriptions stop applying Bad values.** Today they land in the model. Depending on that is depending on a value the server declared invalid, but it is a behaviour change for the release notes.
- **Polling starts applying Uncertain values**, which will look like new data on a path where it previously vanished.
- **Read-after-write starts firing.** It has been inert against timestamp-preserving servers; after this it does what it was written to do. Off by default, but anyone who enabled it sees new behaviour.
- **`IncomingChangesPerSecond` shifts** against a server that emits Bad, since those changes are no longer counted as applied.
- **A permanently Bad property freezes at its last good value**, and the source still reports `Synchronized`, so consumers treat the model as trustworthy. Pre-existing on three of four paths, now uniform. It is the honest argument for #299 not really being out of scope.
