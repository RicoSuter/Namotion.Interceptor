---
title: Events and Commands (Messages)
navTitle: Messages
---

# Events and Commands (Messages)

## Overview [Planned]

HomeBlaze includes a pub/sub messaging system for domain events and commands. This is separate from property change tracking — property changes flow through the interceptor pipeline, while events represent discrete occurrences that subjects want to announce to other parts of the system.

The messaging abstractions are defined (`HomeBlaze.Abstractions.Messaging`) but not yet implemented — no message bus implementation exists, and no subjects currently publish or subscribe to events.

## Architecture

### Two Subscription Levels

Events can be consumed at two levels:

```
┌──────────────────────────────────────────────────────┐
│  System-wide: IMessageBus (IObservable<IMessage>)    │
│  Merges all subject event streams into one           │
│  Subscribe to all events, filter by type             │
└──────────────────────┬───────────────────────────────┘
                       │ merges
         ┌─────────────┼──────────────┐
         │             │              │
    ┌────▼──────┐  ┌───▼───────┐ ┌────▼──────┐
    │ Subject   │  │  Subject  │ │ Subject   │
    │IObservable│  │IObservable│ │IObservable│
    │<SwitchEvt>│  │<TempAlert>│ │<DoorEvt>  │
    └───────────┘  └───────────┘ └───────────┘
```

**Direct subscription** — subscribe to a specific subject's event stream. A subject declares which events it produces by implementing `IObservable<TEvent>` (standard Rx). This serves as both the static type declaration (discoverable via registry) and the subscription endpoint. Only the subject controls when events are pushed — external code can subscribe but not publish.

**Message bus** — `IMessageBus` aggregates all subject event streams into a single system-wide `IObservable<IMessage>`. Subscribers can filter by event type using standard Rx operators. Useful for system-wide monitoring, logging, AI agents watching for patterns across subjects, or the alarms system.

### Message Types [Implemented]

All messages implement `IMessage` (marker interface). Two categories:

| Type | Interface | Semantics | Example |
|------|-----------|-----------|---------|
| Event | `IEvent` | Something that happened (past tense). Includes `Timestamp` | A switch turned on, a device disconnected, a threshold was crossed |
| Command | `ICommand` | Request to do something (imperative) | Send a notification, trigger a calibration |

### Subject as Event Source

A subject declares which events it produces by implementing `IObservable<TEvent>`. Internally it uses a private `Subject<T>` (Rx) to push events — external code can only subscribe, not publish.

```csharp
[InterceptorSubject]
public partial class HueBridge : IObservable<SwitchEvent>
{
    private readonly Subject<SwitchEvent> _switchEvents = new();

    // IObservable<SwitchEvent> — external code can subscribe
    public IDisposable Subscribe(IObserver<SwitchEvent> observer)
        => _switchEvents.Subscribe(observer);

    // Only the subject itself can publish
    private void OnLightChanged(string lightId, bool isOn)
    {
        _switchEvents.OnNext(new SwitchEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            DeviceId = lightId,
            Switch = this,
            IsOn = isOn
        });
    }
}
```

**Why `IObservable<TEvent>`:**
- **Static declaration** — the registry discovers `IObservable<SwitchEvent>` on the subject's interfaces
- **No external publishing** — `IObservable<T>` only exposes `Subscribe`
- **Standard pattern** — System.Reactive is already a dependency
- **Composable** — subscribers get full Rx operators (filtering, throttling, buffering, combining)

### Message Bus

`IMessageBus` aggregates all subject event streams into a single system-wide observable:

```csharp
public interface IMessageBus : IObservable<IMessage>
{
    void Publish(IMessage message);
}
```

The bus discovers all subjects implementing `IObservable<T>` (where T : IMessage) via the registry and merges their streams. The `Publish` method exists for infrastructure use (e.g., cross-instance event relay) — subjects use their own internal `Subject<T>`.

The bus implementation must subscribe to `LifecycleInterceptor.SubjectAttached` and `SubjectDetaching` events to dynamically subscribe/unsubscribe as subjects join or leave the graph. This prevents memory leaks from stale subscriptions and ensures new subjects are immediately visible on the bus.

### Subscription Examples

```csharp
// Direct: subscribe to one subject's events
var hueBridge = ...; // IObservable<SwitchEvent>
hueBridge.Subscribe(e => HandleSwitchEvent(e));

// Message bus: subscribe to all events system-wide
messageBus.Subscribe(e => LogEvent(e));

// Message bus: filter by event type
messageBus.OfType<SwitchEvent>().Subscribe(e => HandleAnySwitchEvent(e));

// Message bus: filter by multiple criteria
messageBus
    .OfType<DeviceEvent>()
    .Where(e => e.DeviceId.StartsWith("floor2/"))
    .Subscribe(e => HandleFloor2Event(e));
```

### Concrete Events [Implemented]

Events are defined as records inheriting from base types:

```csharp
public abstract record DeviceEvent : IEvent
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string DeviceId { get; init; }
}

public record SwitchEvent : DeviceEvent
{
    public required ISwitchState Switch { get; init; }
    public required bool IsOn { get; init; }
}
```

Plugin authors define their own event records for domain-specific occurrences.

### Typed Publishers [Implemented]

`IMessagePublisher<TMessage>` provides an alternative injection-based publishing pattern for subjects that prefer not to implement `IObservable<T>` directly:

```csharp
public interface IMessagePublisher<in TMessage> where TMessage : IMessage
{
    void Publish(TMessage message);
}
```

The DI container provides a typed publisher that routes to the message bus. Both patterns (`IObservable<T>` + internal `Subject<T>`, or injected `IMessagePublisher<T>`) result in events flowing through the bus.

## How Events Relate to Other Systems

| System | Relationship |
|--------|-------------|
| **Property change tracking** | Different mechanism. Property changes are continuous state flowing through the interceptor pipeline. Events are discrete occurrences |
| **Health checks** | Health state transitions publish events via the subject's observable (see [Observability](observability.md)). The health page subscribes for live updates |
| **Alarms** | Alarm state changes (active, acknowledged, cleared) will publish events. Notification channels subscribe and route alerts (see [Alarms](alarms.md)) |
| **AI agents** | Built-in agents can subscribe to specific event types — either directly on subjects or system-wide via the bus |
| **Audit trail** | Events can feed the audit trail (see [Audit](audit.md)) |
| **History** | Events are not stored in the time-series history (which tracks property values). Event history uses a dedicated event store — see below |

## Cross-Instance Events [Planned]

The in-process message bus only covers events within a single instance. For the central UNS to have full visibility of events across all satellites, events need a cross-instance transport.

### Why Not WebSocket (SubjectUpdate Protocol)

The WebSocket sync is designed for state replication — continuous, last-writer-wins, with full snapshots on reconnect. Events have fundamentally different delivery semantics: discrete, ordered, must not be lost or deduplicated. Mixing both into one protocol would force two conflicting delivery models into one channel.

### Planned Approach: Central Message Broker

A shared message broker (e.g., MQTT, which is already a supported connector) serves as the cross-instance event transport. Each instance publishes events to the broker, and any interested instance subscribes.

```
┌─────────────┐  ┌─────────────┐  ┌─────────────┐
│ Satellite A │  │ Satellite B │  │ Central UNS │
│             │  │             │  │             │
│ IMessageBus │  │ IMessageBus │  │ IMessageBus │
└─────┬───────┘  └──────┬──────┘  └──────┬──────┘
      │ publish         │ publish        │ subscribe
      └─────────────────┼────────────────┘
                   ┌────▼─────┐
                   │  Message │
                   │  Broker  │
                   │  (MQTT)  │
                   └──────────┘
```

- Satellites publish events from their local subjects to the broker
- Central subscribes and feeds incoming events into its local `IMessageBus`
- The broker handles fan-out, buffering, and delivery guarantees (QoS)
- Only the original subject (on the satellite) publishes — replicated subjects on central do not re-publish, preventing duplicates

This keeps event transport separate from state sync, with each using the protocol best suited to its semantics.

### Open Questions

- Which message broker to recommend (MQTT is natural since it's already a connector; others possible)
- Event topic structure (per-subject-type? per-instance? hierarchical?)
- Delivery guarantees required (at-least-once? exactly-once?)
- Should event propagation be opt-in per event type, or all events by default?
- Event ordering across instances with clock differences
- Broker as an optional dependency (single-instance deployments don't need it)

## Event Persistence [Planned]

Events need their own persistence layer, separate from the time-series property history. Time-series stores are optimized for continuous numeric values at a path; events are discrete, typed, and carry structured payloads.

### Event Store Interface

An `IEventStore` interface provides write and query capabilities for persisted events:

| Operation | Description |
|-----------|-------------|
| Write | Persist an event (type, timestamp, source subject path, payload) |
| Query | Retrieve events by time range, event type, source subject, or combination |

Like history sinks, event store implementations are subjects themselves — discoverable via the registry. Multiple stores can coexist (e.g., in-memory for recent events, a database for long-term).

### AI Access

Dedicated MCP tools in `HomeBlaze.Mcp`:

| Tool | Parameters | Description |
|------|-----------|-------------|
| `get_event_history` | `from?`, `to?`, `eventType?`, `sourcePath?` | Query persisted events. Queries all `IEventStore` subjects from the registry |
| `get_command_history` | `from?`, `to?`, `commandType?`, `sourcePath?` | Query persisted commands |

### Why Not Reuse Time-Series History

| Concern | Time-Series (property history) | Events |
|---------|-------------------------------|--------|
| Data shape | (path, value, timestamp) — scalar values | (type, timestamp, source, payload) — structured records |
| Semantics | Continuous state — latest value wins, downsampling is natural | Discrete occurrences — every event matters, no downsampling |
| Query patterns | "What was temperature at 3pm?" / "Show trend for last hour" | "What alarms fired today?" / "Show all switch events for floor 2" |
| Storage optimization | Columnar, compression-friendly (numeric time-series) | Row-oriented, indexed by type and source |

### Open Questions

- Concrete `IEventStore` interface design (sync vs async, pagination, streaming)
- Which backing stores to support initially (SQLite? PostgreSQL? Same DB as history sink?)
- Retention policy for events (separate from property history retention)

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Event model | Immutable records implementing `IEvent` | Timestamped, pattern-matchable, extensible by plugins |
| Subject event declaration | `IObservable<TEvent>` on the subject | Static discovery via registry, no external publishing, standard Rx pattern, composable |
| System-wide subscription | `IMessageBus` merging all subject streams | Single entry point for cross-cutting concerns (logging, AI, audit, alarms) |
| Separation from property changes | Events are a distinct mechanism | Property changes are continuous state; events are discrete occurrences |

## Open Questions

- Cross-instance event propagation strategy
- Should the bus auto-discover `IObservable<T>` subjects via registry, or require explicit registration?
