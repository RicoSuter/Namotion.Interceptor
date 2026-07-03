# Source Sync State

Date: 2026-07-03
Status: Approved design, pending implementation plan

## Problem

Consumers of a subject tree cannot tell whether a property value is actually in sync with its external source. At application start the tree is offline. Sources connect asynchronously, claim their properties, load the initial state, and only then start applying live updates. During that window a property shows a local default value that was never confirmed by the external system, and after a connection loss it silently shows a stale last-known-good value.

The existing API is insufficient:

- `property.TryGetSource()` is polling based and only returns the owning source, not whether that source is syncing.
- At startup, no source has claimed anything yet, so "no source" is ambiguous: it can mean "no source attached yet" or "this property will never have a source".
- Which properties a source will claim is not predictable up front (a source may claim only part of the branch it is configured for), so waiting cannot be expressed in terms of properties alone.

Two consumer groups need this:

1. Waiting consumers that must block until the tree is live (for example, automation logic that must not run on default values).
2. Diagnostics that want to show sync state per source and per property.

## Goals

- A source-level state with well-defined transitions, driven by the existing pump lifecycle in `SubjectSourceBase`.
- An awaitable primitive: wait until all known sources have completed their initial sync.
- A cheap per-property state derived from source ownership, with no per-property storage.
- Works for both DI-registered (hosted service) sources and dynamically created sources, with no new DI registrations.

## Non-Goals

- Outbound write confirmation. `Synced` describes the inbound direction only (the model mirrors the external system). Pending outbound writes remain visible through the existing `PendingWriteCount` on the source; the write retry queue can be non-empty during regular synced operation and must not affect the state.
- A per-property observable `@syncState` registry attribute. Rejected for now: every state transition would cost O(claimed properties) intercepted writes and allocations, and the attributes would leak into subject updates sent to other connectors. If a UI ever needs per-property observability, it can be built as an opt-in extension on top of the events introduced here.
- Timeout policy. A permanently unreachable device keeps its source in `Connecting` forever and the wait never completes. That is semantically honest; callers bound their patience with the `CancellationToken`.

## Design

### SourceState enum

```csharp
public enum SourceState
{
    NoSource,    // only returned by the property-level API: no source has claimed the property
    Connecting,  // registered or claimed, but subscribe-read-replay is not complete; also after a detected connection loss
    Synced,      // initial load complete, live updates flowing
    Stopped      // source shut down (host stop or dynamic removal); terminal
}
```

One enum is shared by the source-level and property-level APIs. A source itself is never `NoSource`; that value exists so the property-level API returns one coherent type. There is no separate `Disconnected` state: after a failure the pump loops straight back into its connect-and-load phase, so a disconnected source is a connecting source. The lost diagnostic distinction ("never synced" versus "was synced, now stale") is recovered through `LastSyncedAt`.

### ISubjectSource additions

`ISubjectSource` (in `Namotion.Interceptor.Connectors`) gains three members:

```csharp
SourceState State { get; }
DateTimeOffset? LastSyncedAt { get; }
event EventHandler<SourceStateChangedEventArgs>? StateChanged;
```

- `LastSyncedAt` is the completion time of the most recent initial sync; `null` means never synced. While `Connecting` after a drop, it tells a dashboard "stale, last confirmed at T".
- `SourceStateChangedEventArgs` carries the source, old state, new state, and a timestamp.

This is a breaking change for custom `ISubjectSource` implementers. `SubjectSourceBase` provides the implementation, so all built-in connectors (OPC UA, MQTT, WebSocket) inherit it without changes. The docs recommend deriving from `SubjectSourceBase`.

### State transitions in SubjectSourceBase

All transitions happen in the pump (`ExecuteAsync`); there is no multi-writer contention:

- Construction: `Connecting`.
- After `LoadInitialStateAndResumeAsync` completes: `Synced`. This is deliberately before `ReapplyRetryQueue`, because the retry queue reflects the outbound direction and can be non-empty at any time during regular operation.
- In the pump's catch block (failure detected): `Connecting`.
- On pump exit (cancellation or shutdown), in a finally block: `Stopped`.

Implementation notes:

- The state is a volatile field; `LastSyncedAt` is written before the state transition that makes it observable.
- `StateChanged` is raised from the pump thread after the field is updated, outside any lock.
- The raise helper wraps subscriber invocation in try/catch with logging. A throwing subscriber must not be treated as a pump failure (otherwise a buggy `Synced` handler would flip the source back to `Connecting` in an endless loop).

### SourceStateTracker as an auto-registered context service

The tracker lives in the `IInterceptorSubjectContext`, not in DI. In its constructor, `SubjectSourceBase` idempotently adds the tracker to its context via `TryAddService` and registers itself. On `Dispose` the source unregisters. Because both DI-hosted and dynamically created sources pass through that constructor, registration "just works" for both, and each subject tree gets its own tracker with natural scoping.

```csharp
public class SourceStateTracker
{
    IReadOnlyList<ISubjectSource> Sources { get; }   // snapshot
    event EventHandler? Changed;

    void Register(ISubjectSource source);
    void Unregister(ISubjectSource source);

    Task WaitForAllSourcesSyncedAsync(CancellationToken cancellationToken = default);
    Task WaitForSourcesSyncedAsync(Func<ISubjectSource, bool> filter, CancellationToken cancellationToken = default);
}
```

The tracker subscribes to each registered source's `StateChanged` and raises its own `Changed` event on any state change, registration, or unregistration. `Changed` carries no payload; consumers re-read the `Sources` snapshot, and consumers that need old/new state details subscribe to the per-source `StateChanged` event.

### Wait semantics

The wait completes when at least one matching source is currently registered and every currently registered, non-`Stopped` matching source is `Synced`. If the only registered matching sources are `Stopped`, the condition is vacuously met and the wait completes.

- Implementation is a re-evaluation loop over a `TaskCompletionSource` (created with `RunContinuationsAsynchronously`), triggered by any state change, registration, or unregistration.
- A source registered while someone is waiting is included; a source that drops back to `Connecting` before the condition is met re-blocks the wait.
- Once the returned task completes it stays completed. Callers who care about later disconnects subscribe to `StateChanged` or call the wait again.
- An empty set does not complete the wait. This is deliberate: it converts the startup race (waiter runs before sources are constructed) into correct blocking behavior, because mid-wait registrations are picked up. The cost is that waiting on a tree that never gets any source blocks until cancellation; this is documented.
- The filter overload covers subtree scoping (for example, only sources whose `RootSubject` is under a given subject). A filter that never matches blocks until cancellation.

### Context extension methods and the startup race

Waiting is exposed as extensions on `IInterceptorSubjectContext`:

```csharp
Task WaitForAllSourcesSyncedAsync(this IInterceptorSubjectContext context, CancellationToken cancellationToken = default);
Task WaitForAllSourcesSyncedAsync(this IInterceptorSubjectContext context, IServiceProvider serviceProvider, CancellationToken cancellationToken = default);
Task WaitForSourcesSyncedAsync(this IInterceptorSubjectContext context, Func<ISubjectSource, bool> filter, CancellationToken cancellationToken = default);
Task WaitForSourcesSyncedAsync(this IInterceptorSubjectContext context, IServiceProvider serviceProvider, Func<ISubjectSource, bool> filter, CancellationToken cancellationToken = default);
```

If no tracker service exists yet, the extensions idempotently create it via `TryAddService`, so waiting before any source exists follows the empty-set rule above (block until the first source registers).

The `IServiceProvider` overloads close the remaining startup race for hosted waiters: sources are registered in DI as keyed singletons plus `AddSingleton<IHostedService>`, and the source is the hosted service. Resolving `IEnumerable<IHostedService>` forces construction of every DI-registered source, and construction is exactly what triggers tracker self-registration. A hosted waiter whose `ExecuteAsync` runs before the host has constructed later-registered sources uses this overload and is fully race-free. Constructing hosted services early is safe; the host reuses the same singletons.

Dynamic sources that do not exist yet at call time cannot be known by any mechanism. Applications that create sources dynamically (for example HomeBlaze device loading) coordinate at the application level: finish creating sources, then wait, or rely on mid-wait registration pickup.

### Per-property API

A single read-only extension on `PropertyReference` in `Namotion.Interceptor.Connectors`:

```csharp
public static SourceState GetSourceState(this PropertyReference property)
```

Derivation: `TryGetSource` returns nothing gives `NoSource`; otherwise the owning source's `State`. Computed on read from existing attached data, with no storage and no allocations. For richer diagnostics (`LastSyncedAt`, `PendingWriteCount`) the caller uses `TryGetSource` and reads the source directly.

`GetSourceState` deliberately takes no `IServiceProvider`. Forcing construction of DI sources cannot help a synchronous read: claiming happens when a source connects, not when it is constructed, so the property would still read `NoSource` at that instant. The result is only fully meaningful after phase 1 of the consumer protocol below.

Once a property is claimed, `GetSourceState()` returns `Connecting` instead of `NoSource`, so even before phase 1 completes a diagnostics view can distinguish "will sync, still loading" from "no source, so far".

### Consumer protocol (canonical pattern)

```csharp
// Phase 1: wait until claiming is over and every source finished subscribe-read-replay
await context.WaitForAllSourcesSyncedAsync(serviceProvider, cancellationToken);

// Phase 2 is now instant and well-defined per property:
// Synced = live value, NoSource = local-only property that was never claimed
```

Phase 2 is not a second wait. Its value is that per-property answers only become well-defined after phase 1: from that moment, `NoSource` genuinely means "local-only". This resolves the problem that it is not predictable which properties a source will claim.

Consumers interested in a single property use the same protocol: wait once, then read `property.GetSourceState()`.

### Diagnostics

- A dashboard lists `tracker.Sources` with `State`, `LastSyncedAt`, and `PendingWriteCount`, subscribing to the tracker's `Changed` event for updates.
- Per-property display reads `GetSourceState()` and re-renders on source state changes. Notification is per source, not per property, which is proportionate because all properties of a source flip together.

### Edge cases

- A property claimed while its source is still loading reports `Connecting`.
- A stopped source that still owns properties reports `Stopped` per property, meaning "will never sync again".
- Ownership release on subject detach (existing `SourceOwnershipManager` behavior) reverts properties to `NoSource`.
- A source that is disposed unregisters from the tracker; its final `Stopped` transition is broadcast by the pump exit before disposal.

## Affected Code

- `Namotion.Interceptor.Connectors`: `SourceState`, `SourceStateChangedEventArgs`, `ISubjectSource` members, `SubjectSourceBase` transitions, `SourceStateTracker`, context wait extensions, `GetSourceState` property extension.
- Built-in connectors (OPC UA, MQTT, WebSocket): no source changes expected; they inherit from `SubjectSourceBase`.
- Public API snapshot tests: `ISubjectSource` changes will fail `VerifyChecksTests.PublicApi` in affected projects; the new `.verified.txt` snapshots are accepted as part of the change.
- `docs/connectors.md`: new "Source state and waiting for sync" section, including the consumer protocol and the breaking-change note for custom `ISubjectSource` implementers.

## Testing

In `Namotion.Interceptor.Connectors.Tests`, following the `When<Condition>_Then<ExpectedBehavior>` naming and event-based synchronization (no hardcoded waits):

- Pump transitions via a fake source: `Connecting` initially; `Synced` after initial load; back to `Connecting` on pump failure; `Stopped` on cancellation; `LastSyncedAt` set exactly on entering `Synced` and preserved through a later disconnect.
- Event behavior: `StateChanged` raised with correct old/new states; a throwing subscriber is logged and does not fail the pump.
- Tracker: registration from the base constructor, unregistration on dispose, tracker created idempotently when multiple sources share a context, `Changed` forwarding.
- Wait semantics: completes only when all non-`Stopped` sources are `Synced`; empty set blocks; a source registered mid-wait is included and re-blocks the wait when `Connecting`; filter overload scopes correctly; cancellation propagates.
- `IServiceProvider` overload: with a `ServiceCollection` containing an unstarted hosted source, the overload forces construction and the wait observes the source.
- Per-property: `NoSource` for unclaimed, `Connecting` for claimed while loading, `Synced` for claimed after load, `Stopped` for a claimed property of a stopped source.

## Alternatives Considered

- Observable `@syncState` dynamic registry attribute per claimed property: fully per-property observable through existing change tracking, but O(claimed properties) writes and allocations per transition, wire noise into other connectors, and attribute lifetime coupled to claim/release. Rejected; possible later as an opt-in layer on top of the events.
- Polling info object (`property.TryGetSourceInfo()`): does not solve waiting or the startup race; it is the status quo with more fields. Rejected.
- Tracker as a DI singleton seeded from `IEnumerable<IHostedService>`: works for DI sources but requires a new DI registration and does not naturally cover dynamically created sources or multiple trees. Rejected in favor of the auto-registered context service.
- Gating `Synced` on an empty write retry queue: the queue can be non-empty during regular operation (any transient `WriteResult.Failure` enqueues), so the state would flap; inbound and outbound are orthogonal signals (`State` versus `PendingWriteCount`). Rejected.
- A fourth `Disconnected` state: the pump has no such phase (it loops back into connect-and-load), and `LastSyncedAt` recovers the "stale versus never synced" distinction. Rejected.
- Per-property wait method (`WaitForSourceStateAsync(property)` returning the state once well-defined): at startup the property is typically unclaimed at call time, so the method silently degrades into the whole-tree wait, which is surprising for a per-property API. The return value is rarely actionable (`NoSource` usually also means "ready" because a local-only property has no external truth), and the behavior is expressible in two lines with the existing primitives. Deferred; a reconnect-aware consumer that waits for a single already-claimed property is the scenario that would justify adding it later (wait only for the owning source instead of the whole tree).
- Waiting for "all properties claimed" as an earlier milestone than synced: claiming is connector-specific and happens during connect and subscribe setup. OPC UA claims during the address-space browse, which requires a live session, and the claimed set depends on what the server exposes; MQTT and WebSocket derive claims from local mapping but also claim during listening setup. There is no "claiming finished" signal to build on, the gap between claimed and synced is small (the slow part is connecting, which both milestones share), and values are still untrustworthy at that point. The useful part, distinguishing "will sync, still loading" from "no source", is already visible per property as `Connecting` versus `NoSource`. Rejected.
