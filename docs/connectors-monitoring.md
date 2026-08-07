# Source Monitoring

Every `ISubjectSource` reports whether it's still connecting, has completed its initial load, or has stopped. The `Namotion.Interceptor.Connectors.Monitoring` namespace turns that per-source state into a per-tree registry, a typed event stream, and an awaitable primitive, so an application can ask "is my tree live yet" instead of polling `TryGetSource()` in a loop.

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

This works whether the sources are registered directly in DI, as above, or attached to the subject graph through `WithHostedServices` (see [Hosting](hosting.md)). An attached source is started from a queue rather than inline, so it has not registered by the time host startup finishes; registration is held open across that gap, so a wait cannot complete before the source is actually in.

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

Two things about that scoping matter before you rely on a wait.

No wait can complete before source registration is complete, whatever its scope: that is the first condition checked, ahead of any scope evaluation. So until `CompleteSourceRegistration()` has run (and any `DeferWaitCompletion()` holds are released), every wait blocks, empty scope or not.

After registration completes, a wait is not frozen to the sources that existed at that moment. Satisfaction is re-evaluated on every registration, unregistration, and state change, against whichever sources are registered right now, so a source that registers later and falls in scope of a still-pending wait is picked up just like any earlier source and can block it. The only wait that is frozen is one that has already completed, because a completed task cannot be un-completed. A scope that currently matches no source is, for the same reason, vacuously satisfied rather than blocking, the same way a scope whose sources are all `Stopped` is (`Stopped` is terminal, see [The State Model, Transitions, and Delivery Contract](#the-state-model-transitions-and-delivery-contract)): both complete immediately instead of waiting.

Be aware of what that vacuous completion does and does not tell you. An empty scope is the correct and expected answer for a branch that genuinely has no external source, such as configuration or computed state. It is also what you get if the source for that branch was never created, or if you anchored on a branch unrelated to any source. Those are application bugs, and this library cannot distinguish them from the legitimate case, because both look identical from the inside: nothing claims here. A warning is logged the first time an empty scope is detected for a given anchor, and likewise the first time a wait on that anchor completes with every in-scope source `Stopped`. It is deduplicated per anchor rather than per wait deliberately: awaiting per operation on a local-only branch is a supported pattern, and a per-wait guard would have logged once per call forever. Treat it as a hint rather than a verdict - check that log line first if a branch you expected a source to drive completes immediately. A consumer that treats a completed wait as proof the branch is live can walk straight into a dead one.

A subject referenced from two trees only fully participates in the first one. The context machinery that lets a subject's own context reach its tree's services adds that fallback the first time the subject attaches, and leaves it alone on a second attach from a different tree, so the subject's context keeps resolving through the first tree only. In practice that means a source claiming a property on such a subject publishes to the first tree's stream, and a wait anchored on the subject through the second tree sees only the first tree's sources, not the second tree's. Avoid sharing a subject instance across two independently-monitored trees if you need it to fully participate in both.

## Reading Per-Property State

`property.GetSourceState()` reads a property's synchronization state, derived from its owning source with no per-property storage:

```csharp
using Namotion.Interceptor.Connectors.Monitoring;

var property = new PropertyReference(root.Kitchen, nameof(Kitchen.Temperature));
var state = property.GetSourceState();
```

See the XML docs on `SourceState` for what each member means. `Synchronized` specifically means the owning source completed its initial load, and what that guarantees differs per protocol; see [What Synchronized Means per Protocol](#what-synchronized-means-per-protocol).

`GetSourceState()` is only fully meaningful once the branch containing the property has been awaited through `WaitForSynchronizationAsync`. Before any claiming has happened, `Unclaimed` cannot be distinguished from "not yet claimed, but will be." After a claim it reports `Connecting`, so "will synchronize, still loading" is already distinguishable from "no source" even before the wait completes.

## What Synchronized Means per Protocol

OPC UA's `LoadInitialStateAsync` batch-reads every owned property from the session before returning, and WebSocket's applies the full-state message the server sends on connect. For both, reaching `Synchronized` means real values were confirmed from the external system.

MQTT's `LoadInitialStateAsync` always returns `null`. Retained messages arrive indistinguishably from any other message, through the normal subscription handler, and neither MQTT 3.1.1 nor 5.0 defines a signal that says "no more retained messages are coming" for a subscription. `SubjectSourceBase` therefore reaches `Synchronized` once the client's subscriptions are established, not once retained values have actually arrived. This is a protocol limitation, not something left unfinished: raising `DefaultQualityOfService` does not change it, since QoS governs delivery guarantees for messages that are sent, not whether the broker tells the client when a topic's retained backlog is exhausted. See [#418](https://github.com/RicoSuter/Namotion.Interceptor/issues/418) for an opt-in barrier that would wait for the first message (retained or live) per subscribed topic before declaring `Synchronized`.

## Applications That Create Sources at Runtime

There are two ways to declare registration complete, and no third: pick by whether your sources exist before host startup finishes.

`WithSourceMonitoring(builder.Services)` registers a `SourceRegistrationGate` hosted service that calls `CompleteSourceRegistration()` once `IHostApplicationLifetime.ApplicationStarted` fires. That covers every source that exists by the end of host startup, whether it is a DI-registered hosted service or one attached to the subject graph. Attaching a source queues its `StartAsync` rather than running it inline, so it has not registered yet when the attach returns; the hosting layer holds registration open from the attach until that start has actually run, and nested attaches compose, because a service that attaches children during its own start takes their holds before its own is released.

An application that creates sources *after* startup, for example devices discovered at runtime, uses the parameterless `WithSourceMonitoring()` overload instead and declares registration complete itself:

```csharp
using Namotion.Interceptor.Connectors.Monitoring;

protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await LoadAsync(stoppingToken);
    _context.CompleteSourceRegistration();
}
```

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

Because of that, the stream is not a ledger: it cannot be replayed to reconstruct a history of transitions for a property, even in principle, since the order events arrive in is not the order the transitions actually happened in. A consumer built on it maintains a view of current state, kept up to date by whichever events arrive, not a log of what happened and when.

`CurrentState` reports ownership and nothing else. It does not tell you whether the property's subject is still in the object graph, and this monitor publishes no events about that either: source monitoring answers "which source owns this property, and what state is that source in". Graph membership is a separate question with its own owner, so ask `ISubjectRegistry.TryGetRegisteredSubject(subject)` for it. In practice the two rarely need distinguishing, because every built-in connector releases its claims when a subject detaches, which publishes `PropertyReleased` and makes `CurrentState` report `Unclaimed`.

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

See the XML docs on `SourceState` for what each member means. On a source itself (as opposed to a property, via `GetSourceState()`), `Unclaimed` never occurs.

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

`SourceMonitor.Publish` enqueues every event onto each subscriber's own queue; each subscription drains its own queue on a single worker at a time, so a slow handler delays only that subscription. There is no ordering guarantee across subscriptions: two subscribers can observe events in different relative orders under concurrent activity.

## Worked Sample: Availability Attributes

A common pattern: expose an `IsAvailable` flag on the bound property itself, as a derived attribute, rather than a separate per-device shadow property.

```csharp
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class Device
{
    public partial double Temperature { get; set; }
}
```

`RegisteredSubjectProperty.AddDerivedAttribute` attaches the attribute; `TryGetAttribute` reads it back:

```csharp
using Namotion.Interceptor.Registry;

var isAvailable = device
    .TryGetRegisteredProperty(nameof(Device.Temperature))?
    .TryGetAttribute("IsAvailable")?
    .GetValue();
```

This pattern depends on construction order: the updater must be constructed, and `Subscribe` called, before any source it cares about starts claiming properties. `ISubjectSource` exposes no way to enumerate the properties a source has already claimed, so `PropertyClaimed` is the only way this updater ever learns about a claim. A property claimed before the updater subscribed never gets an `IsAvailable` attribute at all, with no later event to fill the gap.

The updater subscribes once and reacts to every event kind that can change a property's availability. `SourceRegistered` and `SourceUnregistered` carry no property, so nothing needs applying until a property is actually claimed:

```csharp
using System.Collections.Concurrent;
using System.Collections.Immutable;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Registry;

public sealed class DeviceAvailabilityUpdater : IDisposable
{
    private const string IsAvailableAttribute = "IsAvailable";

    // Properties each source has claimed. Needed only for StateChanged, which carries no property
    // (see Handle below).
    private readonly ConcurrentDictionary<ISubjectSource, ImmutableHashSet<PropertyReference>> _bySource = new();

    // Backing store for each property's IsAvailable attribute, so the attribute's getValue is a
    // dictionary read rather than a live GetSourceState() call on every access.
    private readonly ConcurrentDictionary<PropertyReference, bool> _isAvailable = new();

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
                Track(sourceEvent.Source, sourceEvent.Property!.Value);
                Apply(sourceEvent.Property!.Value, sourceEvent.CurrentState);
                break;

            case SourceEventKind.PropertyReleased:
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

    private void Track(ISubjectSource source, PropertyReference property)
    {
        _bySource.AddOrUpdate(
            source,
            _ => ImmutableHashSet.Create(property),
            (_, existing) => existing.Add(property));

        // First sighting of this property: give it an IsAvailable attribute.
        var registeredProperty = property.TryGetRegisteredProperty();
        if (registeredProperty is not null && registeredProperty.TryGetAttribute(IsAvailableAttribute) is null)
        {
            registeredProperty.AddDerivedAttribute(
                IsAvailableAttribute, typeof(bool),
                getValue: _ => _isAvailable.TryGetValue(property, out var available) && available,
                setValue: (_, _) => { }); // no-op: the value lives in _isAvailable; this only triggers recalculation
        }
    }

    private void Apply(PropertyReference property, SourceState state)
    {
        var isAvailable = state == SourceState.Synchronized;
        _isAvailable[property] = isAvailable;
        property.TryGetRegisteredProperty()?.TryGetAttribute(IsAvailableAttribute)?.SetValue(isAvailable);
    }
}
```

For the two property-kind events, `sourceEvent.CurrentState` resolves through `GetSourceState()` for that specific property (see [Observing Changes](#observing-changes)), so `Apply` can use it directly. `StateChanged` carries no property at all: `sourceEvent.CurrentState` there is the source's own state and says nothing about any individual property, so the handler instead walks `_bySource` for that source and calls `property.GetSourceState()` on each claimed property directly.
