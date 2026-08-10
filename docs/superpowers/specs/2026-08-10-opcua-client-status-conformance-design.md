# OPC UA client: status code conformance

**Goal:** the client treats a `DataValue`'s status the way the standard expects, identically on every inbound path, and a permanently refused write stops blocking every other write.

**Shape:** first of two stacked PRs. This one stands alone and is independently valuable.

**Base:** master at `f561d196`.

## Why this is not just scaffolding

`UncertainLastUsableValue`, `UncertainSensorNotAccurate` and `UncertainSubstituteValue` are routine in industrial servers: a sensor fault, a value held over from a failed poll, a substituted value on a bad input. Refusing a write with a bad status is equally routine. So the behaviour below is already wrong against the installed base, today, with no change to our own server.

It is also the prerequisite for the server work. Once our server returns those statuses, a topology with this library at both ends hits the write stall described below.

## The defect

### Inbound: four paths, four behaviours

| Path | Good | Uncertain | Bad |
|---|---|---|---|
| Subscription (`Client/Connection/SubscriptionManager.cs:200-207`) | apply | apply | **apply** |
| Polling (`Client/Polling/PollingManager.cs:364-374`) | apply | **dropped, no log, no metric** | log + metric |
| Initial and reconnect load (`Client/OpcUaSubjectClientSource.cs:237`) | apply | skipped silently | skipped silently |
| Read-after-write (`Client/ReadAfterWrite/ReadAfterWriteManager.cs:323-326`) | apply | skipped silently | skipped silently |

The subscription path never inspects the status at all, so it applies **Bad** values. A server may omit the value entirely for a bad status, so a sensor fault can write a null or a default into the model as though it were a reading.

Two further problems in the same code:

- The initial load logs `"Successfully read {Count} OPC UA nodes"` using the requested count (`OpcUaSubjectClientSource.cs:245`), so it reports success for properties it skipped.
- Two of the four paths do not wrap the apply. `OpcUaSubjectClientSource.cs:247-251` iterates every property converting and applying with no `try`, so **one property whose apply throws aborts the entire initial state load** and leaves everything after it in the iteration uninitialized. `ReadAfterWriteManager.cs:340` has the same shape. Subscriptions and polling both wrap.

### Outbound: the classification exists, nothing acts on it

`OpcUaStatusCodeClassifier` already sorts permanent from transient, and says so in its own documentation: *"The write path currently uses it for diagnostics only... so permanently-failed writes are still requeued (#332)."*

The consequence is a stall, not a lost write:

1. `OutboundWriter.ProcessWriteResults` reports every non-Good result as a failed change (`Client/OutboundWriter.cs:76-116`).
2. `SubjectSourceBase.WriteChangesViaRetryQueueAsync` enqueues them (`Connectors/SubjectSourceBase.cs:303-306`).
3. Every later batch first flushes the retry queue, which requeues and returns `false` on any error (`Connectors/WriteRetryQueue.cs:153-172`).
4. On `false` the caller **enqueues the new changes instead of writing them** (`SubjectSourceBase.cs:290-296`).

So one property the server will never accept blocks all outbound writes from that client, indefinitely.

## Design

### One rule for an inbound value

| Status | Action | Rationale |
|---|---|---|
| Good | apply | unchanged |
| Uncertain | apply, count | the standard's meaning is usable but of degraded quality; discarding it loses data the server was willing to give |
| Bad | do not apply, log, count | the value is not usable and may not even be present |

### One shared apply

All four paths run the same sequence: convert with `ConvertToPropertyValue`, call `SetValueFromSource`, log. Four divergent copies collapse into one helper that also owns the status decision and the exception handling, which is what makes the four paths agree by construction rather than by discipline.

What stays per-site, because it genuinely differs: the timestamp source, the metrics recorded, read-after-write's staleness check against `TryGetWriteTimestamp`, and the initial load's deferral through a returned closure.

Placing the status rule on `OpcUaStatusCodeClassifier` extends a type that already exists to be the single shared answer for status questions, rather than introducing a second concept.

### Outbound disposition

`WriteResult` gains a way to express which of the failed changes failed permanently. `WriteRetryQueue` then drops those with a diagnostic and a counter instead of requeueing them, so they stop blocking.

Transient failures keep blocking, deliberately. That blocking is what preserves write ordering, and removing it would let newer changes overtake queued ones.

### Public API

`WriteResult` is a public readonly struct and appears on `ISubjectSource.WriteChangesAsync`, so this reaches every custom source. The change is **additive**: a new factory overload plus a property that defaults to "none permanent", which is exactly today's behaviour. Existing implementations compile unchanged and behave unchanged; only the OPC UA client opts in. The public API snapshot moves and is re-accepted.

## Explicitly out of scope

- Exposing value quality in diagnostics or on the model. That is [#299](https://github.com/RicoSuter/Namotion.Interceptor/issues/299) and it is a feature.
- Any change to the OPC UA server. That is the stacked PR.
- Changing the ordering guarantee for transient failures.

## Acceptance criteria

1. **All four inbound paths agree** on Good, Uncertain and Bad.
2. **No new problems.** A Good value behaves exactly as it does today on every path.
3. **Existing custom sources are unaffected**, compile and behave unchanged.
4. **A permanently refused write does not block other writes**, and is visible when dropped.
5. **Net simplification.** Four copies of the convert-and-apply sequence become one. This PR is expected to remove more code than it adds, and the number is reported rather than predicted.

## Test plan

Written red first. One testability note shapes the approach: our own server does not emit Uncertain until the stacked PR, so the inbound rule is tested at the unit level and the outbound rule uses refusals the SDK produces without any of our code.

**The status rule, unit level, exhaustive over Good, Uncertain and Bad:**

| Test | Passes when |
|---|---|
| `WhenAnInboundValueIsUncertain_ThenItIsApplied` | the value reaches the property |
| `WhenAnInboundValueIsBad_ThenItIsNotApplied` | the property is untouched and the drop is counted |
| `WhenAnApplyThrows_ThenTheFailureIsLoggedAndTheNextValueIsStillApplied` | one bad property does not abort the rest |

**Per path, that the shared rule is actually reached:**

| Test | Path |
|---|---|
| `WhenASubscriptionNotificationIsBad_ThenTheValueIsNotApplied` | the behaviour change, since today it is applied |
| `WhenAPolledValueIsUncertain_ThenItIsApplied` | today silently dropped |
| `WhenAnInitialLoadValueIsUncertain_ThenItIsApplied` | today skipped |
| `WhenOnePropertyFailsDuringInitialLoad_ThenTheRemainingPropertiesAreStillApplied` | today aborts the load |
| `WhenPropertiesAreSkippedDuringInitialLoad_ThenTheLogReportsTheAppliedCount` | today reports the requested count |

**Outbound, using SDK-native refusals.** Writing to a property with no setter yields `BadNotWritable` from the SDK with none of our code involved, which makes this testable today:

| Test | Passes when |
|---|---|
| `WhenAWriteFailsPermanently_ThenItIsDroppedWithADiagnostic` | the change leaves the queue and the drop is counted |
| `WhenAWriteFailsPermanently_ThenSubsequentWritesAreStillDelivered` | the head-of-line test, and the reason this PR exists |
| `WhenAWriteFailsTransiently_ThenItIsRequeuedAndOrderingIsPreserved` | the deliberate blocking still blocks |
| `WhenASourceReportsNoPermanentFailures_ThenBehaviourIsUnchanged` | pins the additive API default |

Conventions: `When<Condition>_Then<ExpectedBehavior>` naming, explicit `// Arrange`, `// Act`, `// Assert`, `AsyncTestHelpers.WaitUntilAsync` rather than delays.

The OPC UA suite binds a fixed port and cannot run concurrently with itself or with the connector tester.

## Risks

- **Subscriptions stop applying Bad values.** Today they land in the model. Anything depending on that is depending on a value the server declared invalid, but it is a behaviour change and belongs in the release notes.
- **Polling starts applying Uncertain values.** Values will begin arriving on paths where they previously vanished. That is the fix, and it will look like new data to anyone watching.
- **A permanently failed write can now be lost.** That is the intended trade against blocking every other write, which is why it must be counted and logged rather than silently dropped.
- **`WriteResult` is public.** Additive, but the surface moves and consumers will see it.
