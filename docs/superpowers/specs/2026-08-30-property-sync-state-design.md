# Per-property divergence: material for a brainstorm

**Status: not a spec. Not implementable as written.** Three review rounds found roughly forty defects, and the last round invalidated the entry-point design. This file has been pruned to what survived verification, plus the questions that must be answered before a real spec exists. Everything unverified has been removed rather than left to mislead.

Prerequisite: the batch-continuation work (`2026-08-31-inbound-batch-continuation-design.md`), whose per-property catch in `ApplyPropertyUpdate` is where detection will attach.

## Goal

A consumer can ask, per property, whether the local model currently agrees with the source that owns it, and be told the moment it stops agreeing. This is the observability layer #342 defers to in its question 3. It builds no repair; #340 and #349 own that.

## The contract, verified and unchanged

A property has **diverged** when the model knows it holds a different value than its source, and knows nothing will fix it on its own.

**Knows.** Only detected disagreement is reported. The model cannot discover that a source changed a value behind its back, so this is not a consistency checker.

**Nothing will fix it.** Only terminal outcomes count. A write in flight, or parked in the retry queue while the transport is down, has not diverged; reporting it would flicker on every local write. Hence no `InFlight` state.

`Diverged` is a floor, not a ceiling. Its absence means nothing was detected, not that the two agree.

## Survived all three rounds: do not re-litigate

**Layering.** Divergence types belong in Connectors beside `SourceState`. `SetValueFromOrigin` stays in Tracking: it already calls `PendingOrigin.Set` at that site (`SubjectChangeContextExtensions.cs:49`), so it already depends on Core internals through the existing `InternalsVisibleTo`, and moving it would change its namespace and break every consumer's `using`.

**No refusal marker interface.** Connectors catches `ValidationException` directly. It is a shared-framework type and Connectors targets `net9.0`. Adding a marker later, if a second refusal mechanism appears, is non-breaking.

**`SourceState.Diverged`, property-level only**, like `Unclaimed`. Reported only when the owning source is `Synchronized`; any other source state dominates. This loses nothing for the decision the state serves, because `docs/connectors-monitoring.md:204` already defines `State` as *"can I trust these values"* and everything other than `Synchronized` means no. `TryGetDivergence()` answers *why* and stays readable regardless of source state, so an operator diagnosing an incident during a reconnect reads it directly. Document that the two can disagree.

**Source-level `Diverged` is impossible**, not merely undesirable: `SourceMonitor.cs:367` gates branch waits on `state == SourceState.Synchronized` exactly, so a source reporting `Diverged` would make every containing wait hang.

**The `PendingFrame` stamp mechanism works.** Confirmed by two independent reviewers after two others wrongly said it could not. `PendingOrigin.Set` copies the frame by value into the scope before installing a fresh one, so a nested stamped write captures the outer stamp and `Dispose` restores it intact. Invariants an implementer must not break:

1. `Set` must keep assigning a whole new `PendingFrame`, never mutating fields in place.
2. Scope capture stays by value and stays before the frame is overwritten; `Restore` writes the whole struct.
3. **The stamp write must be gated on a non-Local attempted origin. This is a correctness requirement, not a performance ordering.** Without it, a nested local write or a derived recalculation clobbers the outer stamp after the outer terminal wrote it.
4. The reader stays lexically inside the `using` block, before `Dispose`.
5. Every non-Local origin producer keeps going through `Set`.

**The stamp must be positive.** It records what happened, with absence meaning "not applied", never "applied faithfully". Inverting it made a transaction-captured write look like a landing and erased correct state; `SubjectTransactionInterceptor.cs:137` returns without calling `next`, and `PropertyValueEqualityCheckHandler.cs:16-19` does the same for a no-op, so neither reaches a terminal.

**Value-gate the clear.** `if (record is not null && SentValueEqualsCurrent()) Clear(record)`. The healthy path already exits on `record is null`, so this costs nothing and it fixes two exits the outcome enum cannot express: a post-commit `OnChanged` or listener throwing *after* a good write, and a listener rewriting the value out of band. The comparer must mirror the terminal's, including the boxed-enum unbox at `IWriteInterceptor.cs:280-289`.

**Skip derived at the detection site**, using `Metadata.IsDerived`, not via a special case in Core. A derived property's getter recomputes the stored value so a stamped origin never survives, making it indistinguishable from a transform. `SubjectUpdateFactory.cs:83` includes every property with a getter in a complete update, so a WebSocket welcome snapshot carries them and they would otherwise be permanently marked.

**Do not pin an `ISubjectSource` in the record.** `PropertyReference.cs:160-166` states the rule: keeping per-property data free of a source reference is what lets it need no release on detach. Use an identity token. Ownership changes underneath records via `SourceOwnershipManager` on release, detach and dispose, and a reconnect re-claims, possibly with a different instance.

**Servers are excluded.** A server has many clients, so a refused value is diverged with respect to one and fine for the others: a per-(property, client) matrix, a different data model rather than a larger version of this one. `OpcUaSubjectServer` and `MqttSubjectServer` derive from `SubjectConnectorBase`, never claim properties, and stamp a non-source (`MqttSubjectServer.cs:48` is a bare `new object()`).

**No ABBA cycle and no consumer callback under `SyncRoot`.** Verified in both rounds. `SourceMonitor.Publish` only enqueues.

## Open questions, to answer before writing a spec

### 1. What makes a record stale?

Every round found defects here. The revision approach is broken in ways that are documented below and should not be retried unchanged. A value-based rule may avoid the machinery entirely but has not been checked. Constraints any answer must satisfy:

- A record written by a losing race must not permanently mark a converged property.
- A clear leaves no tombstone, so a late record wins against nothing.
- On the exception path no terminal ran, so there is no producing revision at all.
- Ownership can change between writing a record and reading it.

### 2. Where does detection live?

The applier does **not** call `SetValueFromSource`. It calls `SetValueFromOrigin` with a `ChangeOrigin` whose `Source` is `object` (`SubjectUpdateApplyContext.cs:61`), and WebSocket applies everything through the applier. So an `ISubjectSource`-typed entry point cannot intercept it and WebSocket would get no detection at all.

Direction that looks workable but is unverified: two detection sites in Connectors sharing an internal helper, one at `RegisteredSubjectPropertyExtensions.SetValueFromSource` and one in the applier, each doing a runtime `Origin.Source is ISubjectSource` test. The applier's site is the catch block the batch-continuation work creates, which already has the property and the exception in hand.

### 3. Is outbound in scope at all?

Three of round two's ten findings were outbound-only, and it needs a second invalidation model because a later local commit does not mean the source received anything. Including it roughly doubles the surface. Decide deliberately rather than by default.

## Falsified: do not repeat these claims

- **Call sites.** Not twelve, not five. **Three** actually change binding. Extension binding is decided by the receiver's static type, not by namespace imports; several OPC UA sites already bind to the Connectors overload because they hold a `RegisteredSubjectProperty`.
- **Cycle-boundary clearing in `SubjectPropertyWriter.StartBuffering`** is unimplementable. The ownership manager is `internal` to each connector assembly and invisible from Connectors, the set is empty at the first call, it fires on retries when nothing reconnected, and it misses two of three OPC UA apply paths.
- **The server exclusion is not a compile-time fact**, because the applier is shared by client and server.
- **`AspNetCore` does reference Validation**, so it is not a precedent for using `ValidationException` without that reference. The substantive claim still holds for other reasons.
- **MQTT never re-applies a snapshot**: `LoadInitialStateAsync` returns `null`, so reconnect clears nothing.
- **`WriteRetryQueue`'s buffering-disabled drop site is dead code**; production always passes `maxQueueSize > 0`. The live drops are at `SubjectSourceBase.cs:366, :381, :613, :625`.
- **`TryGetWriteState` returns the property's last commit from any thread**, not this write's, so it cannot supply a producing revision.
- **Outbound changes routinely carry `Revision == 0`**, because `CollapsePerProperty` and `ChangeMerger` call `WithoutRevision()`.
- **`PropertyReference` exposes no compare-and-replace**, so "never overwrite a newer record" cannot be written atomically today.

## Known hole with no answer yet

The generated `OnXChanging` hook runs between `PendingOrigin.Set` and `TryConsume` (`SubjectCodeGenerator.cs:371`). A hook that clamps by re-entering the same property makes the inner write consume the outer's armed stamp, and nothing restores it, latching a false `Diverged` on a property holding exactly what the source sent. That is the clamping pattern, which is the case the transform detection exists to catch. Either close it, by requiring `HasValue == false` before trusting the outcome, or state it as an accepted limitation.

## Known verification requirements

- Benchmark gate covering the inbound path and, if outbound is included, the outbound batch path.
- Connector suites locally: OPC UA, MQTT, WebSocket. CI path filters skip them for shared-library changes.
- Both public API snapshots, `Tracking.Tests` and `Connectors.Tests`.

## Documentation this will need

Drafted during design and kept here because the "not covered" list is the part that stops the feature being misread. `docs/connectors-monitoring.md` needs four edits and one new section.

**Edit 1, "Reading Per-Property State".** Its opening sentence says `GetSourceState()` is "derived from its owning source with no per-property storage". That stops being true and must be reworded. Append the `TryGetDivergence()` example and the rule that `Diverged` describes one property's relationship with its source rather than the source itself.

**Edit 2, "The State Model".** Add `Diverged` to the enum block, and change "On a source itself, `Unclaimed` never occurs" to name both property-only members.

**Edit 3, "The Event Stream" table.** Add a `PropertyStateChanged` row with `Property` set.

**Edit 4, "Diagnostics and State answer different questions".** Note that `Diverged` sits on the `State` side even though a dropped outbound write can cause it, because it answers "can I trust this value" rather than "what is the transport doing". The distinction from outbound backlog is the same one already drawn there: a queued write is expected to arrive and says nothing, a dropped one never will.

**New section, "What Diverged Does Not Cover".** The framing matters more than the list:

> `Diverged` is a floor, not a ceiling. It reports disagreement the framework itself detected. Its absence means nothing was detected, not that the model and the source agree.

Detected and reported:

| Direction | Case |
|---|---|
| `Inbound` | A value the source sent that the model refused or failed to apply, including a validator rejecting it and a property setter throwing. |
| `Inbound` | A value an `OnChanging` hook transformed on the way in, so what the model stored is not what the source sent. |
| `Outbound` | A local write dropped from the retry queue, if the outbound direction is in scope at all. |

Not reported:

| Case | Why, and where it is tracked |
|---|---|
| A source changing a value without telling us | No read-back or periodic compare while connected, so nothing detects it. #342 question 4. |
| A write the source rejects permanently | The retry queue keeps retrying rather than giving up, so the change is never dropped and never marked, although it never lands either. #342 row 3. |
| A transaction commit whose source write fails and whose revert also fails | Repair designed but not built. #340. |
| A write landing inside a transaction commit window | Silently overwritten, documented best effort. #338. |
| An inbound update naming a property the model does not have | No property exists to mark. |
| A value sent for a property with no setter | Deferred: a WebSocket welcome snapshot carries read-only and derived properties, so marking would report `Diverged` permanently in the default configuration. |
| A property no source has claimed | Divergence is defined against an owning source. Reports `Unclaimed`. |

One blind spot in reporting rather than detection: while the owning source is not `Synchronized`, its own state is reported instead, so a diverged property reads `Synchronizing` for the duration of a reconnect. The record survives and `TryGetDivergence()` still returns it, so a consumer wanting divergence during an outage reads that directly rather than going through `GetSourceState()`.

Also needing edits outside that file: `docs/connectors.md`'s "Inbound Update Error Handling" should point at `Diverged` for how a dropped update becomes observable, and `docs/validation.md` should note that a rejected inbound value is now reported rather than only logged.
