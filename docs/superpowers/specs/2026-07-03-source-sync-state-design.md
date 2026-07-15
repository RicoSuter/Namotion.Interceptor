# Source Synchronization State and Source Event Stream

Date: 2026-07-03
Status: Approved design (revised after design review; amended after a grilling session on 2026-07-10), pending implementation

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
    Unclaimed,     // only returned by the property-level API: no source has claimed the property
    Connecting,    // registered or claimed, but subscribe-read-replay is not complete; also after a detected connection loss
    Synchronized,  // initial load complete, live updates flowing
    Stopped        // source shut down (host stop or dynamic removal); final unless the source is started again
}
```

One enum is shared by the source-level and property-level APIs. A source itself is never `Unclaimed`; that value exists so the property-level API returns one coherent type. There is no separate `Disconnected` state: after a failure the pump loops straight back into its connect-and-load phase, so a disconnected source is a connecting source. The lost diagnostic distinction ("never synchronized" versus "was synchronized, now stale") is recovered through `LastSynchronizedAt`.

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
- `StateChanged` is the notification primitive: the source raises it itself whenever `State` changes, with the same `SourceEvent` payload used on the stream (no separate event args type). Raising the event is part of the interface contract and is the idiomatic thing implementers get right; sources never report state to the monitor directly. Unlike the monitor stream, this event is raised synchronously on the transitioning thread (normally the pump): handlers must be fast and should treat it as observe-only; mutating consumers belong on the monitor stream.
- Consumers pick by shape: aggregate consumers (waits, dashboards) use the monitor stream below; a consumer holding a specific source reference (for example an application device wrapper) subscribes to that source's `StateChanged` directly, with no enumeration race and no stream filtering.

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
- `LastSynchronizedAt` is stored as UTC ticks in a `long` field (`0` means never synchronized), written with `Interlocked.Exchange` immediately after a successful transition to `Synchronized` and before the event is raised, so handlers always observe the fresh value; a rejected transition (an in-flight `Synchronized` losing against sticky `Stopped` during dispose) leaves the timestamp untouched. It is read with `Interlocked.Read` and materialized to `DateTimeOffset?` in the property getter. This is the same pattern as `PropertyReference.SetWriteTimestamp`; a `DateTimeOffset?` field would tear under concurrent access.
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
    SourceState OldState,          // StateChanged: the source's previous state; ownership events: the property's previous effective state
    SourceState NewState,          // StateChanged: the source's new state; ownership events: the property's new effective state
    DateTimeOffset Timestamp);
```

Delivery rules:

- Delivery is asynchronous and serialized. Emission from any thread enqueues the `SourceEvent` onto a monitor-owned queue; an on-demand drain loop (single-flight on the thread pool, exits when the queue is empty, so there is no permanent task and no dispose lifecycle) delivers events to subscribers one at a time in enqueue order, outside all locks. This matches how property changes already reach consumers (`PropertyChangeQueue`: writers enqueue, consumers drain on their own threads) and is what makes the handler contract below possible.
- Handlers may read and write the object graph, including writing registry attributes directly in the callback. Because delivery happens on the drain thread outside all internal locks, a handler write is an ordinary property write. Handlers observe a deferred view: by delivery time the authoritative state (`TryGetSource`/`GetSourceState`) can already be newer than the event, and consumers reconcile against it.
- State-transition events of a source are enqueued from the transitioning thread after the sticky compare-exchange serializes the transition. Ownership events (`PropertyClaimed`/`PropertyReleased`) are enqueued after the ownership change completes, on the mutating thread (the pump, a lifecycle detach, or any caller of the public primitives), and carry no cross-event ordering guarantee; a concurrent claim/release race on the same property can be enqueued out of order. The stream is a change notification, not a ledger: `TryGetSource`/`GetSourceState` are the authoritative reads, and the attach/detach catch-up re-synchronizes the view. Preventing same-property inversion would require the enqueue to span the ownership compare-and-set, nesting it inside the lifecycle lock and the public claim primitives; deliberately not done.
- Handlers should still be fast: a slow handler delays delivery to the other subscribers, though never the pump, the claim path, or the attach/detach paths. Claim bursts are real (an OPC UA browse can claim thousands of properties in one connect cycle) and queue transiently as structs until drained; diagnostics UIs throttle or sample on their side.
- A throwing handler is caught and logged by the drain loop; it breaks neither the other subscribers nor any emitting path.

### Emission points

Each event kind is emitted at the lowest level that knows the truth:

- **`PropertyClaimed` / `PropertyReleased`: emitted by `SetSource` and `RemoveSource` themselves**, not by `SourceOwnershipManager`. The documented contract tells sources to claim by calling `SetSource(this)` directly, and the low-level ownership API is public, so emission at the primitive is the only way the stream stays trustworthy. Emission happens only on actual ownership transitions: an idempotent re-claim by the same source and a rejected claim (property owned by a different source) emit nothing, and `RemoveSource` emits only when it actually removed the ownership entry. To distinguish a fresh claim from a re-claim atomically, `PropertyReference` gains `TryAddPropertyData(key, value)`, the add-if-absent mirror of the existing `TryRemovePropertyData`; `SetSource` switches from `GetOrSetPropertyData` to it. `RemoveSource` already reports actual removal. Ownership events describe the property's effective-state transition, not the source's: `PropertyClaimed` carries `Unclaimed` to the owning source's state, `PropertyReleased` carries the state at release to `Unclaimed`. The attach/detach catch-up emits with the same payload rule. This makes "apply `NewState`" universally correct for per-property consumers; a release can never leave a stale `Synchronized` behind.
- Monitors are resolved via `property.Subject.Context.GetServices<SourceMonitor>()` (service resolution walks fallback contexts) and the event is delivered to every resolved monitor. Single-service resolution would throw for a subject attached to two trees (multi-parent subjects see both trees' monitors through their fallback contexts); iterating handles that case and gives the right semantics, since a property visible in two trees appears on both streams. The result is a cached `ImmutableArray`, usually of length 0 or 1; when empty, emission is a no-op, so plain contexts and benchmarks pay nothing.
- **`StateChanged`: raised by the source itself** (the pump in `SubjectSourceBase`). The monitor, subscribed since registration, forwards each event to the stream.
- **`SourceRegistered` / `SourceUnregistered`: emitted by the monitor** in `Register`/`Unregister` (called from `SubjectSourceBase.StartAsync` and dispose).
- `SourceOwnershipManager` has no eventing role. It remains a convenience for claimed-set bookkeeping and automatic release on subject detach; its releases go through `RemoveSource` and therefore appear on the stream like any other.

### SourceMonitor as an explicitly configured context service

The monitor lives in the `IInterceptorSubjectContext`, not in DI, and is added explicitly at context configuration time:

```csharp
public static IInterceptorSubjectContext WithSourceMonitoring(this IInterceptorSubjectContext context)
```

called on the tree root context alongside the other features (same shape as the existing `WithSourceTransactions()`). Automatic placement from inside a source cannot work: connectors receive the context of their own root subject, which can be a subtree context, and a service added there is invisible to the tree root and to sibling subtrees (context fallbacks point child to parent, never sideways). A subtree-rooted source would fragment the tree into per-subtree monitors, and a waiter on the root context would create yet another empty monitor and block forever. Explicit configuration puts the monitor at the scope the whole tree resolves.

`SubjectSourceBase` overrides `StartAsync`: it resolves all monitors reachable from its context (`GetServices<SourceMonitor>()` walks the fallback chain upward, so a subtree-rooted source finds the root monitor), registers with each, and only then calls `base.StartAsync` to launch the pump. If no monitor is resolvable, the source runs untracked; existing applications and benchmarks that never call `WithSourceMonitoring()` keep working unchanged. On `Dispose` the source unregisters from the monitors it registered with. Because both DI-hosted and dynamically created sources start through the same hosted-service lifecycle, registration works for both.

Registering at start instead of at construction has three consequences:

- `SourceRegistered` always publishes a fully constructed source. No handler or `Sources` snapshot reader can observe a source whose derived constructor has not finished.
- A source that is constructed but never started stays invisible. It can never synchronize, so it must not hold `WaitForSourcesSynchronizedAsync` open.
- `Register` is idempotent (a re-register is a no-op that emits nothing), which makes stop/start cycles safe.

Custom `ISubjectSource` implementations that do not derive from `SubjectSourceBase` register themselves with the resolvable monitors once fully constructed and started, and unregister on dispose.

Two robustness contracts: the monitor does not require lifecycle tracking (on a context without `WithLifecycle()` the attach/detach catch-up is disabled while registration, stream, and waits work normally; unlike `SourceOwnershipManager` it never throws for a missing lifecycle interceptor), and `Unregister` of a source that was never registered is a no-op.

```csharp
public class SourceMonitor
{
    IReadOnlyList<ISubjectSource> Sources { get; }              // snapshot for polling consumers
    SourceSubscription Subscribe(Action<SourceEvent> handler);  // typed event stream

    void Register(ISubjectSource source);                 // idempotent
    void Unregister(ISubjectSource source);

    Task WaitForSourcesSynchronizedAsync(CancellationToken cancellationToken = default);           // all sources
    Task WaitForSourcesSynchronizedAsync(Func<ISubjectSource, bool> filter, CancellationToken cancellationToken = default);
}

public sealed class SourceSubscription : IDisposable
{
    ImmutableArray<ISubjectSource> Sources { get; }   // snapshot captured atomically with the subscription
}
```

On `Register` the monitor subscribes to the source's `StateChanged` event and forwards its events to the stream; on `Unregister` it unsubscribes. Registration precedes the pump start, so `SourceRegistered` precedes any forwarded `StateChanged` of that source. Subscription and registration are serialized by the monitor, and `Subscribe` captures the `Sources` snapshot atomically with the subscription under that serialization, returning it on the `SourceSubscription` handle. Reading `monitor.Sources` separately after subscribing is not race-free (a source registered between the two calls appears in both the snapshot and the stream, so a naive consumer double-counts it); the handle's snapshot is the correct baseline. Each subscription is stamped with the queue's current sequence number, so events enqueued before the subscription are never delivered to it: the handle's snapshot plus the delivered events observe every change exactly once. The wait methods are internal stateful consumers of registration, unregistration, and state-change notifications, notified synchronously at the emission points rather than through the delivery queue, so wait correctness does not depend on drain latency; they are tracked separately from public stream subscribers (see the catch-up cost guard below).

### View catch-up on subject attach and detach

The stream's contract is "the tree's view of ownership", not "every `SetSource` call anywhere". Two lifecycle situations would otherwise punch holes in that contract: a property claimed while its subject was not yet attached to the tree (no monitor reachable, nothing emitted) whose subject is attached later, and a claimed subject detaching from the tree without its claims being released (possible for sources that bypass `SourceOwnershipManager`).

The monitor therefore hooks the existing lifecycle infrastructure (which connector contexts already require; `SourceOwnershipManager` throws without `WithLifecycle()`):

- **On subject attach**: scan the subject's properties and emit `PropertyClaimed` for any that arrive already claimed; they just entered the view.
- **On subject detach**: emit `PropertyReleased` for any still claimed; they left the view. Releases performed by `SourceOwnershipManager` during detach go through `RemoveSource` and are real events; the scan then finds nothing, so there are no duplicates.

Cost guard: when the stream has no public subscribers, the scan is skipped entirely, so the recently optimized attach/detach hot paths pay one subscriber-count check and nothing else. With subscribers, the scan only enqueues events while the lifecycle lock is held; handler code never runs under the lock. Pending waits deliberately do not count as subscribers: the wait methods consume monitor notifications through internal bookkeeping rather than a stream subscription, because a wait is typically active during startup, exactly when subject attach storms happen, and waits never need property claim events. Built-in connectors always claim after attach, so the missed-claim window is theoretical today; the catch-up exists to give the stream a consistency contract subscribers can rely on without reasoning about holes.

### Wait semantics

The wait completes when at least one matching source is currently registered and every currently registered, non-`Stopped` matching source is `Synchronized`. If the only registered matching sources are `Stopped`, the condition is vacuously met and the wait completes.

- Implementation is a re-evaluation over a `TaskCompletionSource` (created with `RunContinuationsAsynchronously`), triggered by registration, unregistration, and state change notifications.
- A source registered while someone is waiting is included; a source that drops back to `Connecting` before the condition is met re-blocks the wait.
- Once the returned task completes it stays completed. Callers who care about later disconnects subscribe to the stream or call the wait again.
- A waiter racing a stop/start cycle can complete while a source is momentarily `Stopped`, before the restart re-enters `Connecting` at pump entry. This is a real path: `HostedServiceHandler` stops a subject's attached source on context detach and restarts the same instance on re-attach. Restarts are application initiated, so the application owns the sequencing: initiate the restart first, then wait. Connection loss needs no such care because the pump transitions straight to `Connecting` and re-blocks the wait.
- An empty set does not complete the wait. This is deliberate: it converts the startup race (waiter runs before sources are started) into correct blocking behavior, because mid-wait registrations are picked up. The cost is that waiting on a tree that never gets any source blocks until cancellation; this is documented.
- The filter overload covers subtree scoping (for example, only sources whose `RootSubject` is under a given subject). A filter that never matches blocks until cancellation.

### Monitor access and the startup race

The wait methods live only on the monitor; consumers resolve the monitor and are responsible for picking the right one. A single accessor extension in `Namotion.Interceptor.Connectors` exists purely for discoverable error messages:

```csharp
public static SourceMonitor GetSourceMonitor(this IInterceptorSubjectContext context)
```

It resolves through the fallback chain (so calling it on a child subject's context finds the tree monitor). If no monitor is resolvable, it throws `InvalidOperationException` with guidance to call `WithSourceMonitoring()` on the tree root context; a missing monitor would otherwise surface as a silent forever-block. If multiple monitors are resolvable (one configured locally and one on a shared ancestor context, or a multi-parent subject attached to two trees), it throws and directs the caller to `GetServices<SourceMonitor>()`: combining monitors is a semantic decision that belongs at the callsite, for example `Task.WhenAll` over each monitor's wait to mean "all trees are live". Each monitor applies the empty-set rule to its own sources, so waiting on a configured tree before any source exists blocks until the first source registers.

The startup race for hosted waiters is closed by the hosting model itself. Sources register during host startup, when the host calls their `StartAsync` sequentially. A waiter that runs before host startup completes could observe a partially registered set: if an early source synchronizes before a later source is started, the wait could complete prematurely. The built-in guarantee against this is `IHostApplicationLifetime.ApplicationStarted`: once it fires, every DI-registered source has been started and is therefore registered. Hosted waiters await `ApplicationStarted` first (a few lines with a `TaskCompletionSource` registered on the token), but only from code that runs outside host startup, such as `BackgroundService.ExecuteAsync` or other post-start code. Awaiting the token inside `IHostedService.StartAsync` deadlocks the host, because `ApplicationStarted` only fires after every `StartAsync` has returned. Code that runs after the host has started needs nothing. Even without the guard the window is small, because a source would have to complete its network connect and initial load faster than the host finishes starting the remaining services; the guard makes premature completion impossible rather than unlikely.

Dynamic sources that do not exist yet at call time cannot be known by any mechanism. Applications that create sources dynamically (for example HomeBlaze device loading) coordinate at the application level: finish starting sources, then wait, or rely on mid-wait registration pickup.

### Per-property API

A single read-only extension on `PropertyReference` in `Namotion.Interceptor.Connectors`:

```csharp
public static SourceState GetSourceState(this PropertyReference property)
```

Derivation: `TryGetSource` returns nothing gives `Unclaimed`; otherwise the owning source's `State`. Computed on read from existing attached data, with no storage and no allocations. For richer diagnostics (`LastSynchronizedAt`, `PendingWriteCount`) the caller uses `TryGetSource` and reads the source directly.

The result is only fully meaningful after phase 1 of the consumer protocol below: claiming happens when a source connects, so before that point `Unclaimed` is ambiguous ("not claimed yet" versus "never claimed"). Once a property is claimed, `GetSourceState()` returns `Connecting` instead of `Unclaimed`, so even before phase 1 completes a diagnostics view can distinguish "will sync, still loading" from "no source, so far".

Per-property change observation needs no dedicated API: a property's effective state changes exactly when it is claimed or released (a `PropertyClaimed`/`PropertyReleased` event for that property) or when its owning source transitions (a `StateChanged` event for that source). Both are on the stream; consumers filter.

### Consumer protocol (canonical pattern)

```csharp
// Phase 0 (hosted waiters only): ensure host startup finished, so every
// DI-registered source has been started and is registered.
// (IHostApplicationLifetime.ApplicationStarted awaited via TaskCompletionSource.)
// Run this from ExecuteAsync or other post-start code, never inside StartAsync.
await applicationStartedTask;

// Phase 1: wait until claiming is over and every source finished subscribe-read-replay
await context.GetSourceMonitor().WaitForSourcesSynchronizedAsync(cancellationToken);

// Phase 2 is now instant and well-defined per property:
// Synchronized = live value, Unclaimed = local-only property that was never claimed
```

Phase 2 is not a second wait. Its value is that per-property answers only become well-defined after phase 1: from that moment, `Unclaimed` genuinely means "local-only". This resolves the problem that it is not predictable which properties a source will claim.

Consumers interested in a single property use the same protocol: wait once, then read `property.GetSourceState()`.

### Driving application use case: availability attributes

The concrete consumer this design must serve well, and the scenario that motivated the queued delivery model: an application declares a stored `ConnectionState` registry attribute (of type `SourceState`) on selected properties and a derived `IsAvailable` attribute computing `ConnectionState == SourceState.Synchronized`. A small updater component subscribes to the monitor stream once and maintains `ConnectionState`; the existing derived-property infrastructure propagates `IsAvailable` changes to UIs and GraphQL automatically. The registry already supports both halves (`AddAttribute` or `[PropertyAttribute]` for the stored attribute, `AddDerivedAttribute` or `[Derived]` for the derived one).

The updater follows the stream contracts:

- Because delivery is asynchronous, serialized, and outside all locks, the updater writes `ConnectionState` directly in the callback; it needs no queue or dispatcher of its own. `ConnectionState` is eventually consistent, which is acceptable for its purpose.
- The stream does not replay, so a late-starting updater bootstraps by subscribing first and then initializing from a registry walk using `GetSourceState()` as the authoritative read. Events racing the walk re-apply idempotent writes, so the view self-heals.
- On `PropertyClaimed`/`PropertyReleased` it updates the single property (the event's `NewState` carries the property's new effective state, `Unclaimed` for releases, so no extra read is needed). On `StateChanged` it updates the affected properties using a small per-source index it maintains as a side effect of the claim and release events it already handles, seeded by the bootstrap walk; for a small static attributed set, walking its own attributed properties and checking ownership via `TryGetSource` works too. Either way, no per-source claimed-set query is needed on the API. Serialized delivery makes the index single-writer; only the bootstrap walk shares a small uncontended lock with the handler.

The costs are opt-in and scoped: a permanently subscribed updater keeps the attach/detach catch-up scan active, each source transition costs one write per attributed property of that source, and the attributes are real registry members visible to other connectors unless filtered. These are exactly the costs the rejected built-in `@syncState` attribute would have imposed on every claimed property of every application; here the application pays them only where it declares the attributes.

### Diagnostics

- A dashboard subscribes to the stream once and starts from the subscription's snapshot: source list with `State`, `LastSynchronizedAt`, and `PendingWriteCount` (all on the `ISubjectSource` surface), claim/release activity as it happens, and per-property state by filtering events for the properties on screen.
- Notification granularity is proportionate: state transitions are per source (all properties of a source flip together), claims and releases are per property but occur only on connect cycles and topology changes.

### Edge cases

- A property claimed while its source is still loading reports `Connecting`.
- A stopped source that still owns properties reports `Stopped` per property, meaning "will not synchronize again while stopped".
- Ownership release on subject detach (existing `SourceOwnershipManager` behavior) reverts properties to `Unclaimed` and appears on the stream as `PropertyReleased`.
- Disposing a source without stopping it first still publishes `Stopped`: the disposal fallback transitions and raises before unregistering, so the stream sees `StateChanged` to `Stopped` followed by `SourceUnregistered`.
- Connectors release ownership before unregistering: their dispose path runs `SourceOwnershipManager.Dispose` (releasing all claims through `RemoveSource`) before calling the base dispose, so `PropertyReleased` events precede `SourceUnregistered` on the stream. The OPC UA client already follows this order; it is the documented convention for connector dispose implementations.
- A stopped source that is started again re-registers idempotently (no duplicate `SourceRegistered`) and transitions `Stopped` to `Connecting` at pump entry.
- A source that is constructed but never started is not registered: it does not appear in `Sources` and does not affect the wait. Disposing it raises `Stopped` on the source's own `StateChanged` event only; no monitor is involved and nothing appears on any stream.
- Claiming a property of a subject that is not attached to any tree with a monitor emits no event at claim time (the monitor resolution finds no service); the claim enters the stream via attach catch-up when the subject joins a tree. This is documented on `SetSource`.

## Delivery

One pull request implements the whole design, based on master (the design touches only the core and Connectors packages plus docs; the in-flight OPC UA loader work touches neither). The PR adds `docs/connectors-source-monitoring.md` as the permanent documentation (following the existing `connectors-` feature-page convention, linked from `docs/connectors.md`), including the availability-attributes scenario above as a worked sample (stored `ConnectionState` attribute, derived `IsAvailable`, updater with its per-source index writing directly in the callback), and deletes this spec at the end, once the design is implemented.

## Affected Code

- `Namotion.Interceptor` (core): `PropertyReference.TryAddPropertyData(key, value)`, the atomic add-if-absent counterpart to `TryRemovePropertyData`; the core public API snapshot updates accordingly.
- `Namotion.Interceptor.Connectors`: `SourceState`, `SourceEventKind`, `SourceEvent`, `ISubjectSource` members (`State`, `LastSynchronizedAt`, `PendingWriteCount`, `StateChanged`), `SubjectSourceBase` transitions (pump entry, catch, finally, disposal fallback) and `StartAsync` registration, `SourceMonitor` and `SourceSubscription` (including the lifecycle attach/detach catch-up and internal wait bookkeeping), the `WithSourceMonitoring()` context extension, the `GetSourceMonitor()` accessor extension, `GetSourceState` property extension, emission logic in `SetSource`/`RemoveSource` (built on `TryAddPropertyData`).
- Built-in connectors (OPC UA, MQTT, WebSocket): no source changes expected; they inherit from `SubjectSourceBase` and claim through the existing paths.
- Test doubles that implement `ISubjectSource` directly (for example `ConcurrentTestSource` and `BlockingTestSource` in `Namotion.Interceptor.Connectors.Tests`) gain the four new members.
- Public API snapshot tests: `ISubjectSource` changes will fail `VerifyChecksTests.PublicApi` in affected projects; the new `.verified.txt` snapshots are accepted as part of the change.
- Documentation: a new `docs/connectors-source-monitoring.md` page covering the state model, the stream and its delivery contract, waits and the consumer protocol (including the hosted-waiter `ApplicationStarted` guidance), the breaking-change note for custom `ISubjectSource` implementers, and the availability-attributes worked sample with the per-source index; `docs/connectors.md` gains a short linking section and its context recipe gains `WithSourceMonitoring()`.

## Testing

In `Namotion.Interceptor.Connectors.Tests`, following the `When<Condition>_Then<ExpectedBehavior>` naming and event-based synchronization (no hardcoded waits):

- Pump transitions via a fake source: `Connecting` initially; `Synchronized` after initial load; back to `Connecting` on pump failure; `Stopped` on cancellation; `LastSynchronizedAt` set exactly on entering `Synchronized` and preserved through a later disconnect; a restarted source transitions `Stopped` to `Connecting` at pump entry and can synchronize again.
- Sticky `Stopped`: after the disposal fallback set `Stopped`, a late pump transition (catch-path `Connecting` or in-flight `Synchronized`) neither overwrites the state, nor emits an event, nor updates `LastSynchronizedAt`.
- Registration lifecycle: `StartAsync` registers the source and `SourceRegistered` precedes any `StateChanged` of that source on the stream; `Register` is idempotent (a stop/start cycle emits no duplicate `SourceRegistered`); a constructed but never started source is not registered and does not affect the wait; disposing without stopping emits `Stopped` before `SourceUnregistered`; disposing a never-started source does not throw and emits nothing to the stream; `Unregister` of a never-registered source is a no-op; after unregistration the monitor no longer forwards the source's events.
- Monitor robustness: a monitor on a context without lifecycle tracking supports registration, stream, and waits without throwing (catch-up disabled); a claim on a subject attached to two trees with different monitors throws nothing and appears on both streams; a source started on a context without any monitor runs untracked and does not throw; `GetSourceMonitor()` on a context without a monitor throws `InvalidOperationException` with configuration guidance; with monitors on both a local and an ancestor context it throws, while `GetServices<SourceMonitor>()` returns both so the consumer can combine waits explicitly.
- `TryAddPropertyData` (core): returns true and stores the value when the key is absent; returns false and leaves the existing value untouched when present.
- Attach/detach catch-up: attaching a subject with already-claimed properties emits `PropertyClaimed` for each; detaching a subject with still-claimed properties emits `PropertyReleased`; releases performed by `SourceOwnershipManager` during detach produce no duplicate events; without public stream subscribers the catch-up scan is skipped; a pending wait alone does not trigger the scan.
- Claim/release emission: a fresh claim emits `PropertyClaimed`; an idempotent re-claim by the same source emits nothing; a rejected claim (owned by another source) emits nothing; `RemoveSource` emits `PropertyReleased` only on actual removal; claims without a monitor in the context emit nothing and do not throw.
- Handler robustness: a throwing subscriber is logged by the drain loop and breaks neither the other subscribers nor any emitting path; a handler that writes a registry attribute directly in the callback succeeds, including for events originating from the attach/detach catch-up (no deadlock, no reentrancy failure).
- Subscription consistency: the `SourceSubscription` snapshot plus the delivered events observe every change exactly once, including a source registering concurrently with `Subscribe`; events enqueued before the subscription are not delivered to it; delivery order matches enqueue order.
- Ownership event payloads: `PropertyClaimed` carries `Unclaimed` to the owning source's state; `PropertyReleased` carries the state at release to `Unclaimed`, including for releases performed during detach.
- Wait semantics: completes only when all non-`Stopped` sources are `Synchronized`; empty set blocks; a source registered mid-wait is included and re-blocks the wait when `Connecting`; filter overload scopes correctly; cancellation propagates.
- Per-property: `Unclaimed` for a property no source has claimed, `Connecting` for claimed while loading, `Synchronized` for claimed after load, `Stopped` for a claimed property of a stopped source.

## Alternatives Considered

- Observable `@syncState` dynamic registry attribute per claimed property: fully per-property observable through existing change tracking, but O(claimed properties) writes and allocations per transition, wire noise into other connectors, and attribute lifetime coupled to claim/release. Rejected; filtering the event stream provides per-property observability without any of these costs.
- Per-source events as the sole mechanism (no aggregated stream): forces aggregate consumers into per-source bookkeeping with an inherent race between enumerating sources and subscribing to each one. The final design keeps the per-source `StateChanged` event as the notification primitive feeding the monitor, while the stream is the aggregate consumption surface with snapshot-plus-subscribe consistency.
- A plain .NET event or `IObservable<SourceEvent>` instead of the monitor's `Subscribe` method: both fight the stream's contracts. Per-handler exception isolation over a multicast event requires `GetInvocationList()`, which allocates on every emission on claim-burst paths, and a throwing Rx observer propagates into the emitter; `Subscribe` keeps an immutable handler snapshot and invokes each handler under try/catch without per-emission allocations. The subscribe-then-snapshot consistency contract requires subscription to be serialized with registration and emission under the monitor's lock, which compiler-generated event accessors cannot do. The plain event stays where it fits: `ISubjectSource.StateChanged` is single-source, low-rate, and implementer-facing. An `IObservable<SourceEvent>` adapter can be added additively later if a consumer wants operator composition.
- A per-source claimed-properties query (on `ISubjectSource` or as a monitor-side claim ledger): would slightly simplify consumers like the availability updater, but the updater already knows its attributed properties, sources that bypass `SourceOwnershipManager` could not answer the query, and a monitor-side ledger would reintroduce per-property storage. Deferred; additive if a real consumer needs it.
- Connector-maintained state attributes (the ownership layer writes an app-declared `ConnectionState` attribute directly at claim, release, and state transitions, skipping the monitor and stream entirely): dramatically less machinery for the availability use case and synchronously consistent, but it makes the connector layer opinionated about app-model conventions (attribute discovery, shape, and release semantics are consumer-specific), and it serves exactly one consumer pattern while removing the general surface every other consumer would need. Rejected: ownership primitives stay unopinionated, consumer-specific logic rides the stream, and a convenience package built on the stream can ship later if the pattern proves common.
- Synchronous handler delivery on the emitting thread: emissions can occur while the lifecycle interceptor's lock is held (attach/detach catch-up, detach releases), which would force an observe-only handler contract and push every mutating consumer (such as the availability updater) into building its own queue and dispatcher. Rejected in favor of monitor-owned queued delivery, matching the `PropertyChangeQueue` architecture; the cost is deferred delivery, which the notification-plus-authoritative-reads contract already absorbs.
- State reporting through monitor calls instead of a source-raised event: a custom source that registers but forgets to report its transitions would hang `WaitForSourcesSynchronizedAsync` silently, and "call the monitor service resolved from your context" is unusual choreography compared to the idiomatic "raise your event when your state changes". Rejected in favor of the event primitive.
- Auto-registered monitor (source adds the monitor to its own context via `TryAddService` on start, no explicit configuration): `TryAddService` cannot place a service at tree scope from below. A subtree-rooted source would add the monitor to its subtree context, where the tree root and sibling subtrees cannot resolve it (fallbacks point child to parent); monitors fragment per subtree, and a waiter on the root context creates yet another empty monitor and blocks forever. Rejected in favor of explicit `WithSourceMonitoring()` configuration on the tree root context, matching the `WithLifecycle()` requirement sources already have.
- Serialized (totally ordered) stream emission through a monitor-wide lock: preventing same-property claim/release inversion would require the emission lock to span the ownership compare-and-set, nesting a new lock inside the lifecycle lock and the public claim primitives. Deferred; the documented weaker contract (notification stream plus authoritative reads) suffices, and strengthening delivery guarantees later is non-breaking.
- Constructor-time monitor registration (register in the `SubjectSourceBase` constructor): the base constructor runs before the derived constructor, so `SourceRegistered` and the `Sources` snapshot would publish a partially constructed source (`RootSubject` and other derived members not yet assigned, a `NullReferenceException` trap for handlers), and a source that is constructed but never started would block the wait forever. Rejected; `StartAsync` is the natural fully-constructed hook and gives "only sources that can ever synchronize participate" semantics.
- Claim-derived registration (register a source on its first `SetSource`, unregister on its last `RemoveSource`, no self-registration): registration must precede connection for the wait to be meaningful, but claims only exist after a connection succeeds, so an offline, retrying source would be invisible and the wait would complete prematurely; a source that claims nothing would never appear in diagnostics; reconnect cycles that release claims would drop membership mid-outage; and the claim path would need per-source refcounting under lock. Rejected: lifecycle state rides the lifecycle (`StartAsync`/`Dispose`), ownership events ride the ownership primitives (`SetSource`/`RemoveSource`).
- `IServiceProvider` wait overloads that force construction of DI-registered sources to pre-register them: resolving `IEnumerable<IHostedService>` constructs every hosted service in the application (servers, unrelated workers) ahead of normal host startup with observable side effects, and scoping it to sources would require a new DI registration convention. `IHostApplicationLifetime.ApplicationStarted` provides the same "all DI sources are registered" guarantee with no new API surface. Dropped; a pre-registration overload can be reintroduced additively if a real consumer needs one.
- Emitting claim/release events from `SourceOwnershipManager` instead of `SetSource`/`RemoveSource`: rejected because the documented low-level contract has sources calling `SetSource(this)` directly, which would produce silent claims and an untrustworthy stream. Claiming is not a hot path (once per property per connect cycle), so emission at the primitive costs one service lookup per claim.
- Polling info object (`property.TryGetSourceInfo()`): does not solve waiting or the startup race; it is the status quo with more fields. Rejected.
- Monitor as a DI singleton seeded from `IEnumerable<IHostedService>`: works for DI sources but requires a new DI registration and does not naturally cover dynamically created sources or multiple trees. Rejected in favor of the explicitly configured context service.
- Gating `Synchronized` on an empty write retry queue: the queue can be non-empty during regular operation (any transient `WriteResult.Failure` enqueues), so the state would flap; inbound and outbound are orthogonal signals (`State` versus `PendingWriteCount`). Rejected.
- A fourth `Disconnected` state: the pump has no such phase (it loops back into connect-and-load), and `LastSynchronizedAt` recovers the "stale versus never synchronized" distinction. Rejected.
- Per-property wait method (`WaitForSourceStateAsync(property)` returning the state once well-defined): at startup the property is typically unclaimed at call time, so the method silently degrades into the whole-tree wait, which is surprising for a per-property API. The return value is rarely actionable (`Unclaimed` usually also means "ready" because a local-only property has no external truth), and the behavior is expressible in two lines with the existing primitives. Deferred; a reconnect-aware consumer that waits for a single already-claimed property is the scenario that would justify adding it later (wait only for the owning source instead of the whole tree).
- Waiting for "all properties claimed" as an earlier milestone than synchronized: claiming is connector-specific and happens during connect and subscribe setup. OPC UA claims during the address-space browse, which requires a live session, and the claimed set depends on what the server exposes; MQTT and WebSocket derive claims from local mapping but also claim during listening setup. There is no "claiming finished" signal to build on, the gap between claimed and synchronized is small (the slow part is connecting, which both milestones share), and values are still untrustworthy at that point. The useful part, distinguishing "will sync, still loading" from "no source", is already visible per property as `Connecting` versus `Unclaimed`. Rejected.
- Modeling source state as observable subject properties (making sources interceptor subjects): UIs and GraphQL would bind natively, but infrastructure state would flow through the very change pipeline the sources operate, with echo and feedback risks (a source publishing its own `Connecting` while disconnected). Applications can mirror state into their model deliberately, as HomeBlaze does today. Rejected.
- Moving ownership storage into the Registry: gives ownership a metadata home but changes nothing functionally; per-property attached data stays because `TryGetSource` is on the hot outbound path (checked per change in `ChangeQueueProcessor`) and must remain O(1) and allocation-free. Rejected.
- `IsPropertyInScope` intent query on `ISubjectSource` (`false` = never claimed, `true` = claimed if the external system exposes it, `null` = unknown locally): would allow definitive "local-only" answers at startup for out-of-scope properties and better "why is this not syncing" diagnostics. Deferred as future work; not needed for the current consumers.
