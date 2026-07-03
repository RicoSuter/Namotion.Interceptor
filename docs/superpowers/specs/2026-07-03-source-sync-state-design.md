# Source Synchronization State and Source Event Stream

Date: 2026-07-03
Status: Approved design (revised after design review), pending implementation plan

## Problem

Consumers of a subject tree cannot tell whether a property value is actually in sync with its external source. At application start the tree is offline. Sources connect asynchronously, claim their properties, load the initial state, and only then start applying live updates. During that window a property shows a local default value that was never confirmed by the external system, and after a connection loss it silently shows a stale last-known-good value.

The existing API is insufficient:

- `property.TryGetSource()` is polling based and only returns the owning source, not whether that source is synchronizing.
- At startup, no source has claimed anything yet, so "no source" is ambiguous: it can mean "no source attached yet" or "this property will never have a source".
- Which properties a source will claim is not predictable up front (a source may claim only part of the branch it is configured for), so waiting cannot be expressed in terms of properties alone.

There is also a structural asymmetry: property values have a full traceability pipeline (interceptors, change queue, observables, timestamps, source attribution), while source metadata has none. Claims, releases, and connection state are silent writes into per-property attached data. Everything built on top of them is forced to poll.

Two consumer groups need this:

1. Waiting consumers that must block until the tree is live (for example, automation logic that must not run on default values).
2. Diagnostics that want to observe sync state per source and per property, including claim and release activity.

## Goals

- A source-level state with well-defined transitions, driven by the existing pump lifecycle in `SubjectSourceBase`.
- A single typed event stream for all source metadata: source registration, state transitions, property claims, and property releases.
- An awaitable primitive: wait until all known sources have completed their initial synchronization.
- A cheap per-property state derived from source ownership, with no per-property storage.
- Works for both DI-registered (hosted service) sources and dynamically created sources, with no new DI registrations.

## Non-Goals

- Outbound write confirmation. `Synchronized` describes the inbound direction only (the model mirrors the external system). Pending outbound writes remain visible through `PendingWriteCount` on the source; the write retry queue can be non-empty during regular synchronized operation and must not affect the state.
- A per-property observable `@syncState` registry attribute. Per-property observability falls out of filtering the event stream instead, with no per-property storage, no intercepted writes, and no wire leakage.
- A scope or intent query (`IsPropertyInScope`) that would answer "will this property ever be claimed" before connecting. Deferred as future work; actual claims are connection-dependent, and the diagnostics value did not justify the API surface for now.
- Timeout policy. A permanently unreachable device keeps its source in `Connecting` forever and the wait never completes. That is semantically honest; callers bound their patience with the `CancellationToken`.

## Design

### SourceState enum

```csharp
public enum SourceState
{
    NoSource,      // only returned by the property-level API: no source has claimed the property
    Connecting,    // registered or claimed, but subscribe-read-replay is not complete; also after a detected connection loss
    Synchronized,  // initial load complete, live updates flowing
    Stopped        // source shut down (host stop or dynamic removal); final unless the source is started again
}
```

One enum is shared by the source-level and property-level APIs. A source itself is never `NoSource`; that value exists so the property-level API returns one coherent type. There is no separate `Disconnected` state: after a failure the pump loops straight back into its connect-and-load phase, so a disconnected source is a connecting source. The lost diagnostic distinction ("never synchronized" versus "was synchronized, now stale") is recovered through `LastSynchronizedAt`.

### ISubjectSource additions

`ISubjectSource` (in `Namotion.Interceptor.Connectors`) gains four members:

```csharp
SourceState State { get; }
DateTimeOffset? LastSynchronizedAt { get; }
int PendingWriteCount { get; }
event EventHandler<SourceEvent>? StateChanged;
```

- `LastSynchronizedAt` is the completion time of the most recent initial synchronization; `null` means never synchronized. While `Connecting` after a drop, it tells a dashboard "stale, last confirmed at T".
- `PendingWriteCount` moves up from `SubjectSourceBase` so a diagnostics view can render a complete dashboard row (`State`, `LastSynchronizedAt`, `PendingWriteCount`) from the `ISubjectSource` surface alone, without downcasting. It remains the orthogonal outbound signal next to the inbound `State`.
- `StateChanged` is the notification primitive: the source raises it itself whenever `State` changes, with the same `SourceEvent` payload used on the stream (no separate event args type). Raising the event is part of the interface contract and is the idiomatic thing implementers get right; sources never report state to the tracker directly.
- Consumers pick by shape: aggregate consumers (waits, dashboards) use the tracker stream below; a consumer holding a specific source reference (for example an application device wrapper) subscribes to that source's `StateChanged` directly, with no enumeration race and no stream filtering.

This is a breaking change for custom `ISubjectSource` implementers. That surface is deliberately low-level and almost internal: nearly all implementations derive from `SubjectSourceBase`, which provides all of these members, so the built-in connectors (OPC UA, MQTT, WebSocket) inherit them without changes. The docs recommend deriving from `SubjectSourceBase`.

### State transitions in SubjectSourceBase

Transitions happen on the pump path plus one disposal fallback:

- Construction: the state field starts as `Connecting`. Nothing can observe the source before registration, so no event is involved.
- Pump entry (start of `ExecuteAsync`): transition to `Connecting`. A no-op on first start; on a restarted source this is the real `Stopped` to `Connecting` transition (`BackgroundService` supports stop/start cycles).
- After `LoadInitialStateAndResumeAsync` completes: `Synchronized`. This is deliberately before `ReapplyRetryQueue`, because the retry queue reflects the outbound direction and can be non-empty at any time during regular operation.
- In the pump's catch block (failure detected): `Connecting`.
- On pump exit (cancellation or shutdown), in a finally block: `Stopped`.
- In `Dispose`, as a fallback: if the state is not already `Stopped`, transition to `Stopped` and raise the event before unregistering. This makes dispose-without-stop safe (the final `Stopped` still reaches the stream while the source is registered). Stopping before disposing remains the graceful path, because a hard-disposed pump cannot flush pending writes.

Implementation notes:

- The state is stored in an `int` field (the enum's underlying value) so it works with `Volatile.Read` and `Interlocked.CompareExchange`. All transitions go through a compare-exchange helper with two rules: a transition to the current state is a no-op (no duplicate events), and `Stopped` is sticky, meaning only the pump-entry transition (restart) may leave it. Stickiness makes the disposal fallback race-free against a dying pump: a late catch-block `Connecting` or an in-flight `Synchronized` cannot overwrite `Stopped`.
- `LastSynchronizedAt` is stored as UTC ticks in a `long` field (`0` means never synchronized), written with `Interlocked.Exchange` immediately before the `Synchronized` transition and read with `Interlocked.Read`, materialized to `DateTimeOffset?` in the property getter. This is the same pattern as `PropertyReference.SetWriteTimestamp`; a `DateTimeOffset?` field would tear under concurrent access.
- Each transition raises the source's own `StateChanged` event after the field is updated, from the thread performing the transition (normally the pump, the disposing thread for the fallback). The raise helper wraps handler invocation in try/catch with logging: a throwing subscriber must not be treated as a pump failure (otherwise a buggy `Synchronized` handler would flip the source back to `Connecting` in an endless loop).

### The source event stream

All source metadata changes are published as one typed event on a per-tree stream:

```csharp
public enum SourceEventKind
{
    SourceRegistered,
    SourceUnregistered,
    StateChanged,
    PropertyClaimed,
    PropertyReleased
}

public readonly record struct SourceEvent(
    SourceEventKind Kind,
    ISubjectSource Source,
    PropertyReference? Property,   // set for PropertyClaimed/PropertyReleased, otherwise null
    SourceState OldState,          // meaningful for StateChanged; otherwise the source's current state
    SourceState NewState,
    DateTimeOffset Timestamp);
```

Delivery rules:

- Events from a single source are delivered in order (the pump is single-threaded); claim events from concurrent connect activity of different sources may interleave.
- Handlers must be fast and non-blocking, the same rule as for tracking observables. Claim bursts are real: an OPC UA browse can claim thousands of properties in one connect cycle. Diagnostics UIs throttle or sample on their side.
- A throwing handler is caught and logged; it must not break the claim path or the pump (a buggy handler must not flip a source back to `Connecting` in an endless loop).

### Emission points

Each event kind is emitted at the lowest level that knows the truth:

- **`PropertyClaimed` / `PropertyReleased`: emitted by `SetSource` and `RemoveSource` themselves**, not by `SourceOwnershipManager`. The documented contract tells sources to claim by calling `SetSource(this)` directly, and the low-level ownership API is public, so emission at the primitive is the only way the stream stays trustworthy. Emission happens only on actual ownership transitions: an idempotent re-claim by the same source and a rejected claim (property owned by a different source) emit nothing, and `RemoveSource` emits only when it actually removed the ownership entry. The internal compare-and-set is adjusted to distinguish "newly added" from "already present".
- The tracker is resolved via `property.Subject.Context` (service resolution walks fallback contexts). If no tracker service exists, emission is a null check and a no-op, so plain contexts and benchmarks pay nothing.
- **`StateChanged`: raised by the source itself** (the pump in `SubjectSourceBase`). The tracker, subscribed since registration, forwards each event to the stream.
- **`SourceRegistered` / `SourceUnregistered`: emitted by the tracker** in `Register`/`Unregister` (called from `SubjectSourceBase.StartAsync` and dispose).
- `SourceOwnershipManager` has no eventing role. It remains a convenience for claimed-set bookkeeping and automatic release on subject detach; its releases go through `RemoveSource` and therefore appear on the stream like any other.

### SourceStateTracker as an auto-registered context service

The tracker lives in the `IInterceptorSubjectContext`, not in DI. `SubjectSourceBase` overrides `StartAsync`: it idempotently adds the tracker to its context via `TryAddService`, registers itself, and only then calls `base.StartAsync` to launch the pump. On `Dispose` the source unregisters. Because both DI-hosted and dynamically created sources start through the same hosted-service lifecycle, registration works for both, and each subject tree gets its own tracker with natural scoping.

Registering at start instead of at construction has three consequences:

- `SourceRegistered` always publishes a fully constructed source. No handler or `Sources` snapshot reader can observe a source whose derived constructor has not finished.
- A source that is constructed but never started stays invisible. It can never synchronize, so it must not hold `WaitForAllSourcesSynchronizedAsync` open.
- `Register` is idempotent (a re-register is a no-op that emits nothing), which makes stop/start cycles safe.

Custom `ISubjectSource` implementations that do not derive from `SubjectSourceBase` register themselves once fully constructed and started, and unregister on dispose.

```csharp
public class SourceStateTracker
{
    IReadOnlyList<ISubjectSource> Sources { get; }        // snapshot
    IDisposable Subscribe(Action<SourceEvent> handler);   // typed event stream

    void Register(ISubjectSource source);                 // idempotent
    void Unregister(ISubjectSource source);

    Task WaitForAllSourcesSynchronizedAsync(CancellationToken cancellationToken = default);
    Task WaitForSourcesSynchronizedAsync(Func<ISubjectSource, bool> filter, CancellationToken cancellationToken = default);
}
```

On `Register` the tracker subscribes to the source's `StateChanged` event and forwards its events to the stream; on `Unregister` it unsubscribes. Registration precedes the pump start, so `SourceRegistered` precedes any forwarded `StateChanged` of that source. Subscription and registration are serialized by the tracker, so the documented consumption pattern is race-free: subscribe first, then read the `Sources` snapshot; every change after the snapshot arrives as an event. The wait methods are internal stateful consumers of registration, unregistration, and state-change notifications, tracked separately from public stream subscribers (see the catch-up cost guard below).

### View catch-up on subject attach and detach

The stream's contract is "the tree's view of ownership", not "every `SetSource` call anywhere". Two lifecycle situations would otherwise punch holes in that contract: a property claimed while its subject was not yet attached to the tree (no tracker reachable, nothing emitted) whose subject is attached later, and a claimed subject detaching from the tree without its claims being released (possible for sources that bypass `SourceOwnershipManager`).

The tracker therefore hooks the existing lifecycle infrastructure (which connector contexts already require; `SourceOwnershipManager` throws without `WithLifecycle()`):

- **On subject attach**: scan the subject's properties and emit `PropertyClaimed` for any that arrive already claimed; they just entered the view.
- **On subject detach**: emit `PropertyReleased` for any still claimed; they left the view. Releases performed by `SourceOwnershipManager` during detach go through `RemoveSource` and are real events; the scan then finds nothing, so there are no duplicates.

Cost guard: when the stream has no public subscribers, the scan is skipped entirely, so the recently optimized attach/detach hot paths pay one subscriber-count check and nothing else. Pending waits deliberately do not count as subscribers: the wait methods consume tracker notifications through internal bookkeeping rather than a stream subscription, because a wait is typically active during startup, exactly when subject attach storms happen, and waits never need property claim events. Built-in connectors always claim after attach, so the missed-claim window is theoretical today; the catch-up exists to give the stream a consistency contract subscribers can rely on without reasoning about holes.

### Wait semantics

The wait completes when at least one matching source is currently registered and every currently registered, non-`Stopped` matching source is `Synchronized`. If the only registered matching sources are `Stopped`, the condition is vacuously met and the wait completes.

- Implementation is a re-evaluation over a `TaskCompletionSource` (created with `RunContinuationsAsynchronously`), triggered by registration, unregistration, and state change notifications.
- A source registered while someone is waiting is included; a source that drops back to `Connecting` before the condition is met re-blocks the wait.
- Once the returned task completes it stays completed. Callers who care about later disconnects subscribe to the stream or call the wait again.
- An empty set does not complete the wait. This is deliberate: it converts the startup race (waiter runs before sources are started) into correct blocking behavior, because mid-wait registrations are picked up. The cost is that waiting on a tree that never gets any source blocks until cancellation; this is documented.
- The filter overload covers subtree scoping (for example, only sources whose `RootSubject` is under a given subject). A filter that never matches blocks until cancellation.

### Context extension methods and the startup race

Waiting is exposed as extensions on `IInterceptorSubjectContext`:

```csharp
Task WaitForAllSourcesSynchronizedAsync(this IInterceptorSubjectContext context, CancellationToken cancellationToken = default);
Task WaitForSourcesSynchronizedAsync(this IInterceptorSubjectContext context, Func<ISubjectSource, bool> filter, CancellationToken cancellationToken = default);
```

If no tracker service exists yet, the extensions idempotently create it via `TryAddService`, so waiting before any source exists follows the empty-set rule above (block until the first source registers).

The startup race for hosted waiters is closed by the hosting model itself. Sources register during host startup, when the host calls their `StartAsync` sequentially. A waiter that runs before host startup completes could observe a partially registered set: if an early source synchronizes before a later source is started, the wait could complete prematurely. The built-in guarantee against this is `IHostApplicationLifetime.ApplicationStarted`: once it fires, every DI-registered source has been started and is therefore registered. Hosted waiters await `ApplicationStarted` first (a few lines with a `TaskCompletionSource` registered on the token); code that runs after the host has started needs nothing. Even without the guard the window is small, because a source would have to complete its network connect and initial load faster than the host finishes starting the remaining services; the guard makes premature completion impossible rather than unlikely.

Dynamic sources that do not exist yet at call time cannot be known by any mechanism. Applications that create sources dynamically (for example HomeBlaze device loading) coordinate at the application level: finish starting sources, then wait, or rely on mid-wait registration pickup.

### Per-property API

A single read-only extension on `PropertyReference` in `Namotion.Interceptor.Connectors`:

```csharp
public static SourceState GetSourceState(this PropertyReference property)
```

Derivation: `TryGetSource` returns nothing gives `NoSource`; otherwise the owning source's `State`. Computed on read from existing attached data, with no storage and no allocations. For richer diagnostics (`LastSynchronizedAt`, `PendingWriteCount`) the caller uses `TryGetSource` and reads the source directly.

The result is only fully meaningful after phase 1 of the consumer protocol below: claiming happens when a source connects, so before that point `NoSource` is ambiguous ("not claimed yet" versus "never claimed"). Once a property is claimed, `GetSourceState()` returns `Connecting` instead of `NoSource`, so even before phase 1 completes a diagnostics view can distinguish "will sync, still loading" from "no source, so far".

Per-property change observation needs no dedicated API: a property's effective state changes exactly when it is claimed or released (a `PropertyClaimed`/`PropertyReleased` event for that property) or when its owning source transitions (a `StateChanged` event for that source). Both are on the stream; consumers filter.

### Consumer protocol (canonical pattern)

```csharp
// Phase 0 (hosted waiters only): ensure host startup finished, so every
// DI-registered source has been started and is registered.
// (IHostApplicationLifetime.ApplicationStarted awaited via TaskCompletionSource.)
await applicationStartedTask;

// Phase 1: wait until claiming is over and every source finished subscribe-read-replay
await context.WaitForAllSourcesSynchronizedAsync(cancellationToken);

// Phase 2 is now instant and well-defined per property:
// Synchronized = live value, NoSource = local-only property that was never claimed
```

Phase 2 is not a second wait. Its value is that per-property answers only become well-defined after phase 1: from that moment, `NoSource` genuinely means "local-only". This resolves the problem that it is not predictable which properties a source will claim.

Consumers interested in a single property use the same protocol: wait once, then read `property.GetSourceState()`.

### Diagnostics

- A dashboard subscribes to the stream once and reads the `Sources` snapshot: source list with `State`, `LastSynchronizedAt`, and `PendingWriteCount` (all on the `ISubjectSource` surface), claim/release activity as it happens, and per-property state by filtering events for the properties on screen.
- Notification granularity is proportionate: state transitions are per source (all properties of a source flip together), claims and releases are per property but occur only on connect cycles and topology changes.

### Edge cases

- A property claimed while its source is still loading reports `Connecting`.
- A stopped source that still owns properties reports `Stopped` per property, meaning "will not synchronize again while stopped".
- Ownership release on subject detach (existing `SourceOwnershipManager` behavior) reverts properties to `NoSource` and appears on the stream as `PropertyReleased`.
- Disposing a source without stopping it first still publishes `Stopped`: the disposal fallback transitions and raises before unregistering, so the stream sees `StateChanged` to `Stopped` followed by `SourceUnregistered`.
- A stopped source that is started again re-registers idempotently (no duplicate `SourceRegistered`) and transitions `Stopped` to `Connecting` at pump entry.
- A source that is constructed but never started is not registered: it does not appear in `Sources` and does not affect the wait.
- Claiming a property of a subject that is not attached to any tree with a tracker emits no event at claim time (the tracker resolution finds no service); the claim enters the stream via attach catch-up when the subject joins a tree. This is documented on `SetSource`.

## Affected Code

- `Namotion.Interceptor.Connectors`: `SourceState`, `SourceEventKind`, `SourceEvent`, `ISubjectSource` members (`State`, `LastSynchronizedAt`, `PendingWriteCount`, `StateChanged`), `SubjectSourceBase` transitions (pump entry, catch, finally, disposal fallback) and `StartAsync` registration, `SourceStateTracker` (including the lifecycle attach/detach catch-up and internal wait bookkeeping), context wait extensions, `GetSourceState` property extension, emission logic in `SetSource`/`RemoveSource` (including the added-versus-existing distinction in the compare-and-set).
- Built-in connectors (OPC UA, MQTT, WebSocket): no source changes expected; they inherit from `SubjectSourceBase` and claim through the existing paths.
- Public API snapshot tests: `ISubjectSource` changes will fail `VerifyChecksTests.PublicApi` in affected projects; the new `.verified.txt` snapshots are accepted as part of the change.
- `docs/connectors.md`: new "Source state and events" section covering the state model, the stream, the consumer protocol (including the hosted-waiter `ApplicationStarted` guidance), and the breaking-change note for custom `ISubjectSource` implementers.

## Testing

In `Namotion.Interceptor.Connectors.Tests`, following the `When<Condition>_Then<ExpectedBehavior>` naming and event-based synchronization (no hardcoded waits):

- Pump transitions via a fake source: `Connecting` initially; `Synchronized` after initial load; back to `Connecting` on pump failure; `Stopped` on cancellation; `LastSynchronizedAt` set exactly on entering `Synchronized` and preserved through a later disconnect; a restarted source transitions `Stopped` to `Connecting` at pump entry and can synchronize again.
- Sticky `Stopped`: after the disposal fallback set `Stopped`, a late pump transition (catch-path `Connecting` or in-flight `Synchronized`) neither overwrites the state nor emits an event.
- Registration lifecycle: `StartAsync` registers the source and `SourceRegistered` precedes any `StateChanged` of that source on the stream; `Register` is idempotent (a stop/start cycle emits no duplicate `SourceRegistered`); a constructed but never started source is not registered and does not affect the wait; disposing without stopping emits `Stopped` before `SourceUnregistered`; after unregistration the tracker no longer forwards the source's events.
- Attach/detach catch-up: attaching a subject with already-claimed properties emits `PropertyClaimed` for each; detaching a subject with still-claimed properties emits `PropertyReleased`; releases performed by `SourceOwnershipManager` during detach produce no duplicate events; without public stream subscribers the catch-up scan is skipped; a pending wait alone does not trigger the scan.
- Claim/release emission: a fresh claim emits `PropertyClaimed`; an idempotent re-claim by the same source emits nothing; a rejected claim (owned by another source) emits nothing; `RemoveSource` emits `PropertyReleased` only on actual removal; claims without a tracker in the context emit nothing and do not throw.
- Handler robustness: a throwing subscriber is logged and breaks neither the claim path nor the pump.
- Subscription consistency: subscribe-then-snapshot observes every subsequent change exactly once.
- Wait semantics: completes only when all non-`Stopped` sources are `Synchronized`; empty set blocks; a source registered mid-wait is included and re-blocks the wait when `Connecting`; filter overload scopes correctly; cancellation propagates.
- Per-property: `NoSource` for unclaimed, `Connecting` for claimed while loading, `Synchronized` for claimed after load, `Stopped` for a claimed property of a stopped source.

## Alternatives Considered

- Observable `@syncState` dynamic registry attribute per claimed property: fully per-property observable through existing change tracking, but O(claimed properties) writes and allocations per transition, wire noise into other connectors, and attribute lifetime coupled to claim/release. Rejected; filtering the event stream provides per-property observability without any of these costs.
- Per-source events as the sole mechanism (no aggregated stream): forces aggregate consumers into per-source bookkeeping with an inherent race between enumerating sources and subscribing to each one. The final design keeps the per-source `StateChanged` event as the notification primitive feeding the tracker, while the stream is the aggregate consumption surface with snapshot-plus-subscribe consistency.
- State reporting through tracker calls instead of a source-raised event: a custom source that registers but forgets to report its transitions would hang `WaitForAllSourcesSynchronizedAsync` silently, and "call the tracker service resolved from your context" is unusual choreography compared to the idiomatic "raise your event when your state changes". Rejected in favor of the event primitive.
- Constructor-time tracker registration (register in the `SubjectSourceBase` constructor): the base constructor runs before the derived constructor, so `SourceRegistered` and the `Sources` snapshot would publish a partially constructed source (`RootSubject` and other derived members not yet assigned, a `NullReferenceException` trap for handlers), and a source that is constructed but never started would block the wait forever. Rejected; `StartAsync` is the natural fully-constructed hook and gives "only sources that can ever synchronize participate" semantics.
- Claim-derived registration (register a source on its first `SetSource`, unregister on its last `RemoveSource`, no self-registration): registration must precede connection for the wait to be meaningful, but claims only exist after a connection succeeds, so an offline, retrying source would be invisible and the wait would complete prematurely; a source that claims nothing would never appear in diagnostics; reconnect cycles that release claims would drop membership mid-outage; and the claim path would need per-source refcounting under lock. Rejected: lifecycle state rides the lifecycle (`StartAsync`/`Dispose`), ownership events ride the ownership primitives (`SetSource`/`RemoveSource`).
- `IServiceProvider` wait overloads that force construction of DI-registered sources to pre-register them: resolving `IEnumerable<IHostedService>` constructs every hosted service in the application (servers, unrelated workers) ahead of normal host startup with observable side effects, and scoping it to sources would require a new DI registration convention. `IHostApplicationLifetime.ApplicationStarted` provides the same "all DI sources are registered" guarantee with no new API surface. Dropped; a pre-registration overload can be reintroduced additively if a real consumer needs one.
- Emitting claim/release events from `SourceOwnershipManager` instead of `SetSource`/`RemoveSource`: rejected because the documented low-level contract has sources calling `SetSource(this)` directly, which would produce silent claims and an untrustworthy stream. Claiming is not a hot path (once per property per connect cycle), so emission at the primitive costs one service lookup per claim.
- Polling info object (`property.TryGetSourceInfo()`): does not solve waiting or the startup race; it is the status quo with more fields. Rejected.
- Tracker as a DI singleton seeded from `IEnumerable<IHostedService>`: works for DI sources but requires a new DI registration and does not naturally cover dynamically created sources or multiple trees. Rejected in favor of the auto-registered context service.
- Gating `Synchronized` on an empty write retry queue: the queue can be non-empty during regular operation (any transient `WriteResult.Failure` enqueues), so the state would flap; inbound and outbound are orthogonal signals (`State` versus `PendingWriteCount`). Rejected.
- A fourth `Disconnected` state: the pump has no such phase (it loops back into connect-and-load), and `LastSynchronizedAt` recovers the "stale versus never synchronized" distinction. Rejected.
- Per-property wait method (`WaitForSourceStateAsync(property)` returning the state once well-defined): at startup the property is typically unclaimed at call time, so the method silently degrades into the whole-tree wait, which is surprising for a per-property API. The return value is rarely actionable (`NoSource` usually also means "ready" because a local-only property has no external truth), and the behavior is expressible in two lines with the existing primitives. Deferred; a reconnect-aware consumer that waits for a single already-claimed property is the scenario that would justify adding it later (wait only for the owning source instead of the whole tree).
- Waiting for "all properties claimed" as an earlier milestone than synchronized: claiming is connector-specific and happens during connect and subscribe setup. OPC UA claims during the address-space browse, which requires a live session, and the claimed set depends on what the server exposes; MQTT and WebSocket derive claims from local mapping but also claim during listening setup. There is no "claiming finished" signal to build on, the gap between claimed and synchronized is small (the slow part is connecting, which both milestones share), and values are still untrustworthy at that point. The useful part, distinguishing "will sync, still loading" from "no source", is already visible per property as `Connecting` versus `NoSource`. Rejected.
- Modeling source state as observable subject properties (making sources interceptor subjects): UIs and GraphQL would bind natively, but infrastructure state would flow through the very change pipeline the sources operate, with echo and feedback risks (a source publishing its own `Connecting` while disconnected). Applications can mirror state into their model deliberately, as HomeBlaze does today. Rejected.
- Moving ownership storage into the Registry: gives ownership a metadata home but changes nothing functionally; per-property attached data stays because `TryGetSource` is on the hot outbound path (checked per change in `ChangeQueueProcessor`) and must remain O(1) and allocation-free. Rejected.
- `IsPropertyInScope` intent query on `ISubjectSource` (`false` = never claimed, `true` = claimed if the external system exposes it, `null` = unknown locally): would allow definitive "local-only" answers at startup for out-of-scope properties and better "why is this not syncing" diagnostics. Deferred as future work; not needed for the current consumers.
