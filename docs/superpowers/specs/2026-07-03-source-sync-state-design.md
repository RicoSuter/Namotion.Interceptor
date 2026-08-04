# Source Synchronization State and Source Event Stream

Date: 2026-07-03
Status: Approved design, pending implementation.
Revised 2026-08-04 after a second design round: the wait became branch scoped and moved onto the
subject, phase 0 was replaced by an explicit registration signal, delivery moved to per-subscriber
queues, and the handler contract became an authoritative re-read.

## Problem

Consumers of a subject tree cannot tell whether a property value is actually in sync with its external source. At application start the tree is offline. Sources connect asynchronously, claim their properties, load the initial state, and only then start applying live updates. During that window a property shows a local default value that was never confirmed by the external system, and after a connection loss it silently shows a stale last-known-good value.

The existing API is insufficient:

- `property.TryGetSource()` is polling based and only returns the owning source, not whether that source is synchronizing.
- At startup, no source has claimed anything yet, so "no source" is ambiguous: it can mean "no source attached yet" or "this property will never have a source".
- Which properties a source will claim is not predictable up front (a source may claim only part of the branch it is configured for), so waiting cannot be expressed in terms of properties alone.

There is also a structural asymmetry: property values have a full traceability pipeline (interceptors, change subscriptions, observables, timestamps, source attribution), while source metadata has none. Claims, releases, and connection state are silent writes into per-property attached data. Everything built on top of them is forced to poll.

Two consumer groups need this:

1. Waiting consumers that must block until the part of the tree they care about is live (for example, a startup reconciliation service that mirrors device values into a database, or automation logic that must not run on default values).
2. Diagnostics that want to observe sync state per source and per property, including claim and release activity.

## Goals

- A source-level state with well-defined transitions, driven by the existing pump lifecycle in `SubjectSourceBase`.
- A single typed event stream for all source metadata: source registration, state transitions, property claims, and property releases.
- An awaitable primitive scoped to a branch of the tree, so an unrelated failing connection cannot block a consumer that does not depend on it.
- A cheap per-property state derived from source ownership, with no per-property storage.
- Works for both DI-registered (hosted service) sources and dynamically created sources, with no new DI registrations.
- Quiescent consistency for consumers maintaining a derived view: once claims, releases, and transitions settle, every consumer view agrees with the authoritative read.

## Non-Goals

- Outbound write confirmation. `Synchronized` describes the inbound direction only (the model mirrors the external system). Pending outbound writes remain visible through `PendingWriteCount` on the source; the write retry queue can be non-empty during regular synchronized operation and must not affect the state.
- A per-property observable `@syncState` registry attribute. Per-property observability falls out of filtering the event stream instead, with no per-property storage, no intercepted writes, and no wire leakage.
- An audit history of ownership transitions. The stream is a notification stream, not a ledger. Same-property claim and release events can be enqueued out of order, so a consumer cannot reconstruct the sequence of transitions from it. Consumers maintain views, not histories. See `SourceEvent.CurrentState` below and the versioned-ownership alternative for what it would take to lift this.
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

### State transitions are driven by SubjectPropertyWriter, not by the pump

The obvious placement, transitions on the `SubjectSourceBase.ExecuteAsync` pump path, is wrong, and would have shipped a feature that reports `Synchronized` straight through a real outage on every built-in connector.

The built-in clients do not route ordinary reconnects through the base pump. They detect the drop themselves, buffer, reconnect, and reload while the base pump sits inside `processor.ProcessAsync`. `StartBuffering` and `LoadInitialStateAndResumeAsync` are called from five sites outside the pump: `MqttSubjectClientSource:125,549` (via `MqttConnectionMonitor` and `OnReconnectedAsync`), `WebSocketSubjectClientSource:598,629,659` (the monitor loop and `ReconnectAndResumeAsync`), `OpcUaSubjectClientSource:458,491`, and `SessionManager:77,80,611` (`PerformFullStateSyncIfNeededAsync`, after an SDK auto-reconnect). A pump-only design leaves `State` at `Synchronized` for every one of those outages, and its catch block only ever fires for failures that escape all the way out of `StartListeningAsync` or `ProcessAsync`.

The fix is to move the two connection-phase transitions onto `SubjectPropertyWriter`, which every one of those paths already goes through and which already holds the source (`private readonly ISubjectSource _source`, assigned from `new SubjectPropertyWriter(this, logger)`):

- **`StartBuffering()` transitions to `Connecting`.** Buffering starts exactly when the source has stopped trusting its live feed, on first connect and on every reconnect.
- **Normal completion of `LoadInitialStateAndResumeAsync` transitions to `Synchronized`**, including its "already replayed by a concurrent reconnection" early return, which also means state has been loaded and replayed. Duplicate transitions are absorbed by the no-op rule below. A load that throws does not transition, and the exception propagates as before.

Any future connector inherits correct reporting from the writer it already uses. The writer reaches the transition through an internal interface implemented by `SubjectSourceBase`; a custom `ISubjectSource` that constructs its own writer simply gets no transition, which is correct, since custom implementers own their state by the interface contract.

**The writer alone is still not sufficient, because one connector detects loss before it buffers.** OPC UA's `SessionManager.OnKeepAlive` (`SessionManager.cs:274`) sees the bad keep-alive status, sets `_isReconnecting`, and hands off to `SessionReconnectHandler.BeginReconnect`. It does not call `StartBuffering`. Buffering happens only afterwards, in `PerformFullStateSyncIfNeededAsync` (`SessionManager.cs:77`), once the SDK has reconnected and the health-check loop runs. So for the entire SDK auto-reconnect window, which is the common OPC UA outage, the source would still report `Synchronized`.

`SubjectSourceBase` therefore also exposes an internal `ReportConnectionLost()` that transitions to `Connecting` without touching buffering, and `OnKeepAlive` calls it at loss detection. Keeping it separate from `StartBuffering` is deliberate: calling `StartBuffering` there would replace `_updates` with a fresh list, and the later `StartBuffering` in `PerformFullStateSyncIfNeededAsync` would then discard whatever was buffered in between, so overloading buffering to carry state reporting would change data-path behaviour to fix a reporting bug.

This is the one place where a connector has to remember something, so it is covered by connector-level regression tests rather than by trust. Each client connector gets an integration test that induces an outage through the existing `IFaultInjectable` hook and asserts the source reports `Connecting` for the duration and `Synchronized` again afterwards. That test is what stops the next connector from silently reintroducing this defect.

The remaining transitions stay on the lifecycle, where the writer has no visibility:

- Construction: the state field starts as `Connecting`. Nothing can observe the source before registration, so no event is involved.
- Pump entry (start of `ExecuteAsync`): transition to `Connecting`. Always a no-op in practice, since the field already starts there, but it keeps the invariant local to the pump.
- In the pump's catch block (a failure that escaped the connector's own handling): `Connecting`.
- On pump exit (cancellation or shutdown), in a finally block: `Stopped`.
- In `Dispose`, as a fallback: if the state is not already `Stopped`, transition to `Stopped` and raise the event before unregistering. This makes dispose-without-stop safe (the final `Stopped` still reaches the stream while the source is registered). Stopping before disposing remains the graceful path, because a hard-disposed pump cannot flush pending writes.

`Synchronized` still lands before `ReapplyRetryQueue`, because the retry queue reflects the outbound direction and can be non-empty at any time during regular operation.

Implementation notes:

- The state is stored in an `int` field (the enum's underlying value) so it works with `Volatile.Read` and `Interlocked.CompareExchange`. All transitions go through a helper with two rules: a transition to the current state is a no-op (no duplicate events), and **`Stopped` is terminal**. Nothing leaves it.

  Terminal, not merely sticky. An earlier revision assumed a stopped source could be restarted and would re-enter `Connecting` at pump entry, and that is false twice over. `SubjectSourceBase` is a `BackgroundService`, whose `StopAsync` cancels the lifetime token, so a second `StartAsync` on the same instance runs against an already-cancelled token. And `HostedServiceHandler` removes the service from the subject's attached list on detach, so a re-attached subject does not even try to start it again. The repository already documents this: `HostedServiceHandlerTests.cs:108` asserts the attached list is empty after detach, with the comment "the service has been stopped and removed from list (not allowed to restart again anyway)". Restart semantics are removed from this design rather than being specified for a lifecycle that does not exist.
- **The transition, the timestamp write, and the event publication are serialized against each other under a per-source lock.** Compare-exchange alone is not sufficient, because the three steps are not atomic: the writer can compare-exchange `Connecting` to `Synchronized`, be preempted, let disposal compare-exchange `Synchronized` to `Stopped` and unregister, then resume to write `LastSynchronizedAt` and raise `Synchronized` after `Stopped` has already been published. Both compare-exchanges succeed, so stickiness cannot prevent it, and subscribers observe reversed transitions while the monitor may have already unsubscribed and missed one entirely. Holding a per-source lock across read-decide-write-stamp-publish removes the window. Contention is negligible: transitions happen on connect cycles, not on the data path, and the lock is never held while consumer code runs, because monitor delivery is queued.
- `LastSynchronizedAt` is stored as UTC ticks in a `long` field (`0` means never synchronized), written with `Interlocked.Exchange` inside that lock, before the event is raised, so handlers always observe the fresh value; a rejected transition leaves the timestamp untouched. It is read with `Interlocked.Read` and materialized to `DateTimeOffset?` in the property getter. This is the same pattern as `PropertyReference.SetWriteTimestamp`; a `DateTimeOffset?` field would tear under concurrent access.
- Each transition raises the source's own `StateChanged` event from the thread performing the transition. The raise helper wraps handler invocation in try/catch with logging: a throwing subscriber must not be treated as a source failure (otherwise a buggy `Synchronized` handler would flip the source back to `Connecting` in an endless loop).

### What Synchronized means per protocol

`Synchronized` means "this source has completed the strongest initial-load guarantee its protocol offers". That is not the same guarantee everywhere, and the difference is a protocol property rather than an implementation gap.

OPC UA and WebSocket perform an explicit read of the initial state, so `Synchronized` means the values have actually been fetched.

**MQTT cannot make that promise.** `MqttSubjectClientSource.LoadInitialStateAsync` returns `null` by construction: retained messages arrive asynchronously through `OnMessageReceivedAsync`, and MQTT provides no end-of-retained signal in 3.1.1 or 5.0. So an MQTT source reaches `Synchronized` when its subscriptions are established, with retained values possibly still in flight, and a wait covering only MQTT sources can return while properties still show local defaults.

This is documented as a weaker guarantee rather than hidden by excluding MQTT sources from waits, because exclusion would make a tree-wide wait lie by omission: a user would get a completed wait and no indication that a whole protocol was skipped.

Raising QoS does not fix this. QoS gives per-message delivery assurance and per-topic ordering, neither of which is a completeness signal. The plausible improvement is a sentinel barrier (publish a unique message to a dedicated subscribed topic after subscribing and treat its arrival as "everything queued ahead of it has been delivered"), which relies on per-connection FIFO delivery across topics, a broker behaviour rather than a spec guarantee. That would upgrade MQTT from "subscriptions established" to "best-effort retained quiescence", still short of the explicit-read guarantee. If it is ever added, the hook already exists and needs no design change: `MqttSubjectClientSource.OnReconnectedAsync` subscribes and then calls `LoadInitialStateAndResumeAsync`, so the barrier is one awaited call inserted between them, behind an opt-in configuration flag. Tracked as a follow-up issue, deliberately not in this design.

### The source event stream

All source metadata changes are published as one typed event on a per-tree stream:

```csharp
public enum SourceEventKind
{
    SourceRegistered,
    SourceUnregistered,
    StateChanged,
    PropertyClaimed,        // real ownership transition: a source took the property
    PropertyReleased,       // real ownership transition: a source gave the property up
    PropertyEnteredView,    // synthetic: an already-claimed property joined the tree on attach
    PropertyLeftView        // synthetic: a still-claimed property left the tree on detach
}

public readonly record struct SourceEvent(
    SourceEventKind Kind,
    ISubjectSource Source,
    PropertyReference? Property,   // set for the four property kinds, otherwise null
    SourceState OldState,          // StateChanged: the source's previous state; property events: the property's previous effective state
    SourceState NewState,          // StateChanged: the source's new state; property events: the property's new effective state
    DateTimeOffset Timestamp)
{
    /// <summary>
    /// The authoritative state for this event's subject, read at access time rather than captured
    /// when the event was created. Use this to maintain a derived view. <see cref="OldState"/> and
    /// <see cref="NewState"/> describe one transition and must not be applied blindly, because events
    /// for the same property can be enqueued out of order. Not cached: each access performs a
    /// property-data lookup and a volatile read, so hoist it to a local if you read it more than once.
    /// </summary>
    /// <remarks>
    /// For <see cref="SourceEventKind.StateChanged"/> this is the SOURCE's state, not any property's.
    /// A consumer updating properties on a state change must call
    /// <c>property.GetSourceState()</c> per property instead.
    /// </remarks>
    public SourceState CurrentState { get; }
}
```

`CurrentState` resolves per kind: `Source.State` for the three source-level kinds, and for the four property kinds a **topology-aware ownership read**: `Unclaimed` if this event's monitor is no longer resolvable from `property.Subject.Context`, otherwise `Property.Value.GetSourceState()`.

The topology check is not an embellishment; without it the convergence guarantee fails under ordering. A claim can commit and capture its monitors, then the subject can detach and the scan emit `PropertyLeftView`, and only then is the delayed claim enqueued. Delivered last, a plain ownership read would return the still-owning source's state and permanently undo the release, because detach deliberately leaves the ownership data intact. The check makes `CurrentState` answer the question the stream is actually about, which is "what is this property's state *in this monitor's tree*", and it costs one `GetServices<SourceMonitor>()` call, cached per context state and normally of length 0 or 1, plus a reference scan.

`ContextInheritanceHandler` is what makes it work: it removes the parent fallback on the last detach (`change is { ReferenceCount: 0, IsPropertyReferenceRemoved: true }`), so the monitor genuinely stops being resolvable from a detached subtree. The event carries its originating monitor in an internal field for this comparison. One edge remains: a subject constructed directly against the tree root's context rather than inheriting it never loses resolution, so its properties keep reading through; such a subject is not detachable in the relevant sense, and this is documented rather than worked around.

With the topology check in place, `PropertyLeftView` needs no special-cased state. It falls out of the general rule, which is the sign the rule is the right one.

It is a property rather than a method to match the codebase idiom for live reads (`PropertyReference.Metadata`, `ISubjectSource.State`, and `SubjectSourceBase.PendingWriteCount` all do work on access). One consequence to document: the compiler-generated `ToString` of a record struct includes public properties, so logging an event evaluates `CurrentState`, and a log line can therefore show a `CurrentState` that disagrees with `NewState`. Equality and hashing are unaffected, since those use fields.

#### Why view events are a separate kind

The previous revision emitted `PropertyReleased` from the detach catch-up scan, and that was incoherent. The scan exists precisely for properties that are **still claimed** when their subject detaches, so the ownership data is intact and `GetSourceState()` returns the owning source's `Connecting` or `Synchronized`. The event said released while the authoritative read said claimed, so a consumer following the "apply `CurrentState`" rule never converged to the release, which is the exact non-convergence the rule was introduced to eliminate.

Removing the ownership data instead would be worse: the source has not released anything, and `ChangeQueueProcessor` filters outbound changes on `TryGetSource(...) == this`, so a subject that re-attached would silently stop sending that property to its source.

Splitting the kinds resolves it honestly. `PropertyClaimed` and `PropertyReleased` describe **ownership**, and their `CurrentState` is the ownership read. `PropertyEnteredView` and `PropertyLeftView` describe **tree membership**, which is what the catch-up scan actually observes, and `PropertyLeftView` resolves `CurrentState` to `Unclaimed` because a property outside the tree has no effective state within it. Consumers are not complicated by this: a per-property view maintainer handles all four kinds identically by applying `CurrentState`, which now returns the correct value in each case.

#### Why the transition fields must not be applied blindly

An ownership change is a compare-and-set on `Subject.Data`, and the event is enqueued afterwards. A thread that wins the compare-and-set can be preempted before it enqueues, so a causally later mutation can enqueue first. This is the same shape as the flush-ordering defect PR 399 fixes for property changes: enqueue order is a race order, not a commit order.

The consequence is specific. A claim followed by a release, delivered inverted, leaves a consumer that writes `NewState` blindly holding `Connecting` forever while the authoritative read says `Unclaimed`. That divergence never heals, which violates quiescent consistency.

`CurrentState` fixes it without any ordering machinery. Every mutation enqueues an event, and every handler invocation performs a fresh authoritative read, so the handler for the last enqueued event necessarily runs after the last mutation and writes the settled value. Whatever order events arrive in, the view converges once writes settle.

`OldState` and `NewState` remain accurate descriptions of their own transition and are useful for logging, tracing, and diagnostics. They are simply not a view-update primitive.

#### Delivery rules

- Delivery is asynchronous and serialized per subscriber. Emission from any thread enqueues the `SourceEvent` onto **each subscriber's own queue**; each subscription has an on-demand drain loop (single-flight on the thread pool, exits when the queue is empty, so there is no permanent task and no dispose lifecycle) that delivers to that one handler, outside all locks. This follows `PropertyChangeQueueSubscription` in `Namotion.Interceptor.Tracking`, where each subscription owns an isolated queue drained independently.
- Per-subscriber queues mean a slow handler delays only itself. They also remove a mechanism: a subscription's queue is created empty, so events enqueued before it existed cannot be in it, and no per-subscription sequence stamping is needed to filter them out.
- Each subscriber observes its own events in strict enqueue order. Different subscribers can observe different relative orders, because the per-queue enqueues of two concurrent emitters can interleave. Nothing depends on cross-subscriber agreement: no consumer coordinates with another consumer through the stream.
- Handlers may read and write the object graph, including writing registry attributes directly in the callback. Because delivery happens on the drain thread outside all internal locks, a handler write is an ordinary property write.
- Handlers should still be fast. Claim bursts are real (an OPC UA browse can claim thousands of properties in one connect cycle) and queue transiently as structs until drained; diagnostics UIs throttle or sample on their side.
- A throwing handler is caught and logged by its own drain loop; it breaks neither the other subscribers nor any emitting path.

This diverges from `IPropertyChangeObserver`, the other new delivery idiom in Tracking, which is synchronous on the writing thread and forbids handlers from throwing or blocking. That contract is right for property writes, which are the hot path, and wrong here: source events are emitted while the lifecycle lock is held (attach and detach catch-up, detach releases), so a synchronous contract would force every mutating consumer to build its own queue and dispatcher. The divergence is deliberate.

### Emission points

Each event kind is emitted at the lowest level that knows the truth:

- **`PropertyClaimed` / `PropertyReleased`: emitted by `SetSource` and `RemoveSource` themselves**, not by `SourceOwnershipManager`. The documented contract tells sources to claim by calling `SetSource(this)` directly, and the low-level ownership API is public, so emission at the primitive is the only way the stream stays trustworthy. Emission happens only on actual ownership transitions: an idempotent re-claim by the same source and a rejected claim (property owned by a different source) emit nothing, and `RemoveSource` emits only when it actually removed the ownership entry. To distinguish a fresh claim from a re-claim atomically, `PropertyReference` gains `TryAddPropertyData(key, value)`, the add-if-absent mirror of the existing `TryRemovePropertyData`; `SetSource` switches from `GetOrSetPropertyData` to it. `RemoveSource` already reports actual removal. Ownership events describe the property's effective-state transition, not the source's: `PropertyClaimed` carries `Unclaimed` to the owning source's state, `PropertyReleased` carries the state at release to `Unclaimed`.
- **`PropertyEnteredView` / `PropertyLeftView`: emitted by the monitor's attach and detach catch-up scan.** They report tree membership, not ownership, and never touch the ownership data.
- Monitors are resolved via `property.Subject.Context.GetServices<SourceMonitor>()` (service resolution walks fallback contexts) and the event is delivered to every resolved monitor. The result is a cached `ImmutableArray` (`InterceptorSubjectContext` caches per-type resolution on its copy-on-write state snapshot), usually of length 0 or 1; when empty, emission is a no-op, so plain contexts and benchmarks pay nothing.

  **Limitation, stated rather than papered over: a subject attached to more than one tree is only visible to the first tree's monitor.** `ContextInheritanceHandler` adds the parent context as a fallback only on the first attach (`change is { ReferenceCount: 1, IsContextAttach: true }`), so a second parent's context never enters the fallback chain and its monitor is not resolvable from the subject. Claims, view events, and waits therefore see one tree, not both. Iterating `GetServices` rather than requiring a single service is still correct and costs nothing, and it becomes right automatically if context inheritance is ever extended to track every parent, which is the subject of the in-flight `design/context-inheritance-parent-link` work. Until then, no multi-tree guarantee is claimed anywhere in this design.
- **`StateChanged`: raised by the source itself** (the pump in `SubjectSourceBase`). The monitor, subscribed since registration, forwards each event to the stream.
- **`SourceRegistered` / `SourceUnregistered`: emitted by the monitor** in `Register`/`Unregister` (called from `SubjectSourceBase.StartAsync` and dispose).
- `SourceOwnershipManager` has no eventing role. It remains a convenience for claimed-set bookkeeping and automatic release on subject detach; its releases go through `RemoveSource` and therefore appear on the stream like any other.

### SourceMonitor as an explicitly configured context service

The monitor lives in the `IInterceptorSubjectContext`, not in DI, and is added explicitly at context configuration time:

```csharp
public static IInterceptorSubjectContext WithSourceMonitoring(this IInterceptorSubjectContext context);
public static IInterceptorSubjectContext WithSourceMonitoring(this IInterceptorSubjectContext context, IServiceCollection services);
```

called on the tree root context alongside the other features. `WithSourceMonitoring` implies `WithParents()`, which the branch scope needs, in the same way `WithHostedServices` implies `WithLifecycle()`.

Automatic placement from inside a source cannot work: connectors receive the context of their own root subject, which can be a subtree context, and a service added there is invisible to the tree root and to sibling subtrees (context fallbacks point child to parent, never sideways). A subtree-rooted source would fragment the tree into per-subtree monitors, and a waiter on the root context would create yet another empty monitor and block forever. Explicit configuration puts the monitor at the scope the whole tree resolves.

The `IServiceCollection` overload additionally registers a hosted service whose only job is to call `CompleteSourceRegistration()` when `IHostApplicationLifetime.ApplicationStarted` fires. It follows the shape of the existing `WithHostedServices(IServiceCollection)`. It is order-independent: `Host.StartAsync` resolves `IEnumerable<IHostedService>` once, constructing every hosted service, and only then starts them in order, so a registration hold taken in the holder's constructor is in place before any source's `StartAsync` runs, regardless of registration order.

`SubjectSourceBase` overrides `StartAsync`: it resolves all monitors reachable from its context (`GetServices<SourceMonitor>()` walks the fallback chain upward, so a subtree-rooted source finds the root monitor), registers with each, and only then calls `base.StartAsync` to launch the pump. If no monitor is resolvable, the source runs untracked; existing applications and benchmarks that never call `WithSourceMonitoring()` keep working unchanged. On `Dispose` the source unregisters from the monitors it registered with. Because both DI-hosted and dynamically created sources start through the same hosted-service lifecycle, registration works for both.

Registering at start instead of at construction has three consequences:

- `SourceRegistered` always publishes a fully constructed source. No handler or `Sources` snapshot reader can observe a source whose derived constructor has not finished.
- A source that is constructed but never started stays invisible. It can never synchronize, so it must not hold a wait open.
- `Register` is idempotent (a re-register is a no-op that emits nothing). This is defensive rather than load-bearing, since `Stopped` is terminal and a stopped source is never started again.

Custom `ISubjectSource` implementations that do not derive from `SubjectSourceBase` register themselves with the resolvable monitors once fully constructed and started, and unregister on dispose.

Two robustness contracts: the monitor does not require lifecycle tracking (on a context without `WithLifecycle()` the attach and detach catch-up is disabled while registration, stream, and waits work normally; unlike `SourceOwnershipManager` it never throws for a missing lifecycle interceptor), and `Unregister` of a source that was never registered is a no-op.

```csharp
public class SourceMonitor
{
    IReadOnlyList<ISubjectSource> Sources { get; }              // snapshot for polling consumers
    SourceSubscription Subscribe(Action<SourceEvent> handler);  // typed event stream

    void Register(ISubjectSource source);                       // idempotent
    void Unregister(ISubjectSource source);

    void CompleteSourceRegistration();                          // idempotent
    IDisposable DeferWaitCompletion();

    Task WaitForSynchronizationAsync(IInterceptorSubject subject, CancellationToken cancellationToken = default);
}

public sealed class SourceSubscription : IDisposable
{
    ImmutableArray<ISubjectSource> Sources { get; }   // snapshot captured atomically with the subscription
}
```

On `Register` the monitor subscribes to the source's `StateChanged` event and forwards its events to the stream; on `Unregister` it unsubscribes. Registration precedes the pump start, so `SourceRegistered` precedes any forwarded `StateChanged` of that source. Subscription and registration are serialized by the monitor, and `Subscribe` captures the `Sources` snapshot atomically with the subscription under that serialization, returning it on the `SourceSubscription` handle. Reading `monitor.Sources` separately after subscribing is not race-free (a source registered between the two calls appears in both the snapshot and the stream, so a naive consumer double-counts it); the handle's snapshot is the correct baseline. The handle's snapshot plus its subsequently delivered events observe every change exactly once. The wait methods are internal stateful consumers of registration, unregistration, registration-hold changes, and state changes, notified synchronously at the emission points rather than through the delivery queues, so wait correctness does not depend on drain latency; they are tracked separately from public stream subscribers (see the catch-up cost guard below).

### The registration signal

The monitor is born holding one registration count. No wait can complete while the count is above zero, regardless of source states.

- `CompleteSourceRegistration()` releases the initial hold. It is idempotent, so a re-entrant loader guard (such as `RootManager.LoadAsync` returning early when `Root` is already set) is safe.
- `DeferWaitCompletion()` takes a further count for the duration of a later batch and releases it on dispose. Counts compose, so concurrent holders are fine.
- Taking a hold blocks pending waits but does not un-complete an already-completed task, preserving the "once completed, stays completed" rule.

The default is therefore fail-safe. An application that forgets to signal gets waits that hang, which is loud and diagnosable, rather than waits that complete early on a partially registered tree, which is silent and produces exactly the bug this design exists to prevent.

This replaces the `IHostApplicationLifetime.ApplicationStarted` guidance entirely, including its warning that awaiting the token inside `IHostedService.StartAsync` deadlocks the host. That guidance was only ever correct for applications whose sources are DI-registered hosted services, and the driving application is not one: HomeBlaze's `RootManager` deserializes the whole subject tree inside `ExecuteAsync`, so every source comes into existence after `ApplicationStarted` has already fired. `ApplicationStarted` survives only as the trigger the `IServiceCollection` overload uses internally.

Two ways to signal, neither requiring the other:

```csharp
// Hosted, via the overload
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithLifecycle()
    .WithSourceMonitoring(builder.Services)
    .WithHostedServices(builder.Services);
```

```csharp
// Explicit, no hosted service involved
var host = builder.Build();
await host.StartAsync();                                  // every source has started and registered
context.CompleteSourceRegistration();
await host.WaitForShutdownAsync();
```

#### The attach-driven start queue

The lifecycle callback that starts an attached hosted service is synchronous, runs on the graph-mutating thread under the lifecycle lock, and cannot await. `HostedServiceHandler.AttachHostedService` therefore posts the start onto a `BufferBlock` drained sequentially by the handler's own loop and returns. A source attached through the automatic lifecycle path is queued to start, not started. When a loader that builds the tree returns, its sources are attached but not yet registered, and signalling at that point races them.

This is not a narrow race. `PostStartService` awaits `Task.Delay(50)` before calling `StartAsync` (a deliberate delay carrying a TODO in the source), and the drain is strictly one action at a time, so twenty attached sources take at least a second to all register. Signalling immediately after the tree is built is close to guaranteed to be wrong rather than occasionally wrong.

`HostedServiceHandler` gains `WaitForPendingActionsAsync(CancellationToken)`. Because the drain is FIFO and sequential, a marker action posted after the queued starts is a barrier: when it runs, everything queued before it has completed. `SubjectSourceBase.StartAsync` registers with its monitors before launching the pump, so completion of the barrier implies registration.

The handler is `internal`, so applications reach the barrier through a public extension in `Namotion.Interceptor.Hosting`, resolving the handler that `WithHostedServices` already registered as a context service. It returns a completed task when no handler is configured, since the absence means nothing was ever queued:

```csharp
public static Task WaitForPendingHostedServiceActionsAsync(
    this IInterceptorSubjectContext context, CancellationToken cancellationToken = default);
```

Attach-driven applications await it before signalling:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await LoadAsync(stoppingToken);                                        // builds the tree, attaches sources
    await _context.WaitForPendingHostedServiceActionsAsync(stoppingToken); // their StartAsync has run
    _context.CompleteSourceRegistration();
}
```

This is the one part of the design that lands in `Namotion.Interceptor.Hosting`. It is one internal member plus one extension, it sits in the package that owns the queue, and it keeps the layering intact: Hosting does not learn about Connectors. It is a barrier for work **already queued**; subjects attaching afterwards post new actions it does not cover, which is the right semantics for a loader that has finished building its tree. If the handler's drain loop is not running, the marker never executes and the await does not complete; in practice the handler starts during host startup, before any tree building happens in `ExecuteAsync`, but the contract is documented rather than left as a trap.

### Branch-scoped waits

The wait is anchored on a subject, and the subject is the scope:

```csharp
public static Task WaitForSynchronizationAsync(this IInterceptorSubject subject, CancellationToken cancellationToken = default);
```

A source is **in scope** of subject `T` when its `RootSubject` and `T` are on the same root-to-leaf path, that is when `RootSubject` is an ancestor of `T` or `T` itself (the source is rooted above and may claim into the branch), or when `RootSubject` is a descendant of `T` (the source is rooted inside the branch). A source rooted on a sibling branch is in neither set and is excluded.

Both tests are the same helper with its arguments swapped: `IsAncestorOrSelf(candidate, target)` walks up from `target` through `subject.GetParents()` (from `WithParents()` in Tracking) with a visited set, since the parent graph is a multi-parent DAG. Ancestor chains are shallow and the walk runs only on wait start and on wait notifications, which are rare. A subject that is not attached has no parents, so an unattached anchor matches nothing and the wait blocks, which is correct.

Waiting on the tree root is not a special case: every source in the tree is rooted at the root or below it, so "the whole tree" is simply the root anchor. This replaces three APIs from the previous revision (the no-argument wait, the `Func<ISubjectSource, bool>` filter overload, and the proposed property-scoped wait) with one method.

The wait completes when all of the following hold:

1. the monitor's registration count is zero,
2. at least one in-scope source is currently registered, and
3. every currently registered, non-`Stopped` in-scope source is `Synchronized`.

If the only registered in-scope sources are `Stopped`, condition 3 is vacuously met and the wait completes.

- Implementation is a re-evaluation over a `TaskCompletionSource` (created with `RunContinuationsAsynchronously`), triggered by registration, unregistration, registration-count changes, state changes, **and subject attach or detach**.
- The topology trigger is not optional. Branch scope is computed from `GetParents()`, which is mutable: reparenting a wait's anchor, or a source's `RootSubject`, changes which sources are in scope without any registration or state transition occurring. Without a topology trigger a pending wait's condition can become satisfied, or become unsatisfiable, while its `TaskCompletionSource` is never re-evaluated, leaving it blocked indefinitely. The monitor already hooks lifecycle for the catch-up scan, so this reuses an existing subscription rather than adding one. Unlike the catch-up scan, this trigger is **not** gated on having stream subscribers, because a wait is exactly the consumer that has none.
- A source registered while someone is waiting is included if it is in scope; a source that drops back to `Connecting` before the condition is met re-blocks the wait.
- Once the returned task completes it stays completed. Callers who care about later disconnects subscribe to the stream or call the wait again.
- `Stopped` is terminal, so the vacuous-completion rule has a sharp consequence worth stating rather than burying. A branch whose in-scope sources have all stopped completes **immediately and permanently**, and no restart will ever revive them. Detaching a subject stops its attached source and removes it from the attached list, so a re-attached subject is served by a *new* source instance that must register on its own; until it does, the branch may consist only of stopped sources and the wait completes vacuously on a branch that is not live. The wait logs a warning when it completes with no non-`Stopped` in-scope source, for the same reason it warns on an empty scope: silence here reads as success. Applications that tear down and rebuild a branch hold `DeferWaitCompletion()` across the rebuild.
- Connection loss needs no such care, because the source transitions straight back to `Connecting` and re-blocks the wait.
- An empty in-scope set does not complete the wait. This keeps the conservative default: "no source has registered here yet" and "no source will ever claim here" are not distinguishable, and completing on the second reading would return default values, which is the original bug. Because the registration count gives a definite moment, a wait that still has an empty in-scope set once the count reaches zero logs a warning naming the anchor subject, so a misconfigured branch surfaces instead of hanging silently.

`WaitForSynchronizationAsync` resolves every monitor reachable from `subject.Context` and waits on all of them, so there is no ambiguity to throw on. In practice more than one monitor is reachable only when one is configured locally and another on a shared ancestor context. It is **not** reachable through a second parent: as noted under emission, `ContextInheritanceHandler` records only the first attach, so a subject in two trees resolves the first tree's monitor alone. The design claims no multi-tree coverage.

### Monitor access

Consumers never need the monitor for waiting. It is resolved for diagnostics and for the registration signal:

```csharp
public static SourceMonitor GetSourceMonitor(this IInterceptorSubjectContext context);
public static void CompleteSourceRegistration(this IInterceptorSubjectContext context);
public static IDisposable DeferWaitCompletion(this IInterceptorSubjectContext context);
```

The two convenience extensions iterate `GetServices<SourceMonitor>()` and apply to **every reachable monitor**. `GetSourceMonitor()` has to return one object, so it throws when several are resolvable and directs the caller to `GetServices<SourceMonitor>()`. The rule is: extensions act on every reachable monitor, `GetSourceMonitor()` demands exactly one.

All of them, including `WaitForSynchronizationAsync`, throw `InvalidOperationException` with guidance to call `WithSourceMonitoring()` on the tree root context when no monitor is reachable. A missing monitor would otherwise surface as a silent forever-block or a silent no-op.

Dynamic sources that do not exist yet at call time cannot be known by any mechanism. Applications that create sources dynamically hold `DeferWaitCompletion()` across the creation, or signal after it.

### View catch-up on subject attach and detach

The stream's contract is "the tree's view of ownership", not "every `SetSource` call anywhere". Two lifecycle situations would otherwise punch holes in that contract: a property claimed while its subject was not yet attached to the tree (no monitor reachable, nothing emitted) whose subject is attached later, and a claimed subject detaching from the tree without its claims being released (possible for sources that bypass `SourceOwnershipManager`).

The monitor therefore hooks the existing lifecycle infrastructure:

- **On subject attach**: scan the subject's properties and emit `PropertyEnteredView` for any that arrive already claimed. `CurrentState` reads through to the owning source, which is correct, because the property is genuinely claimed and now genuinely in the tree.
- **On subject detach**: emit `PropertyLeftView` for any still claimed. The scan does not remove the ownership data, because the source has not released anything and `ChangeQueueProcessor` filters outbound changes on `TryGetSource(...) == this`; clearing it would silently stop a re-attached subject from reaching its source. `CurrentState` resolves to `Unclaimed` because the monitor is no longer resolvable from the detached subject's context, so a view maintainer converges even though a plain ownership read would still report the source. Releases performed by `SourceOwnershipManager` during detach go through `RemoveSource` and are real `PropertyReleased` events; the scan then finds nothing, so there are no duplicates.

Cost guard: when the stream has no public subscribers, the scan is skipped entirely, so the recently optimized attach and detach hot paths pay one subscriber-count check and nothing else. With subscribers, the scan only enqueues events while the lifecycle lock is held; handler code never runs under the lock. Pending waits deliberately do not count as subscribers: the wait methods consume monitor notifications through internal bookkeeping rather than a stream subscription, because a wait is typically active during startup, exactly when subject attach storms happen, and waits never need property claim events. This invariant is what ruled out the property-scoped wait considered in the alternatives below. Built-in connectors always claim after attach, so the missed-claim window is theoretical today; the catch-up exists to give the stream a consistency contract subscribers can rely on without reasoning about holes.

The scan touches `PropertyReference.Metadata`, which is no longer cached on the struct (it looks up the subject's property table on each access), so the scan hoists metadata to a local where it reads it more than once.

### Per-property API

A single read-only extension on `PropertyReference` in `Namotion.Interceptor.Connectors`:

```csharp
public static SourceState GetSourceState(this PropertyReference property)
```

Derivation: `TryGetSource` returns nothing gives `Unclaimed`; otherwise the owning source's `State`. Computed on read from existing attached data, with no storage and no allocations. For richer diagnostics (`LastSynchronizedAt`, `PendingWriteCount`) the caller uses `TryGetSource` and reads the source directly. `SourceEvent.CurrentState` is this same read, reached from an event.

The result is only fully meaningful after the branch wait has completed: claiming happens when a source connects, so before that point `Unclaimed` is ambiguous ("not claimed yet" versus "never claimed"). Once a property is claimed, `GetSourceState()` returns `Connecting` instead of `Unclaimed`, so even before the wait completes a diagnostics view can distinguish "will sync, still loading" from "no source, so far".

Per-property change observation needs no dedicated API: a property's effective state changes exactly when its ownership changes (`PropertyClaimed`/`PropertyReleased`), when it enters or leaves the tree (`PropertyEnteredView`/`PropertyLeftView`), or when its owning source transitions (`StateChanged` for that source). All are on the stream; consumers filter. For the first four, apply `CurrentState` directly; for `StateChanged`, call `GetSourceState()` per affected property.

### Consumer protocol

```csharp
// Wait until every source that can claim under this branch has finished subscribe-read-replay.
await root.Plants["LineA"].WaitForSynchronizationAsync(cancellationToken);

// Now well-defined per property, for properties under that branch:
//   Synchronized = the owning source completed its initial load (see the per-protocol
//                  section: an explicit read for OPC UA and WebSocket, subscriptions
//                  established for MQTT, where retained values may still be arriving)
//   Unclaimed    = local-only property that was never claimed
```

That is the whole protocol. There is no phase 0 and no second wait. The per-property answers become well-defined only after the wait, which is what resolves the problem that it is not predictable which properties a source will claim. Consumers interested in a single property use the same protocol, anchored on that property's subject.

Waiting on the tree root means the whole tree:

```csharp
await root.WaitForSynchronizationAsync(cancellationToken);
```

### Driving application use case: availability attributes

The concrete consumer this design must serve well, and the scenario that motivated the queued delivery model: an application declares a stored `ConnectionState` registry attribute (of type `SourceState`) on selected properties and a derived `IsAvailable` attribute computing `ConnectionState == SourceState.Synchronized`. A small updater component subscribes to the monitor stream once and maintains `ConnectionState`; the existing derived-property infrastructure propagates `IsAvailable` changes to UIs and GraphQL automatically. The registry already supports both halves (`AddAttribute` or `[PropertyAttribute]` for the stored attribute, `AddDerivedAttribute` or `[Derived]` for the derived one).

The updater follows the stream contracts:

- Because delivery is asynchronous, serialized per subscriber, and outside all locks, the updater writes `ConnectionState` directly in the callback; it needs no queue or dispatcher of its own. `ConnectionState` is eventually consistent, which is acceptable for its purpose.
- It writes `event.CurrentState`, never `event.NewState`. That is what makes the view quiescently consistent under out-of-order enqueues.
- The stream does not replay, so a late-starting updater bootstraps by subscribing first and then initializing from a registry walk using `GetSourceState()`. Events racing the walk re-apply idempotent writes, so the view self-heals.
- On the four property kinds it updates the single property from `CurrentState`.
- On `StateChanged` it updates the affected properties using a small per-source index it maintains as a side effect of the property events it already handles, seeded by the bootstrap walk. **It must call `property.GetSourceState()` for each indexed entry, not `event.CurrentState`**, because on a `StateChanged` event `CurrentState` is the source's state and says nothing about any individual property. With the per-property read, the index only has to be a **superset**: a stale entry left by an inverted claim and release, or by a property since reassigned to another source, causes one wasted iteration that writes that property's correct current state rather than the transitioning source's. Reading `event.CurrentState` in that loop instead would write the old source's state onto a released or reassigned property, with no later event guaranteed to repair it. Serialized delivery makes the index single-writer; only the bootstrap walk shares a small uncontended lock with the handler.

The costs are opt-in and scoped: a permanently subscribed updater keeps the attach and detach catch-up scan active, each source transition costs one write per attributed property of that source, and the attributes are real registry members visible to other connectors unless filtered. These are exactly the costs the rejected built-in `@syncState` attribute would have imposed on every claimed property of every application; here the application pays them only where it declares the attributes.

### Diagnostics

- A dashboard subscribes to the stream once and starts from the subscription's snapshot: source list with `State`, `LastSynchronizedAt`, and `PendingWriteCount` (all on the `ISubjectSource` surface), claim and release activity as it happens, and per-property state by filtering events for the properties on screen.
- Notification granularity is proportionate: state transitions are per source (all properties of a source flip together), claims and releases are per property but occur only on connect cycles and topology changes.
- A dashboard rendering "what is the state now" uses `CurrentState`. A dashboard rendering a transition log uses `OldState` and `NewState`, and must accept that the log is not a faithful ordering of same-property transitions.

### Edge cases

- A property claimed while its source is still loading reports `Connecting`.
- A stopped source that still owns properties reports `Stopped` per property, meaning "will not synchronize again while stopped".
- Ownership release on subject detach (existing `SourceOwnershipManager` behavior) reverts properties to `Unclaimed` and appears on the stream as `PropertyReleased`. A source that bypasses the manager leaves ownership intact, and the detach scan reports `PropertyLeftView` instead; the property still reads as claimed through `GetSourceState()`, which is accurate, while `CurrentState` on that event reports `Unclaimed`, which is the tree's view.
- A connector reconnect that never leaves `ProcessAsync` still reports correctly, because `StartBuffering` and `LoadInitialStateAndResumeAsync` drive the transitions rather than the pump's catch block.
- An MQTT source reports `Synchronized` once its subscriptions are established, which is weaker than the other connectors; see the per-protocol section above.
- Disposing a source without stopping it first still publishes `Stopped`: the disposal fallback transitions and raises before unregistering, so the stream sees `StateChanged` to `Stopped` followed by `SourceUnregistered`.
- Connectors release ownership before unregistering: their dispose path runs `SourceOwnershipManager.Dispose` (releasing all claims through `RemoveSource`) before calling the base dispose, so `PropertyReleased` events precede `SourceUnregistered` on the stream. `OpcUaSubjectClientSource.DisposeAsync` already follows this order (`_ownership.Dispose()` then `Dispose()`); it is the documented convention for connector dispose implementations.
- A stopped source stays stopped. `BackgroundService` is not restartable and `HostedServiceHandler` drops it from the subject's attached list on detach, so a re-attached subject is served by a new source instance that registers on its own.
- A source that is constructed but never started is not registered: it does not appear in `Sources` and does not affect any wait. Disposing it raises `Stopped` on the source's own `StateChanged` event only; no monitor is involved and nothing appears on any stream.
- Claiming a property of a subject that is not attached to any tree with a monitor emits no event at claim time (the monitor resolution finds no service); the property enters the stream as `PropertyEnteredView` when the subject joins a tree. This is documented on `SetSource`.
- Waiting on a subject that is not attached to the tree matches no source (it has no parents), so the wait blocks and warns once the registration count reaches zero.

## Delivery

One pull request implements the whole design, based on master. It adds `docs/connectors-source-monitoring.md` as the permanent documentation (following the existing `connectors-` feature-page convention, linked from `docs/connectors.md`), including the availability-attributes scenario above as a worked sample, and deletes this spec at the end, once the design is implemented.

Coordination with in-flight work:

- **PR 399** (per-subject commit revision) touches neither `SubjectSourceBase`, `ISubjectSource`, `SourcePropertyExtensions`, nor `SourceOwnershipManager`, so nothing structural conflicts. Three mechanical collisions: both edit `docs/connectors.md` (different sections), both edit `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt` (regenerate, do not hand-merge), and both touch `SubjectSourceBaseTests.cs` and `ChangeQueueProcessorTests.cs`. PR 399 is the larger change and lands first; this PR rebases onto it.
- **PR 399 phase 2** (`feature/ordered-delivery`) adds ordered exactly-once delivery for property changes. It does not cover source metadata. The delivery divergences here (per-subscriber queues, notification rather than ledger) are deliberate and recorded as such above, so they are not mistaken later for ignorance of that machinery.

The PR also adds `SubjectPropertyChange.GetCurrentValue<T>()` next to `GetOldValue<T>()` and `GetNewValue<T>()`, the property-change mirror of `SourceEvent.CurrentState`. It is a method rather than a property because its siblings are generic for typed unboxing. The core already instructs consumers to do exactly this by hand: `IPropertyChangeObserver` says "Deliveries may arrive out of commit order under concurrent writes to the same property; re-read the property if you need the current value", and `PropertyChangeSubscriptionExtensions` repeats it, which today means hand-writing `property.Metadata.GetValue?.Invoke(property.Subject)`. Shipping the two `Current` accessors together keeps one discoverable idiom across both streams rather than two conventions a release apart. It is included here as a deliberate exception to the rule that a PR adds no public API it does not itself call: the caller is the documentation, and the symmetry is the point. Sequence it after the rebase onto PR 399, which rewrites that type.

## Affected Code

- `Namotion.Interceptor` (core): `PropertyReference.TryAddPropertyData(key, value)`, the atomic add-if-absent counterpart to `TryRemovePropertyData`; the core public API snapshot updates accordingly.
- `Namotion.Interceptor.Connectors`: `SourceState`, `SourceEventKind` (seven kinds, including the two view kinds), `SourceEvent` (including `CurrentState`), `ISubjectSource` members (`State`, `LastSynchronizedAt`, `PendingWriteCount`, `StateChanged`), `SubjectPropertyWriter`-driven transitions on `StartBuffering` and `LoadInitialStateAndResumeAsync` plus the internal transition interface it calls, `SubjectSourceBase` lifecycle transitions (pump entry, catch, finally, disposal fallback) serialized under a per-source lock, and `StartAsync` registration, `SourceMonitor` and `SourceSubscription` (including the lifecycle attach and detach catch-up, per-subscriber delivery queues, the registration count, and internal wait bookkeeping), the `WithSourceMonitoring()` and `WithSourceMonitoring(IServiceCollection)` context extensions and the hosted registration-hold service, the `GetSourceMonitor()`, `CompleteSourceRegistration()` and `DeferWaitCompletion()` context extensions, the `WaitForSynchronizationAsync` subject extension and its branch-scope helper, `GetSourceState` property extension, emission logic in `SetSource`/`RemoveSource` (built on `TryAddPropertyData`).
- `Namotion.Interceptor.Tracking`: `SubjectPropertyChange.GetCurrentValue<T>()`, the property-change mirror of `SourceEvent.CurrentState`; the Tracking public API snapshot updates accordingly.
- `Namotion.Interceptor.Hosting`: `HostedServiceHandler.WaitForPendingActionsAsync(CancellationToken)` (internal, a marker action posted onto the existing `BufferBlock`) and the public `WaitForPendingHostedServiceActionsAsync` context extension that reaches it; the Hosting public API snapshot updates accordingly.
- Built-in connectors (OPC UA, MQTT, WebSocket): no source changes expected; they inherit from `SubjectSourceBase` and claim through the existing paths.
- Test doubles that implement `ISubjectSource` directly (for example `ConcurrentTestSource` and `BlockingTestSource` in `Namotion.Interceptor.Connectors.Tests`) gain the four new members.
- Public API snapshot tests: `ISubjectSource` changes will fail `VerifyChecksTests.PublicApi` in affected projects; the new `.verified.txt` snapshots are accepted as part of the change.
Documentation spans four files, because the API spans four packages:

- **`docs/connectors-source-monitoring.md`** (new): the feature page, named to match `WithSourceMonitoring` and `SourceMonitor` and following the existing `connectors-<topic>.md` convention. Section order is deliberately usage first, mechanism second, so a reader who only wants to wait for a live tree stops after the first two sections:
  1. *Getting started (DI)*: the smallest complete example. Context recipe with `WithSourceMonitoring(builder.Services)`, a source registration, and a worker that awaits `root.WaitForSynchronizationAsync(ct)`. No `SourceMonitor` object appears anywhere in it, and no registration signalling, because the overload handles it.
  2. *Waiting on part of the tree*: one line changing the anchor from `root` to `root.Plants["LineA"]`, plus a short paragraph on what "in scope" means and that a sibling branch's failing source does not block.
  3. *Reading per-property state*: `GetSourceState()` and what each value means after the wait.
  4. *Applications that create sources at runtime*: the advanced scenario. Parameterless `WithSourceMonitoring()`, then the three-line `ExecuteAsync` (load, await `WaitForPendingHostedServiceActionsAsync`, `CompleteSourceRegistration`), with a short explanation of why the barrier is there. `DeferWaitCompletion()` for later batches.
  5. *Observing changes*: `ISubjectSource.StateChanged` for a held source, the monitor stream for aggregate consumers, and the `CurrentState` versus `NewState` rule stated as a rule with a one-line reason.
  6. *The state model and transitions*, *delivery contract*, and the breaking-change note for custom `ISubjectSource` implementers.
  7. *Worked sample*: the availability attributes (`ConnectionState`, derived `IsAvailable`, updater with its per-source index writing `CurrentState`).

  Both examples stay minimal. The simple one is two code blocks and shows no infrastructure type; the advanced one is three statements. Anything longer belongs in the worked sample at the end.
- **`docs/connectors.md`**: a short linking section under Sources, and `WithSourceMonitoring()` added to the context recipe.
- **`docs/hosting.md`**: `WaitForPendingHostedServiceActionsAsync`, documented as a barrier over the attach queue in the package that owns that queue, cross-linked from the source monitoring page rather than duplicated.
- **`docs/tracking.md`**: `GetCurrentValue<T>()`, placed next to the existing advice in Delivery Guarantees that tells consumers to re-read the property, since it is the API that advice has been missing.

## Testing

In `Namotion.Interceptor.Connectors.Tests`, following the `When<Condition>_Then<ExpectedBehavior>` naming and event-based synchronization (no hardcoded waits):

- Pump transitions via a fake source: `Connecting` initially; `Synchronized` after initial load; back to `Connecting` on pump failure; `Stopped` on cancellation; `LastSynchronizedAt` set exactly on entering `Synchronized` and preserved through a later disconnect; `Stopped` is terminal, so a source that has stopped never transitions again, and a stop followed by another `StartAsync` on the same instance neither transitions nor synchronizes.
- Sticky `Stopped`: after the disposal fallback set `Stopped`, a late pump transition (catch-path `Connecting` or in-flight `Synchronized`) neither overwrites the state, nor emits an event, nor updates `LastSynchronizedAt`.
- Registration lifecycle: `StartAsync` registers the source and `SourceRegistered` precedes any `StateChanged` of that source on the stream; `Register` is idempotent (a repeated register emits no duplicate `SourceRegistered`); a constructed but never started source is not registered and does not affect any wait; disposing without stopping emits `Stopped` before `SourceUnregistered`; disposing a never-started source does not throw and emits nothing to the stream; `Unregister` of a never-registered source is a no-op; after unregistration the monitor no longer forwards the source's events.
- Monitor robustness: a monitor on a context without lifecycle tracking supports registration, stream, and waits without throwing (catch-up disabled); a source started on a context without any monitor runs untracked and does not throw; `GetSourceMonitor()` on a context without a monitor throws `InvalidOperationException` with configuration guidance; with monitors on both a local and an ancestor context it throws, while `CompleteSourceRegistration()` and `WaitForSynchronizationAsync` apply to both. A characterization test pins the documented multi-tree limitation: a subject attached to a second tree resolves only the first tree's monitor, so it fails loudly if `ContextInheritanceHandler` ever starts tracking every parent.
- `TryAddPropertyData` (core): returns true and stores the value when the key is absent; returns false and leaves the existing value untouched when present.
- Attach and detach catch-up: attaching a subject with already-claimed properties emits `PropertyEnteredView` for each, with `CurrentState` reading through to the owning source; detaching a subject with still-claimed properties emits `PropertyLeftView`, whose `CurrentState` is `Unclaimed` even though `GetSourceState()` still reports the source, and whose emission leaves the ownership data intact so a re-attached subject still routes changes to that source; releases performed by `SourceOwnershipManager` during detach produce `PropertyReleased` and no duplicate view event; without public stream subscribers the catch-up scan is skipped; a pending wait alone does not trigger the scan but is still re-evaluated on attach and detach.
- Claim and release emission: a fresh claim emits `PropertyClaimed`; an idempotent re-claim by the same source emits nothing; a rejected claim (owned by another source) emits nothing; `RemoveSource` emits `PropertyReleased` only on actual removal; claims without a monitor in the context emit nothing and do not throw.
- Handler robustness: a throwing subscriber is logged by its own drain loop and breaks neither the other subscribers nor any emitting path; a slow subscriber does not delay delivery to another subscriber; a handler that writes a registry attribute directly in the callback succeeds, including for events originating from the attach and detach catch-up (no deadlock, no reentrancy failure).
- Subscription consistency: the `SourceSubscription` snapshot plus the delivered events observe every change exactly once, including a source registering concurrently with `Subscribe`; events enqueued before a subscription existed are not delivered to it; per-subscriber delivery order matches that subscriber's enqueue order.
- Event payloads: `PropertyClaimed` carries `Unclaimed` to the owning source's state; `PropertyReleased` carries the state at release to `Unclaimed`, including for releases performed during detach; `CurrentState` returns the authoritative state at access time and can differ from `NewState` when a later mutation has already happened; `CurrentState` on a `StateChanged` event reflects the source's current state and not any property's; `CurrentState` on `PropertyLeftView` is `Unclaimed` while `GetSourceState()` on the same property still reports the owning source.
- Transitions from connector-owned reconnects: a fake source that calls `StartBuffering` and `LoadInitialStateAndResumeAsync` without the pump leaving `ProcessAsync` transitions `Synchronized` to `Connecting` and back, emitting both events; the "already replayed by a concurrent reconnection" early return still reports `Synchronized`; a load that throws does not transition and the exception still propagates; a custom `ISubjectSource` constructing its own `SubjectPropertyWriter` gets no transitions.
- Transition atomicity: a transition interleaved with disposal never publishes `Synchronized` after `Stopped`, never updates `LastSynchronizedAt` after a `Stopped` transition has been published, and the monitor receives every published transition of a source it is still subscribed to.
- Terminal `Stopped`: no transition leaves `Stopped`, including a second `StartAsync` on the same instance; a wait whose in-scope sources are all `Stopped` completes and logs a warning.
- Topology-aware `CurrentState`: a claim event delivered after its subject detached reports `Unclaimed`, not the still-owning source's state, so a `PropertyLeftView` cannot be undone by a late claim; the same event reports the source's state while the subject is still attached.
- Out-of-band loss detection: a source that reports a connection loss without buffering transitions to `Connecting`, and the OPC UA, MQTT and WebSocket integration suites each induce an outage through `IFaultInjectable` and assert `Connecting` for its duration and `Synchronized` afterwards.
- Availability updater: on `StateChanged` the indexed loop writes each property's own `GetSourceState()`, so a stale index entry for a property since released or reassigned is written with its correct current state rather than the transitioning source's.
- Registration signal: no wait completes while the initial count is held; `CompleteSourceRegistration()` releases it and is idempotent; `DeferWaitCompletion()` re-blocks pending waits and composes when nested; taking a hold does not un-complete an already-completed wait; a source that registers and synchronizes before the signal does not complete a wait early.
- Branch scope: a source rooted at an ancestor of the anchor is in scope; a source rooted at a descendant is in scope; a source rooted on a sibling branch is excluded and its state never affects the wait; a multi-parent subject is in scope of sources on either parent path; an unattached anchor matches nothing.
- Wait semantics: completes only when all non-`Stopped` in-scope sources are `Synchronized`; the root anchor covers every source in the tree; an empty in-scope set blocks and warns once the registration count reaches zero; a source registered mid-wait is included when in scope and re-blocks the wait when `Connecting`; cancellation propagates.
- Per-property: `Unclaimed` for a property no source has claimed, `Connecting` for claimed while loading, `Synchronized` for claimed after load, `Stopped` for a claimed property of a stopped source.

In `Namotion.Interceptor.Tracking.Tests`: `GetCurrentValue<T>()` returns the property's present value, equals `GetNewValue<T>()` when nothing has been written since the change, and reflects a later write rather than the captured one.

In `Namotion.Interceptor.Hosting.Tests`: `WaitForPendingHostedServiceActionsAsync` completes only after the attach actions queued before it have run, including their `Task.Delay(50)`; completes promptly when the queue is empty; completes immediately on a context without a `HostedServiceHandler`; and does not wait for actions queued after it was called.

## Alternatives Considered

- **Property-scoped wait (`WaitForPropertiesSynchronizedAsync(IEnumerable<PropertyReference>)`)**: proposed on PR 354 to avoid head-of-line blocking behind an unrelated failing source. Rejected in favor of the branch-scoped wait, which serves the same use case without its two defects. Its rule for unclaimed properties ("satisfied once the tree-wide wait has completed") degrades the whole method into the tree-wide wait as soon as the caller's set contains one local-only property, silently reintroducing the blocking it exists to remove; a display name or a configured threshold in a reconciliation set is enough. And it must consume claim and release events, which would make waits stream subscribers and turn the attach and detach catch-up scan on during startup, exactly the window the cost guard was built for. The branch-scoped wait answers a purely topological question that is knowable before any connection exists, so neither problem arises. Additive later if a consumer ever holds a flat cross-branch path list with no anchor subject to name.
- **Subtree scoping through the existing `Func<ISubjectSource, bool>` filter**: cannot express the case. The filter scopes downward, but a source rooted at an ancestor of the target branch is the common shape (`a => b => c => d`, source at `a`, consumer interested in `c`), and the caller would have to walk upward through a multi-parent DAG to find it. More fundamentally, `RootSubject` is configured scope, not ownership: a source rooted at `a` may claim only part of `c` or nothing there, while a second source claims the rest, so any caller-side approximation is wrong in both directions, including the dangerous one (missing a source that does claim there, so the wait completes early). Replaced by scope resolution inside the library.
- **`IHostApplicationLifetime.ApplicationStarted` as the registration barrier (phase 0)**: only correct when every source is a DI-registered hosted service. HomeBlaze's `RootManager` deserializes the entire tree inside `ExecuteAsync`, so all its sources appear after `ApplicationStarted` has fired; a waiter woken by it observes a partially registered set and can complete after the first device synchronizes while twenty more are still attaching. It also carried a deadlock footgun (awaiting the token inside `StartAsync` hangs the host). Replaced by the explicit registration count, which is fail-safe rather than fail-silent and works for both models. `ApplicationStarted` survives as an implementation detail of the `IServiceCollection` overload.
- **An `IDisposable` gate with no initial hold (open a gate before creating sources)**: the same primitive with the opposite default. An application that forgets it gets silent premature completion, and the gate has to be opened before any source starts, which is an ordering hazard. Rejected for the initial-hold counter, where forgetting produces a hanging wait: loud, diagnosable, and the safe direction.
- **`subject.GetSourceMonitor().WaitForSourcesSynchronizedAsync(ct)`**: reads as branch-scoped but is not. Resolution walks the fallback chain, so a monitor resolved from any subject is the same tree-level object and cannot know the subject it was reached from. Rejected as a trap; the scope has to be an argument or the anchor of the method itself.
- **Single monitor-owned delivery queue with one drain loop for all subscribers** (the previous revision's design, justified by the now-deleted `PropertyChangeQueue`): one slow subscriber stalls every other subscriber, and it needs per-subscription sequence stamping so a new subscriber is not delivered events enqueued before it existed. At the realistic subscriber count of 0 or 1 it is exactly as cheap as per-subscriber queues; at two or more it saves one struct enqueue per event per extra subscriber, once per connect cycle, which is not worth the coupling. Per-subscriber queues follow the surviving core idiom (`PropertyChangeQueueSubscription`) and delete the sequence stamping outright.
- **Consumer-drained subscriptions (`TryDequeue` on the handle, as `PropertyChangeQueueSubscription` does)**: maximal isolation and no monitor-owned drain, but every consumer becomes a loop-running background service. Too much ceremony for the driving availability updater, which is a few lines as a callback. Rejected in favor of per-subscriber queues drained by the monitor, which keeps callback ergonomics and the isolation.
- **Making `NewState` safe to apply blindly by ordering emission with mutation**: requires a lock spanning the ownership compare-and-set and the enqueue, nesting a new lock inside the lifecycle lock and the public claim primitives, with a lock-ordering hazard, on a path that claims thousands of properties per connect cycle. Rejected. Note that ordered *delivery* does not help: per-subscriber FIFO already holds, and the disorder happens upstream of the queue, between the mutation committing and the event being enqueued.
- **Versioned ownership entries (store `record OwnershipEntry(ISubjectSource? Source, long Version)` and transition with `ConcurrentDictionary.TryUpdate`, leaving a tombstone on release)**: this does work, and lock-free. Because version and source live in one object swapped by one compare-and-swap, the increment is atomic with the mutation, so versions form a per-property total order matching mutation order and the consumer can drop superseded events, exactly as `DeliveredRevisionFilter` does for property changes in PR 399. Drawing the stamp before or after the compare-and-swap does not work: a thread that draws a stamp, is preempted, and then wins a later compare-and-swap carries a lower stamp than a causally earlier mutation. Deferred rather than rejected. It costs one small allocation per claim or release on a path that currently allocates nothing (roughly 160 KB of gen0 for a 5,000-property browse, once per connect cycle), one extra dereference in `TryGetSource` on the hot outbound path, an equivalent change to the source state field, and a filtering burden on every view-maintaining consumer. What it buys is a capability, not correctness: `CurrentState` already delivers quiescent consistency, so the only gain is a true per-property ledger, which no consumer has asked for. It is cleanly additive, since handlers written against `CurrentState` keep working unchanged.
- Observable `@syncState` dynamic registry attribute per claimed property: fully per-property observable through existing change tracking, but O(claimed properties) writes and allocations per transition, wire noise into other connectors, and attribute lifetime coupled to claim and release. Rejected; filtering the event stream provides per-property observability without any of these costs.
- Per-source events as the sole mechanism (no aggregated stream): forces aggregate consumers into per-source bookkeeping with an inherent race between enumerating sources and subscribing to each one. The final design keeps the per-source `StateChanged` event as the notification primitive feeding the monitor, while the stream is the aggregate consumption surface with snapshot-plus-subscribe consistency.
- A plain .NET event or `IObservable<SourceEvent>` instead of the monitor's `Subscribe` method: both fight the stream's contracts. Per-handler exception isolation over a multicast event requires `GetInvocationList()`, which allocates on every emission on claim-burst paths, and a throwing Rx observer propagates into the emitter. The subscribe-then-snapshot consistency contract requires subscription to be serialized with registration under the monitor's lock, which compiler-generated event accessors cannot do. The plain event stays where it fits: `ISubjectSource.StateChanged` is single-source, low-rate, and implementer-facing. An `IObservable<SourceEvent>` adapter can be added additively later.
- A per-source claimed-properties query (on `ISubjectSource` or as a monitor-side claim ledger): would slightly simplify consumers like the availability updater, but the updater already knows its attributed properties, sources that bypass `SourceOwnershipManager` could not answer the query, and a monitor-side ledger would reintroduce per-property storage. Deferred; additive if a real consumer needs it.
- Connector-maintained state attributes (the ownership layer writes an app-declared `ConnectionState` attribute directly at claim, release, and state transitions, skipping the monitor and stream entirely): dramatically less machinery for the availability use case and synchronously consistent, but it makes the connector layer opinionated about app-model conventions (attribute discovery, shape, and release semantics are consumer-specific), and it serves exactly one consumer pattern while removing the general surface every other consumer would need. Rejected: ownership primitives stay unopinionated, consumer-specific logic rides the stream, and a convenience package built on the stream can ship later if the pattern proves common.
- Synchronous handler delivery on the emitting thread (the `IPropertyChangeObserver` contract): emissions can occur while the lifecycle interceptor's lock is held (attach and detach catch-up, detach releases), which would force an observe-only handler contract and push every mutating consumer into building its own queue and dispatcher. Rejected in favor of monitor-owned queued delivery; the cost is deferred delivery, which the notification-plus-authoritative-reads contract already absorbs.
- State reporting through monitor calls instead of a source-raised event: a custom source that registers but forgets to report its transitions would hang every wait silently, and "call the monitor service resolved from your context" is unusual choreography compared to the idiomatic "raise your event when your state changes". Rejected in favor of the event primitive.
- Auto-registered monitor (source adds the monitor to its own context via `TryAddService` on start, no explicit configuration): `TryAddService` cannot place a service at tree scope from below. A subtree-rooted source would add the monitor to its subtree context, where the tree root and sibling subtrees cannot resolve it; monitors fragment per subtree, and a waiter on the root context creates yet another empty monitor and blocks forever. Rejected in favor of explicit `WithSourceMonitoring()` configuration on the tree root context.
- Constructor-time monitor registration (register in the `SubjectSourceBase` constructor): the base constructor runs before the derived constructor, so `SourceRegistered` and the `Sources` snapshot would publish a partially constructed source (`RootSubject` and other derived members not yet assigned, a `NullReferenceException` trap for handlers), and a source that is constructed but never started would block every wait forever. Rejected; `StartAsync` is the natural fully-constructed hook and gives "only sources that can ever synchronize participate" semantics.
- Claim-derived registration (register a source on its first `SetSource`, unregister on its last `RemoveSource`): registration must precede connection for the wait to be meaningful, but claims only exist after a connection succeeds, so an offline, retrying source would be invisible and the wait would complete prematurely; a source that claims nothing would never appear in diagnostics; reconnect cycles that release claims would drop membership mid-outage; and the claim path would need per-source refcounting under lock. Rejected: lifecycle state rides the lifecycle (`StartAsync`/`Dispose`), ownership events ride the ownership primitives (`SetSource`/`RemoveSource`).
- `IServiceProvider` wait overloads that force construction of DI-registered sources to pre-register them: resolving `IEnumerable<IHostedService>` constructs every hosted service in the application ahead of normal host startup with observable side effects, and scoping it to sources would require a new DI registration convention. The registration count provides the same guarantee with no such coupling. Dropped.
- Emitting claim and release events from `SourceOwnershipManager` instead of `SetSource`/`RemoveSource`: rejected because the documented low-level contract has sources calling `SetSource(this)` directly, which would produce silent claims and an untrustworthy stream. Claiming is not a hot path (once per property per connect cycle), so emission at the primitive costs one service lookup per claim.
- Polling info object (`property.TryGetSourceInfo()`): does not solve waiting or the startup race; it is the status quo with more fields. Rejected.
- Monitor as a DI singleton seeded from `IEnumerable<IHostedService>`: works for DI sources but requires a new DI registration and does not naturally cover dynamically created sources or multiple trees. Rejected in favor of the explicitly configured context service.
- Gating `Synchronized` on an empty write retry queue: the queue can be non-empty during regular operation (any transient `WriteResult.Failure` enqueues), so the state would flap; inbound and outbound are orthogonal signals (`State` versus `PendingWriteCount`). Rejected.
- A fourth `Disconnected` state: the pump has no such phase (it loops back into connect-and-load), and `LastSynchronizedAt` recovers the "stale versus never synchronized" distinction. Rejected.
- Waiting for "all properties claimed" as an earlier milestone than synchronized: claiming is connector-specific and happens during connect and subscribe setup. OPC UA claims during the address-space browse, which requires a live session, and the claimed set depends on what the server exposes; MQTT and WebSocket derive claims from local mapping but also claim during listening setup. There is no "claiming finished" signal to build on, the gap between claimed and synchronized is small (the slow part is connecting, which both milestones share), and values are still untrustworthy at that point. The useful part, distinguishing "will sync, still loading" from "no source", is already visible per property as `Connecting` versus `Unclaimed`. Rejected.
- Modeling source state as observable subject properties (making sources interceptor subjects): UIs and GraphQL would bind natively, but infrastructure state would flow through the very change pipeline the sources operate, with echo and feedback risks (a source publishing its own `Connecting` while disconnected). Applications can mirror state into their model deliberately, as HomeBlaze does today. Rejected.
- Moving ownership storage into the Registry: gives ownership a metadata home but changes nothing functionally; per-property attached data stays because `TryGetSource` is on the hot outbound path (checked per change in `ChangeQueueProcessor`) and must remain O(1) and allocation-free. Rejected.
- `IsPropertyInScope` intent query on `ISubjectSource` (`false` = never claimed, `true` = claimed if the external system exposes it, `null` = unknown locally): would allow definitive "local-only" answers at startup for out-of-scope properties and better "why is this not syncing" diagnostics. Deferred as future work; not needed for the current consumers.
