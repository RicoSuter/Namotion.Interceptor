# Source Monitoring

Every `ISubjectSource` (OPC UA, MQTT, WebSocket, or a custom source) reports whether it's still connecting, has completed its initial load, or has stopped. The `Namotion.Interceptor.Connectors.Monitoring` namespace, part of the `Namotion.Interceptor.Connectors` package, turns that per-source state into a per-tree registry, a typed event stream, and an awaitable primitive, so an application can ask "is my tree live yet" instead of polling `TryGetSource()` in a loop.

## Getting Started

Add source monitoring to the tree root context and register the completion hosted service in one call, then await synchronization from anywhere holding a reference to the tree (or a subtree):

```csharp
using Namotion.Interceptor.Connectors.Monitoring;

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithSourceMonitoring(builder.Services)
    .WithHostedServices(builder.Services);

var root = new Root(context);
builder.Services.AddSingleton(root);
builder.Services.AddOpcUaSubjectClientSource<Root>("opc.tcp://localhost:4840", "opc");
builder.Services.AddHostedService<Worker>();
```

```csharp
using Namotion.Interceptor.Connectors.Monitoring;

internal sealed class Worker(Root root) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await root.WaitForSynchronizationAsync(stoppingToken);
        // every source in the tree has finished its initial load
    }
}
```

## Waiting on Part of the Tree

`WaitForSynchronizationAsync` is an extension on `IInterceptorSubject`, not on the context: the subject you call it on is the wait's anchor, and only sources in scope of that anchor are waited on. Change the anchor from the tree root to a subtree to wait on only that branch:

```csharp
await root.Kitchen.WaitForSynchronizationAsync(stoppingToken);
```

A source is in scope for an anchor when its root subject and the anchor lie on the same root-to-leaf path, in either direction: the source's root is an ancestor of (or is) the anchor, so it may claim into the awaited branch, or the source's root sits inside the anchor's own subtree. A source on a sibling branch is in neither set, so a source that never connects, or fails, on an unrelated branch never blocks a wait scoped to a branch it cannot claim into.

Two edge cases in that scoping matter before you rely on a wait.

If, once source registration is complete, the anchor's scope matches no source at all (a mistyped root, a device that was never configured), the wait never completes. It blocks until cancelled, and the only sign anything is wrong is a one-time warning logged the first time that empty scope is detected for that wait. If a wait you expected to complete instead sits forever, check the log for that warning before looking anywhere else.

A scope whose sources are all `Stopped` behaves the opposite way: instead of blocking, it completes. `Stopped` is terminal (see [The State Model, Transitions, and Delivery Contract](#the-state-model-transitions-and-delivery-contract)), so it is tempting to assume a fully stopped branch hangs a wait the same way an empty scope does, but it does not: a scope where every in-scope source has stopped is treated as satisfied, and the wait returns successfully. A consumer that treats a completed wait as proof the branch is live can walk straight into a dead one.

## Reading Per-Property State

`property.GetSourceState()` reads a property's synchronization state, derived from its owning source with no per-property storage:

```csharp
using Namotion.Interceptor.Connectors.Monitoring;

var property = new PropertyReference(root.Kitchen, nameof(Kitchen.Temperature));
var state = property.GetSourceState();
```

| Value | Meaning |
|---|---|
| `Unclaimed` | No source owns this property. |
| `Connecting` | A source owns it, but that source hasn't completed its initial load (or has reconnected since it last did). |
| `Synchronized` | The owning source completed its initial load. What that guarantees differs per protocol; see [What Synchronized Means per Protocol](#what-synchronized-means-per-protocol). |
| `Stopped` | The owning source shut down; it will not restart. |

`GetSourceState()` is only fully meaningful once the branch containing the property has been awaited through `WaitForSynchronizationAsync`. Before any claiming has happened, `Unclaimed` cannot be distinguished from "not yet claimed, but will be." After a claim it reports `Connecting`, so "will synchronize, still loading" is already distinguishable from "no source" even before the wait completes.

## What Synchronized Means per Protocol

OPC UA's `LoadInitialStateAsync` batch-reads every owned property from the session before returning, and WebSocket's applies the full-state message the server sends on connect. For both, reaching `Synchronized` means real values were confirmed from the external system.

MQTT's `LoadInitialStateAsync` always returns `null`. Retained messages arrive indistinguishably from any other message, through the normal subscription handler, and neither MQTT 3.1.1 nor 5.0 defines a signal that says "no more retained messages are coming" for a subscription. `SubjectSourceBase` therefore reaches `Synchronized` once the client's subscriptions are established, not once retained values have actually arrived. This is a protocol limitation, not something left unfinished: raising `DefaultQualityOfService` does not change it, since QoS governs delivery guarantees for messages that are sent, not whether the broker tells the client when a topic's retained backlog is exhausted. See [#418](https://github.com/RicoSuter/Namotion.Interceptor/issues/418) for an opt-in barrier that would wait for the first message (retained or live) per subscribed topic before declaring `Synchronized`.

## Applications That Create Sources at Runtime

`WithSourceMonitoring(builder.Services)` registers a `SourceRegistrationGate` hosted service that calls `CompleteSourceRegistration()` once `IHostApplicationLifetime.ApplicationStarted` fires. That fits an application where every source is a DI-registered hosted service, started as part of ordinary host startup.

An application that creates sources dynamically, for example devices discovered after the host has already started, uses the parameterless `WithSourceMonitoring()` overload instead, and declares registration complete itself once it has started everything it intends to for that batch:

```csharp
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Hosting;

protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await LoadAsync(stoppingToken);
    await _context.WaitForPendingHostedServiceActionsAsync(stoppingToken);
    _context.CompleteSourceRegistration();
}
```

The barrier is there because `LoadAsync` typically attaches sources through the hosted-service path (see [Hosting](hosting.md)), and attaching queues a `StartAsync` call, which is what actually registers the source with the monitor, rather than running it inline. Calling `CompleteSourceRegistration()` before that queue has drained would let a wait started right afterward see a branch that looks fully registered while a source is still on its way in. `WaitForPendingHostedServiceActionsAsync` closes that gap: it completes once every start and stop action queued before the call has actually run.

A `SourceMonitor` is born with one registration hold already taken, at `WithSourceMonitoring()` time, during context configuration and before the host is even built. That is what makes the ordering above work regardless of hosted service construction order: no wait can complete until something explicitly releases that hold.

For a later batch, more devices discovered after the first `CompleteSourceRegistration()` call, take a further hold with `DeferWaitCompletion()` so pending waits do not complete mid-batch:

```csharp
using var hold = _context.DeferWaitCompletion();
// create and start this batch's sources
```

Taking a hold blocks any wait that is still pending, but it never un-completes a wait that has already finished. Dispose the hold once the batch has registered.

## Observing Changes

Two ways to observe state, depending on what you're holding.

A consumer that already holds a reference to a specific source, for example an application wrapper around one `IOpcUaSubjectClientSource`, subscribes to that source's own `StateChanged` event directly, with no enumeration and no stream filtering needed. `StateChanged` fires synchronously on the transitioning thread, inside the source's own transition lock, so handlers must be observe-only: they must not block, and must not cause a transition of any source, directly or indirectly (the lock is reentrant, and a nested transition would publish out of order).

An aggregate consumer, a wait, a diagnostics dashboard, an index across every source in the tree, subscribes to the monitor stream instead:

```csharp
using Namotion.Interceptor.Connectors.Monitoring;

using var subscription = context.GetSourceMonitor().Subscribe(sourceEvent =>
{
    // handle sourceEvent
});
```

Delivery here is queued per subscription and runs outside every lock, so a slower or mutating handler only delays its own subscription, never the transitioning thread or other subscribers. `Subscribe` also returns the source snapshot at the moment of subscribing (`SourceSubscription.Sources`), captured atomically with the subscription, so a consumer that seeds its own state from that snapshot and then processes the stream sees every source exactly once.

`SourceEvent.OldState` and `NewState` record one specific transition and must not be applied blindly to a derived view: events for the same property can be enqueued out of order, because the ownership compare-and-set and the stream enqueue are not atomic, so a release can be delivered before the claim it followed. Use `SourceEvent.CurrentState` instead, which re-resolves the authoritative state at read time rather than replaying what the event captured.

## The State Model, Transitions, and Delivery Contract

```csharp
public enum SourceState
{
    Unclaimed,
    Connecting,
    Synchronized,
    Stopped
}
```

| State | Meaning |
|---|---|
| `Unclaimed` | Only returned by the property-level API (`GetSourceState()`); a source itself is never `Unclaimed`. |
| `Connecting` | Registered or claimed, but subscribe-read-replay isn't complete. Also the state immediately after a detected connection loss, since the connect-and-load phase runs again. |
| `Synchronized` | The source completed its initial load procedure. What that guarantees differs per protocol; see [What Synchronized Means per Protocol](#what-synchronized-means-per-protocol). |
| `Stopped` | The source shut down. |

A source's state is driven by its pump lifecycle: construction and pump entry start it at `Connecting`; `StartBuffering()`, called on every connect and every reconnect, transitions to `Connecting`; a completed initial load transitions to `Synchronized`; a pump failure that escapes the connector's own handling transitions back to `Connecting` before the retry delay. A connector that detects a connection loss before it starts buffering, OPC UA's keep-alive handler is the one built-in example, calls the protected `ReportConnectionLost()` to report `Connecting` immediately, rather than leaving `State` at `Synchronized` for the entire reconnect window.

`Stopped` is terminal: once a source reports it, no further transition succeeds, and `ExecuteAsync` sets it in a `finally` block so it fires on every exit path, including cancellation. This is enforced by an explicit guard in `SubjectSourceBase.StartAsync`, not by the hosting platform: `BackgroundService.StartAsync` would happily run `ExecuteAsync` again on a second call, against a fresh, uncancelled token. Without the guard, a "restarted" stopped source would claim, load, and apply live values while `State` stayed `Stopped`. A stopped source instance is never restarted; create a new instance instead.

`LastSynchronizedAt` records when the most recent initial synchronization completed (`null` if it never has), so a source that is `Connecting` after a drop can still be reported as "stale, last confirmed at T" rather than just "not synchronized." `PendingWriteCount` is orthogonal to `State`: it describes the outbound write retry queue and can be non-empty during entirely normal synchronized operation.

### The Event Stream

Every source metadata change is one `SourceEventKind`:

| Kind | `Property` | When |
|---|---|---|
| `SourceRegistered` | `null` | A source registered, which happens when it starts. |
| `SourceUnregistered` | `null` | A source unregistered, which happens when it is disposed. |
| `StateChanged` | `null` | A source's own state changed. |
| `PropertyClaimed` | set | A source took ownership of a property. |
| `PropertyReleased` | set | A source gave up ownership of a property. |
| `PropertyEnteredView` | set | An already-claimed property joined the tree when its subject attached; ownership did not change. |
| `PropertyLeftView` | set | A still-claimed property left the tree when its subject detached; ownership did not change. |

`SourceMonitor.Publish` enqueues every event onto each subscriber's own queue; each subscription drains its own queue on a single worker at a time, so a slow handler delays only that subscription. There is no ordering guarantee across subscriptions: two subscribers can observe events in different relative orders under concurrent activity.

## Breaking Change for Custom ISubjectSource Implementers

`ISubjectSource` gained four members for this feature:

| Member | Purpose |
|---|---|
| `SourceState State { get; }` | The source's current synchronization state. |
| `DateTimeOffset? LastSynchronizedAt { get; }` | When the most recent initial synchronization completed. |
| `int PendingWriteCount { get; }` | The outbound write retry queue depth. |
| `event EventHandler<SourceEvent>? StateChanged` | Raised whenever `State` changes; see [Observing Changes](#observing-changes) for the handler contract. |

Any type implementing `ISubjectSource` directly, rather than deriving from `SubjectSourceBase`, must now implement all four. This is a breaking change for such implementers.

Deriving from `SubjectSourceBase` is recommended instead of implementing `ISubjectSource` directly: it implements all four members, drives the connection-phase transitions automatically through `SubjectPropertyWriter`, and is what every built-in connector (OPC UA, MQTT, WebSocket) already does. See [Implementing a Source](connectors.md#implementing-a-source) for the base class's hooks.

## Worked Sample: Availability Attributes

A common pattern: expose an `IsAvailable` flag per device, derived from a stored `ConnectionState` that an updater maintains from the monitor stream.

```csharp
using Namotion.Interceptor.Connectors.Monitoring;

[InterceptorSubject]
public partial class Device
{
    public partial SourceState ConnectionState { get; set; }

    [Derived]
    public bool IsAvailable => ConnectionState == SourceState.Synchronized;
}
```

This pattern depends on construction order: the updater must be constructed, and `Subscribe` called, before any source it cares about starts claiming properties. `ISubjectSource` exposes no way to enumerate the properties a source has already claimed, so `PropertyClaimed` (and `PropertyEnteredView`) are the only way this index ever learns about a claim. An updater constructed after a source has already started never receives the events for whatever that source claimed before the subscription existed, there is no way to recover the gap afterward, and the affected properties are simply absent from `_bySource` forever, leaving `IsAvailable` stuck `false` for them even though the source is `Synchronized`.

The updater subscribes once and reacts to every event kind that can change a device's availability. `SourceRegistered` and `SourceUnregistered` carry no property, so nothing needs applying until a property is actually claimed:

```csharp
using System.Collections.Concurrent;
using System.Collections.Immutable;
using Namotion.Interceptor.Connectors.Monitoring;

public sealed class DeviceAvailabilityUpdater : IDisposable
{
    // Add-only, and only ever a record of claims this updater actually observed (see the
    // construction-order requirement above). Within that limit, a stale entry left behind by a
    // release, or by a subject leaving the tree, is harmless: Apply always re-reads through
    // GetSourceState(), so once a property is in the index it never needs to be removed again.
    private readonly ConcurrentDictionary<ISubjectSource, ImmutableHashSet<PropertyReference>> _bySource = new();
    private readonly SourceSubscription _subscription;

    public DeviceAvailabilityUpdater(SourceMonitor monitor)
    {
        _subscription = monitor.Subscribe(Handle);
    }

    public void Dispose() => _subscription.Dispose();

    private void Handle(SourceEvent sourceEvent)
    {
        switch (sourceEvent.Kind)
        {
            case SourceEventKind.PropertyClaimed:
            case SourceEventKind.PropertyEnteredView:
                Track(sourceEvent.Source, sourceEvent.Property!.Value);
                Apply(sourceEvent.Property!.Value, sourceEvent.CurrentState);
                break;

            case SourceEventKind.PropertyReleased:
            case SourceEventKind.PropertyLeftView:
                Apply(sourceEvent.Property!.Value, sourceEvent.CurrentState);
                break;

            case SourceEventKind.StateChanged:
                if (_bySource.TryGetValue(sourceEvent.Source, out var properties))
                {
                    foreach (var property in properties)
                    {
                        Apply(property, property.GetSourceState());
                    }
                }
                break;
        }
    }

    private void Track(ISubjectSource source, PropertyReference property) =>
        _bySource.AddOrUpdate(
            source,
            _ => ImmutableHashSet.Create(property),
            (_, existing) => existing.Add(property));

    private static void Apply(PropertyReference property, SourceState state)
    {
        if (property.Subject is Device device)
        {
            device.ConnectionState = state;
        }
    }
}
```

For the four property-kind events, `sourceEvent.CurrentState` already resolves through `GetSourceState()` for that specific property, so `Apply` can use it directly. `StateChanged` carries no property at all, `sourceEvent.CurrentState` there is the source's own state and says nothing about any individual property, so the handler instead walks its own index of properties this source has claimed and calls `property.GetSourceState()` on each one. That per-property re-read is what makes the index safe to keep imprecise about *removal*: even a property the index still lists after a release resolves to its actual current state rather than a stale one, so once a claim has been observed, the index never needs to drop it again. It does not make the index safe to keep imprecise about *addition*: a claim this updater never observed, because it subscribed after the claim happened, is missing from the index permanently, with no later event to fill the gap.
