# ChangeQueueProcessor: explicit overflow behavior with a drop signal

> Scratch design artifact (issue #352). Not committed with the code.

## Background

`ChangeQueueProcessor` (in `Namotion.Interceptor.Connectors`) is the outbound write queue shared by connector client sources and server background services. It buffers property changes, deduplicates them by property at flush time, and writes the batch to a handler.

#350 added an opt-in `maxQueueDepth` bound. On overflow it drops the oldest unprocessed change and increments a polled `DropCount`. That is observability, not a policy: the drop behavior is hard-coded to "drop oldest" and there is no way for a consumer to react. A history store can accept a bounded gap, but a sync connector treats a dropped change as downstream divergence that should trigger a resync. Exposing a raw drop knob on the connector configs was therefore reverted in #350, pending this design.

## Goal and scope

Model overflow as an explicit, visible behavior on `ChangeQueueProcessor`, plus a signal a consumer can react to, plus the seam on `SubjectSourceBase` where a future resync plugs in.

In scope:
- An `OverflowBehavior` enum (`Unbounded`, `DropOldest`, `DropNewest`).
- A synchronous overflow callback carrying the drop details.
- A consolidated `ChangeQueueProcessorConfiguration` holding the processor's tuning knobs.
- A single overridable seam on `SubjectSourceBase` so a derived source can opt into a bound and react to overflow.
- Renaming the two public names #350 just shipped (`MaxQueueDepth`, `DropCount`) to match library conventions, while the same surface is being reworked.

Out of scope (tracked as a separate follow-up issue):
- Actual resync-on-overflow in the OPC UA / MQTT / WebSocket connectors. The overflow direction differs by side (a server re-pushes full state to peers; a client cannot simply reload inbound state, because the dropped data was its own outbound writes), and the natural full-state primitives live in bespoke per-connector code. That work is integration-test heavy (the ConnectorTester chaos harness) and is best done per connector against this settled mechanism.

The default everywhere stays `Unbounded`, so no current connector changes behavior.

## Decisions and rationale

- **Bound unit: raw change count.** The bound stays on `_changes.Count` (pre-dedup), exactly as today. A burst on a single property can inflate that count even though it dedups to one write, so a "false" overflow is possible; this limitation is documented rather than engineered away. Measuring distinct pending properties or estimated bytes would add allocation and contention to the lock-free enqueue path for accuracy this mechanism does not need yet. The deferred bounding-unit option has a natural home on the configuration if it is ever wanted.
- **Signal: synchronous callback, not an observable.** `ChangeQueueProcessor` is a low-allocation primitive whose collaborators (`writeHandler`, `propertyFilter`) are plain delegates; a callback matches that idiom and is the cheapest signal on the enqueue hot path (a single delegate invocation, no `Subject` allocation or subscriber list). The only internal consumer is the `SubjectSourceBase` seam, which registers exactly one handler. External reactive observability is not lost: a higher layer can bridge the callback into a `Subject<T>` later with no change to the core API, and the polled counter still covers basic monitoring. `System.Reactive` is already referenced by the package but is intentionally not used here.
- **`Unbounded = 0` is the default and gates the bound.** The behavior is the single source of truth: `Unbounded` ignores `MaxQueueSize`; `DropOldest`/`DropNewest` require it. Keeping `Unbounded` as an explicit default value is clearer than representing "no bound" implicitly through a null configuration.
- **One consolidated configuration object.** The three overflow settings plus `BufferTime` are cohesive tuning knobs, so they move into one `ChangeQueueProcessorConfiguration` rather than widening the constructor. Collaborators (`source`, `context`, `propertyFilter`, `writeHandler`, `logger`) stay as constructor parameters, since they are dependencies, not configuration.

### Naming alignment

The first-draft names were checked against the rest of the library and corrected:

| Concept | First draft | Final | Precedent in library |
| --- | --- | --- | --- |
| Config object | `QueueOverflowOptions` | `ChangeQueueProcessorConfiguration` | `MqttClientConfiguration`, `OpcUaServerConfiguration` (always "Configuration", never "Options") |
| Behavior enum | `OverflowPolicy` | `OverflowBehavior` | `TransactionConflictBehavior`, `TransactionFailureHandling` ("Policy" is unused in the library) |
| Drop payload | `OverflowInfo` | `ChangeQueueOverflow` (readonly struct) | `SubjectPropertyChange`, `WriteResult` (descriptive noun, no "Info" suffix; `EventArgs` is reserved for real events) |
| Queue bound | `MaxQueueDepth` (shipped in #350) | `MaxQueueSize` | `WriteRetryQueueSize`, `MaxMessageQueueSize` ("Size", not "Depth") |
| Drop counter | `DropCount` (shipped in #350) | `DroppedChangeCount` | `PendingWriteCount` |
| Handler delegate | `OnOverflow` | `OverflowHandler` | `writeHandler`, `propertyFilter` |

`MaxQueueDepth` and `DropCount` are public API that #350 shipped in the most recent commit. Renaming them now is acceptable: the project is pre-1.0, the public API is snapshot-tested, and this issue reworks that exact surface anyway.

## Design

### New public types (`Namotion.Interceptor.Connectors`)

```csharp
public enum OverflowBehavior
{
    Unbounded = 0,   // default: no bound; MaxQueueSize ignored
    DropOldest,      // drop from the front (the #350 behavior)
    DropNewest,      // reject the incoming change, keep what is queued
}

// Passed to the handler on the hot path, so a readonly struct (no per-event allocation).
// OverflowBehavior is always DropOldest/DropNewest here, since Unbounded never overflows.
public readonly record struct ChangeQueueOverflow(
    int DroppedChangeCount,
    OverflowBehavior OverflowBehavior,
    int MaxQueueSize);

public sealed class ChangeQueueProcessorConfiguration
{
    public TimeSpan BufferTime { get; set; } = TimeSpan.FromMilliseconds(8);
    public OverflowBehavior OverflowBehavior { get; set; } = OverflowBehavior.Unbounded;
    public int? MaxQueueSize { get; set; }                              // required and > 0 when bounded; ignored when Unbounded
    public Action<ChangeQueueOverflow>? OverflowHandler { get; set; }   // synchronous; must be non-blocking

    /// <summary>Throws if a bounded behavior is set without a positive MaxQueueSize.</summary>
    public void Validate();
}
```

`BufferTime` keeps the current semantics, including `<= 0` meaning the immediate (no-buffer) path. A connector whose own configuration exposes a nullable buffer time maps it with a fallback to the 8ms default.

### `ChangeQueueProcessor`

- Constructor becomes `(object? source, IInterceptorSubjectContext context, Func<PropertyReference, bool> propertyFilter, Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> writeHandler, ChangeQueueProcessorConfiguration configuration, ILogger logger)`. The `bufferTime` and `maxQueueDepth` parameters are removed (folded into the configuration). The constructor calls `configuration.Validate()`.
- `DropCount` is renamed to `DroppedChangeCount` (still a polled `long`).
- The bound is enforced where it is today: on the lock-free enqueue path in `ProcessAsync`, single-producer (only the dequeue loop enqueues into `_changes`).
  - `Unbounded`: no bound check; the enqueue path is byte-for-byte identical to today.
  - `DropOldest`: after enqueue, dequeue from the front until back within `MaxQueueSize` (current behavior).
  - `DropNewest`: check the count before enqueue; if at capacity, skip enqueuing the incoming change. A specific tail item cannot be removed from a `ConcurrentQueue` lock-free, and the single-producer enqueue path makes the pre-check exact.
- The overflow handler fires once per overflow event (one drop pass), carrying the number dropped in that event, not once per dropped item. A burst that drops 50 fires one call with `DroppedChangeCount = 50`. It runs synchronously on the producer thread, so the handler must only record or flag (for example, set a resync-requested flag the processing loop observes) and never do I/O inline. `DroppedChangeCount` keeps accumulating as before.

### `SubjectSourceBase` seam

`SubjectSourceBase` currently hard-codes the unbounded path. Replace it with a single overridable factory:

```csharp
protected virtual ChangeQueueProcessorConfiguration CreateChangeQueueConfiguration() =>
    new() { BufferTime = _bufferTime };   // default: unbounded, current behavior
```

`ExecuteAsync` calls this, wraps the returned `OverflowHandler` so the base also emits a warning log on overflow (the override supplies only the reaction), and constructs the processor with the result. A derived source opts into a bound by overriding this one member:

```csharp
protected override ChangeQueueProcessorConfiguration CreateChangeQueueConfiguration()
{
    var configuration = base.CreateChangeQueueConfiguration();
    configuration.OverflowBehavior = OverflowBehavior.DropOldest;
    configuration.MaxQueueSize = 1000;
    configuration.OverflowHandler = _ => RequestResync();   // resync follow-up provides RequestResync
    return configuration;
}
```

This is the documented extension point the resync follow-up overrides. This design defines and tests the seam but does not implement resync.

## Callers to update

All current call sites pass the unbounded path and keep their exact behavior:
- `OpcUaSubjectServer` (`new ChangeQueueProcessor(...)`).
- `MqttSubjectServer`.
- `WebSocketSubjectServer` via its handler's processor factory.
- `SubjectSourceBase` (now through `CreateChangeQueueConfiguration`).
- Test call sites in `ChangeQueueProcessorTests`, `SubjectTransactionEchoSuppressionTests`, and `TestSubjectSource`.

## Testing

Unit tests only (no integration), following the `When<Condition>_Then<ExpectedBehavior>` naming, explicit Arrange/Act/Assert comments, and no hardcoded waits (`AsyncTestHelpers.WaitUntilAsync` or event-based synchronization):
- `DropOldest` overflow drops the oldest and increments `DroppedChangeCount` (extends the existing test).
- `DropNewest` overflow rejects the incoming change and keeps what is queued.
- `Unbounded` drops nothing and never invokes the handler (existing test, adapted).
- The handler fires once per overflow event with `DroppedChangeCount` equal to the number dropped in that event.
- `ChangeQueueProcessorConfiguration.Validate` throws for a bounded behavior without a positive `MaxQueueSize`.
- `SubjectSourceBase` seam: a `TestSubjectSource` overriding `CreateChangeQueueConfiguration` to set a bound triggers its `OverflowHandler` (and the base warning log) under overflow.

## Public API snapshot

`VerifyChecksTests.PublicApi.verified.txt` for `Namotion.Interceptor.Connectors.Tests` is regenerated for: the new `OverflowBehavior`, `ChangeQueueOverflow`, and `ChangeQueueProcessorConfiguration` types; the changed `ChangeQueueProcessor` constructor; the `DropCount` to `DroppedChangeCount` rename; and the new `SubjectSourceBase` member.

## Follow-up

A separate issue tracks resync-on-overflow per connector: server-side full-state re-push (OPC UA node re-write, MQTT retained re-publish, WebSocket broadcast snapshot) and a client-side strategy that does not overwrite pending outbound writes, verified with the ConnectorTester chaos harness. It overrides `CreateChangeQueueConfiguration` to set a bound and an `OverflowHandler` that requests the resync.
