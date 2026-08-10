# OPC UA client: status conformance and bounded write retries

**Goal:** the client treats a `DataValue`'s status the way the standard expects, identically on every inbound path, and a write that keeps failing stops blocking every other write.

**Base:** master at `f561d196`. Independent of the two server PRs: no shared files. Must merge before the server write integrity PR, for the reason in "Bounded retries" below.

## Why this is not just scaffolding

`UncertainLastUsableValue`, `UncertainSensorNotAccurate` and `UncertainSubstituteValue` are routine in industrial servers: a sensor fault, a value held over from a failed poll, a substituted value. Refusing a write is equally routine. So the behaviour below is already wrong against the installed base today, with no change to our own server.

## The defect

### Inbound: four paths, four behaviours

| Path | Good | Uncertain | Bad |
|---|---|---|---|
| Subscription (`Client/Connection/SubscriptionManager.cs:200-207`) | apply | apply | **apply** |
| Polling (`Client/Polling/PollingManager.cs:364-374`) | apply | **dropped, no log, no metric** | log + metric |
| Initial and reconnect load (`Client/OpcUaSubjectClientSource.cs:237`) | apply | skipped silently | skipped silently |
| Read-after-write (`Client/ReadAfterWrite/ReadAfterWriteManager.cs:323-326`) | apply | skipped silently | skipped silently |

The subscription path never inspects the status, so it applies **Bad** values.

Two further problems in the same code:

- The initial load logs `"Successfully read {Count} OPC UA nodes"` using the requested count (`:245`), so it reports success for properties it skipped.
- Two paths do not wrap the apply, and the consequence is worse than uninitialized properties. `SubjectPropertyWriter.LoadInitialStateAndResumeAsync:111` invokes the load closure inside `lock (_lock)` with no `try`. A throw skips `_updates = null` (`:136`) and the transition to `Synchronized` (`:143`), propagates to `SubjectSourceBase.cs:215`, is caught at `:249`, and the whole connect is retried. **A deterministically throwing apply means the source never reaches `Synchronized` at all**, retrying forever. `ReadAfterWriteManager.cs:340` has the same unwrapped shape.

The subscription path's *conversion* is also unwrapped at the item level (`SubscriptionManager.cs:204`): its catch at `:210-216` returns the pooled list and rethrows into the SDK notification dispatch, dropping the whole notification batch.

### Outbound: a failing write blocks every other write

`OutboundWriter.ProcessWriteResults` reports every non-Good result as a failed change (`:76-116`), `SubjectSourceBase` enqueues them (`:303-306`), every later batch first flushes the retry queue which requeues and returns `false` on any error (`WriteRetryQueue.cs:154-172`), and on `false` the caller **enqueues the new changes instead of writing them** (`SubjectSourceBase.cs:290-296`).

So one write the server will never accept blocks all outbound writes from that client, indefinitely. That is #332.

## Design

### One rule for an inbound value

| Status | Action |
|---|---|
| Good | apply |
| Uncertain | apply, count |
| Bad | do not apply, log, count |

Uncertain means usable but of degraded quality, so discarding it loses data the server was willing to give.

**The predicate is `StatusCode.IsBad(status)`, and it lives on its own.** It does not go on `OpcUaStatusCodeClassifier`: that type answers "can this write be retried", a different question with a write-specific code set, and sharing it is what would conflate "not retryable" with "not usable".

### One shared apply

All four paths run convert, apply, log. Four divergent copies collapse into one helper that owns the status decision, the conversion **and** the exception handling, which is what makes the paths agree by construction rather than by discipline.

What stays per-site because it genuinely differs: the timestamp source, the dispatch (`_propertyWriter.Write` versus direct), the pooled-list return (`SubscriptionManager.cs:240-241`), the change-detection cache update (`PollingManager.cs:398-406`), the closure deferral in the initial load, and the metrics recorded.

**This will not remove more code than it adds.** An earlier revision claimed it would. What actually collapses is a small status predicate plus a `TryApply` wrapper; the sites keep their differences. Expect roughly break-even, and take it for the consistency rather than the line count.

### Bounded retries

The retry queue counts attempts per change and drops a change with a diagnostic once it exceeds the bound.

This replaces an earlier design that classified failures as permanent and dropped those. Classification is the wrong lever: `OpcUaStatusCodeClassifier`'s own definition of permanent is "the answer cannot change without a new session" (`:12-20`), and a model-side validation rule is in-process and mutable, so a server refusing on validation grounds returns something the classifier calls **transient**, which is retried forever. A bound catches that and every other persistent failure, whatever its code.

It also removes a public API change: the earlier design grew `WriteResult` so the permanent subset could reach the queue, and `WriteResult` is a public struct on `ISubjectSource.WriteChangesAsync`. A bound needs only internal per-change state in `WriteRetryQueue`, so no public surface moves and nothing has to be threaded through the four result-rebuild sites in `SubjectSourceExtensions` (`:51, :81-83, :97-111, :124-126`), one of which is live for OPC UA because `OutboundWriter.WriteBatchSize` is `MaxNodesPerWrite`.

Transient failures still block while they are within the bound, deliberately: that blocking preserves write ordering.

**This is the cross-PR contract.** The server write integrity PR returns bad statuses on refusal. Without a bound here, any such status stalls that client's entire outbound stream. It is the whole reason for the merge order.

### Read-after-write ranks by revision, not by clock

Separate commit, separable.

`ReadAfterWriteManager.cs:332-334` compares the **remote server's** `SourceTimestamp` against our own last write timestamp and decides whether to apply. A remote clock running ahead lets a stale read overwrite a newer local value; running behind, it discards a fresh one.

Read-after-write is *solicited*, so a local before-point exists. Capture the property's commit revision when the read is scheduled (`:177`), carry it on the pending record (`:30, :33`), and at `:333` skip if the revision has advanced. Purely local, monotonic, no clock.

**This does not close [#373](https://github.com/RicoSuter/Namotion.Interceptor/issues/373).** Subscriptions and polling have no ordering guard at all, and their case does not generalise: an unsolicited notification carries no local before-point and its only ordering information is a clock we have decided not to trust. #373 is updated, not closed.

This is the only cross-clock decision in the connector surface. The other `TryGetWriteTimestamp` uses are data rather than decisions (`CustomNodeManager.cs:397`, `SubjectUpdateFactory.cs:139`), and `WriteRetryQueue.cs:157` uses `Environment.TickCount64`, which is local and monotonic.

## Out of scope

Exposing value quality on the model or in diagnostics ([#299](https://github.com/RicoSuter/Namotion.Interceptor/issues/299)). Any server change. The general inbound ordering problem (#373). New public counters: this PR uses logging and the metrics types that already exist per path, so `OpcUaClientDiagnostics` does not move.

## Acceptance criteria

1. **All four inbound paths agree** on Good, Uncertain and Bad.
2. **No new problems.** A Good value behaves exactly as today on every path.
3. **No public API change.** `WriteResult`, `ISubjectSource` and `OpcUaClientDiagnostics` are untouched.
4. **A persistently failing write does not block other writes**, and is visible when dropped.
5. **A throwing apply during the initial load no longer prevents the source reaching `Synchronized`.**

## Expected size

Roughly 150 to 200 lines of production code, close to break-even on the inbound consolidation and net additive on the retry bound and the revision capture.

## Test plan

Written red first. Our own server does not emit Uncertain until the stacked server PR, so the inbound rule is tested at the unit level and the outbound rule uses refusals the SDK produces on its own: writing to a property with no setter yields `BadNotWritable` with none of our code involved.

**The status rule, unit level:**

| Test | Passes when |
|---|---|
| `WhenAnInboundValueIsUncertain_ThenItIsApplied` | the value reaches the property |
| `WhenAnInboundValueIsBad_ThenItIsNotApplied` | the property is untouched and the drop is counted |
| `WhenAnApplyThrows_ThenTheFailureIsLoggedAndTheNextValueIsStillApplied` | one bad property does not abort the rest |
| `WhenAConversionThrows_ThenTheRestOfTheNotificationIsStillApplied` | covers the subscription path's per-item conversion |

**Per path:**

| Test | Note |
|---|---|
| `WhenASubscriptionNotificationIsBad_ThenTheValueIsNotApplied` | the behaviour change; today it is applied |
| `WhenAPolledValueIsUncertain_ThenItIsApplied` | today silently dropped |
| `WhenAnInitialLoadValueIsUncertain_ThenItIsApplied` | today skipped |
| `WhenAnApplyThrowsDuringInitialLoad_ThenTheSourceStillReachesSynchronized` | today it never does |
| `WhenPropertiesAreSkippedDuringInitialLoad_ThenTheLogReportsTheAppliedCount` | today reports the requested count |

**Outbound, using SDK-native refusals:**

| Test | Passes when |
|---|---|
| `WhenAWriteKeepsFailing_ThenItIsDroppedAfterTheBound` | the change leaves the queue and the drop is logged |
| `WhenAWriteKeepsFailing_ThenSubsequentWritesAreStillDelivered` | the head-of-line test, and the reason this PR exists |
| `WhenAWriteFailsOnceAndThenSucceeds_ThenOrderingIsPreserved` | the deliberate blocking still blocks within the bound |

**Read-after-write ordering, its own commit:**

| Test | Passes when |
|---|---|
| `WhenALocalWriteLandsBeforeTheReadBack_ThenTheReadBackIsNotApplied` | the newer local value survives |
| `WhenNothingChangedSinceTheWrite_ThenTheReadBackIsApplied` | the guard does not over-trigger |
| `WhenTheRemoteTimestampIsAheadOfOurs_ThenLocalOrderingStillDecides` | pins the fix: a skewed remote clock no longer decides |

Conventions: `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange`, `// Act`, `// Assert`, `AsyncTestHelpers.WaitUntilAsync` rather than delays. The OPC UA suite binds a fixed port and cannot run concurrently with itself or the connector tester.

## Risks

- **Subscriptions stop applying Bad values.** Today they land in the model. Depending on that is depending on a value the server declared invalid, but it is a behaviour change and belongs in the release notes.
- **Polling starts applying Uncertain values**, which will look like new data appearing on a path where it previously vanished.
- **A write can now be dropped.** That is the intended trade against blocking every other write, which is why it is logged rather than silent. The bound's value needs choosing, and a too-low bound discards writes a slow server would have accepted.
- **A property that goes permanently Bad freezes at its last good value** with nothing to point an operator at, since quality exposure is out of scope. Defensible against the standard, but it is a silent mode.
