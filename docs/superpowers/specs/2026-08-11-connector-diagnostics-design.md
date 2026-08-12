# Connector diagnostics: a shared, grouped model

Status: designed, not implemented. Closes #277.

Revision 7. Three adversarial reviews ran against revisions 1, 3 and 5. Between them they found five factual errors about existing code and a cluster of implementation defects, all corrected here. The revision 5 review compiled the proposed type shape rather than reasoning about it, which is why the type model below differs materially from earlier revisions.

Two review arguments are answered rather than accepted, in "Why drop counting matters despite being zero in-repo" and in the naming section, so the next reader does not relitigate them.

Line citations were verified against the tree at the time of writing. Freeze the file before implementation, since they drift.

## Problem

Connector diagnostics are defined per connector, but most of what they report is connector-agnostic. Five consequences:

1. **Diagnostics exist only for OPC UA.** MQTT and WebSocket have no diagnostics type, so what an operator can learn depends on which connector they use.
2. **Client and server name the same concepts differently.** Liveness is `IsConnected` on the OPC UA client, `IsRunning` on the OPC UA server, `IsListening` on the MQTT server. Failure history is four reconnect counters on the client and `ConsecutiveFailures` on the server. The server has `StartTime` and `Uptime`; the client has `LastConnectedAt` and no uptime.
3. **The layering is inverted.** The queues and throughput counters live in `Namotion.Interceptor.Connectors`, but there is no diagnostics surface at that layer, so every number is re-exposed per connector. This is why #277 reads as a "cross-layer bridging" problem: it is a missing abstraction, not a bridging problem.
4. **Sub-blocks are applied inconsistently.** Read-after-write has both a nested `ReadAfterWrite` block and a flat `PendingReadAfterWrites`. Polling has the same duplication via `PollingItemCount`.
5. **Three buffers exist and none is observable.** Two outbound, one inbound. Outbound writes are dropped on three paths that only log.

## Why drop counting matters despite being zero in-repo

Every in-repo connector constructs `ChangeQueueProcessor` with `maxQueueDepth: null` (`SubjectSourceBase.cs:240`, `OpcUaSubjectServer.cs:136`, `MqttSubjectServer.cs:103`, `WebSocketSubjectHandler.cs:375`), so `ChangeQueueProcessor.DropCount` is always 0 here. A review argued from this that the machinery should be cut. It should not:

- `maxQueueDepth` is a **public constructor parameter**. Any consumer-written connector can bound its queue today, and bounding an unbounded queue feeding a slow sink is the correct advice. The counter is live for them on day one.
- The processor is created **per connect cycle**, so a naive implementation rebases the count on every reconnect: a metric that silently resets exactly when it matters. The accumulator must exist before the first bounded connector, not after.

Independently of any bound, **three outbound paths drop data today and only log it**:

- `WriteRetryQueue` ring-buffer overflow (`WriteRetryQueue.cs:71-87`).
- `ReconcileRetryQueueAsync` no-setter and per-change-exception branches (`SubjectSourceBase.cs:486-499`).
- The direct-write discard when no retry queue exists (`SubjectSourceBase.cs:270-287`, reached only when the queue is absent per `:267`).

These are unconditional outbound data loss in normal operation and are the immediate value of this change.

**Deliberately not counted**: the disabled-queue drain at `SubjectSourceBase.cs:345-351`. That branch drains the *entire* subscription with no ownership filter (the filter is only in the else-branch at `:356-367`), and `PropertyChangeInterceptor` fans every committed change to every queue subscription unfiltered (`:196`, `:244`), so the subscription carries other sources' properties and this source's own inbound applies. Counting it would report other sources' traffic as this source's lost writes.

That exclusion has a consequence worth stating rather than discovering later: with `writeRetryQueueSize: 0` the uncounted drain is the dominant loss path, so `OutboundRetries.TotalDropped` under-reports in exactly that configuration.

**Also not counted**: `connectors.md:783` documents that writes to properties a source has not claimed yet are discarded (`SubjectSourceBase.cs:364-367`). A fifth path, outside this change because it needs an ownership-aware accumulator.

## Decisions

### Monitoring and diagnostics stay separate

`source.State` answers "can I trust these values". `source.Diagnostics` answers "what is the transport doing". Monitoring does not move, because it drives program behaviour (`WaitForSynchronizationAsync` blocks on `State`; `GetSourceState()` gates persistence), because it addresses differently (per property and per branch, not per connector), and because its members are load-bearing internals with a documented lock-free requirement (`ISubjectSource.cs:11-22`) that exists so `SourceMonitor` can read them under its own lock without an ABBA cycle.

`PendingWriteCount` is the one misfiled member. `connectors-monitoring.md:160` already documents it as orthogonal to `State`, describing the outbound retry queue. It moves; the rest stay.

A read-only `State` mirror on `SourceDiagnostics` was considered and rejected: it reintroduces the two-spellings pattern this change removes, and invites drift.

### `LastSynchronizedAt` becomes `StateChangeTime`

`_lastSynchronizedTicks` is stamped in one place, the transition **into** `Synchronized` (`SubjectSourceBase.cs:589-592`). So it records when the last good period began and cannot say when synchronization was lost. A source that synchronized a week ago and dropped an hour ago reports a week.

Revision 5 claimed `connectors-monitoring.md:160` was therefore wrong. That was overstated: the doc says the member "records when the most recent initial synchronization completed", which is exactly what the code does. The accurate criticism is narrower, that the doc's "stale, last confirmed at T" phrasing invites a reading the member cannot support.

`StateChangeTime` is stamped in `TransitionStateTo`, where the lock is already held and `now` is already computed for the `SourceEvent`, and at start so it is never null on a running source. Paired with the existing `State` it answers both questions: `Synchronized` plus T reads as in sync since T, `Synchronizing` plus T reads as stale since T.

**This is a trade, not a pure win.** After it, nothing reports when a currently stale source was last in sync. That is acceptable because the stale-duration question is the one operators ask during an incident, but it is a loss and should not be sold as a fix. It does not extend to `Unclaimed`, which `SourceState.cs:9-10` documents is only ever returned by the property-level API and never by a source.

This and `PendingWriteCount` are the only places the change touches `ISubjectSource`.

### One liveness spelling

**`IsOperational` and `OperationalChangeTime` are the only liveness members.** `IsConnected`, `IsRunning` and `IsListening` are removed, as are the duplicate spellings on the connector classes themselves: `MqttSubjectServer.NumberOfClients`, and `WebSocketSubjectServer.ConnectionCount` and `CurrentSequence`. `IsReconnecting` survives as a distinct sub-state. `PollingDiagnostics.IsRunning` survives as a sub-component's state, with a doc note so the rule does not look unevenly applied.

**Each connector must define its own operational predicate, and this is user-visible.** `HomeBlaze.OpcUa/OpcUaClient.cs:225` currently surfaces `IsConnected` (transport up, not reconnecting) as a device state property, so whatever the OPC UA client chooses replaces that meaning. The predicate for each of the six connectors is an implementation decision the plan must make explicitly, not infer.

### Metrics are mutable and connector-owned; diagnostics are a read-only view

Every mutable piece lives on a `*Metrics` object the connector holds and never exposes, and every readable piece lives on a `*Diagnostics` view reachable through `ISubjectConnector`. `ConnectorMetrics` carries the liveness state, the start epoch, the last error, the outbound change queue's `QueueMetrics` and the throughput counters, and exposes `MarkStarted()`, `MarkOperational()`, `MarkNotOperational()` and `ReportError(Exception)`. `SourceMetrics` adds the retry queue and inbound buffer metrics. `ConnectorDiagnostics` and `SourceDiagnostics` are constructed from them and forward.

This is the pattern the design already uses twice, in `QueueMetrics`/`QueueDiagnostics` and `ThroughputCounter`/`ThroughputDiagnostics`, so it is one idea applied three times rather than two competing ones.

Revision 6 put the mutators on `ConnectorDiagnostics` itself. That is not shippable: `Diagnostics` is reachable from `ISubjectConnector`, so any consumer could flip another connector's liveness or inject a fake error. An explicitly implemented writer interface was considered as a cheaper alternative, one interface and no new types, but it hides rather than prevents, since a cast defeats it. Two small types buy ownership that is structural rather than conventional.

It also solves the constructor problem more cleanly than revision 6 did. A hand-written implementer creates a `ConnectorMetrics`, hands it to a `ConnectorDiagnostics`, and exposes only the latter.

Revision 5 put these on the base class and typed the diagnostics constructors as taking the base. That made `ISubjectConnector` unimplementable by the five plain classes that implement it today (`SourceSubscriptionTests.cs:245`, `SubjectSourceExtensionsTests.cs:500`, `SourceMonitorTests.cs:601`, `SubjectTransactionBenchmark.cs:109`, `FaultTargetResolverTests.cs:21`), while the interface required a non-null `Diagnostics`. `docs/connectors.md:277` documents direct implementation as supported, so this was not only a test problem.

**`LastError` is not sticky today and going sticky is a behaviour change.** The OPC UA client clears it on every successful reconnection (`ClearLastError` at `OpcUaSubjectClientSource.cs:49`, called from `SessionManager.cs:413`, plus null-writes at `:156` and `:505`), the server clears it at `OpcUaSubjectServer.cs:269`, and `OpcUaClientDiagnostics.cs:98-103` documents the clearing contract. Revision 5 said "it stays sticky" as though that were the status quo. Sticky is still the right choice, since a cleared error erases the only evidence of a transient fault, but it is a change: `ClearLastError`, three null-writes and a documented contract are removed.

### Full break, no forwarding shims

Existing names move rather than being preserved or obsoleted. A deliberate call: the doubled surface of a deprecation cycle is not worth carrying for a diagnostics API.

## Type model

```csharp
// Namotion.Interceptor.Connectors

// --- write side: connector-owned, never reachable through the interfaces ---

public class ConnectorMetrics
{
    public ConnectorMetrics(ThroughputCounter? incoming = null, ThroughputCounter? outgoing = null);

    public QueueMetrics OutboundChanges { get; }

    public void MarkStarted();          // once per ExecuteAsync entry; moves the totals epoch
    public void MarkOperational();
    public void MarkNotOperational();
    public void ReportError(Exception error);
}

public class SourceMetrics : ConnectorMetrics
{
    public QueueMetrics OutboundRetries { get; }
    public QueueMetrics InboundBuffer { get; }
}

// --- read side: exposed through ISubjectConnector.Diagnostics ---

public class ConnectorDiagnostics
{
    public ConnectorDiagnostics(ConnectorMetrics metrics);

    public bool IsOperational { get; }
    public DateTimeOffset? OperationalChangeTime { get; }  // moves with IsOperational
    public Exception? LastError { get; }                   // sticky

    public DateTimeOffset? StartTime { get; }              // totals epoch

    public ThroughputDiagnostics Throughput { get; }
    public QueueDiagnostics OutboundChanges { get; }
}

public class SourceDiagnostics : ConnectorDiagnostics
{
    public SourceDiagnostics(SourceMetrics metrics);

    public QueueDiagnostics OutboundRetries { get; }
    public QueueDiagnostics InboundBuffer { get; }
}

public sealed class QueueDiagnostics                       // read-only view over QueueMetrics
{
    public int Depth { get; }                              // approximate; 0 when no buffer exists
    public int? Capacity { get; }                          // null unbounded, 0 disabled
    public long TotalDropped { get; }                      // monotonic
}

public sealed class ThroughputDiagnostics
{
    public ThroughputDiagnostics(ThroughputCounter? incoming, ThroughputCounter? outgoing);
    public static ThroughputDiagnostics NotInstrumented { get; }

    public double? IncomingPerSecond { get; }              // null when not instrumented
    public double? OutgoingPerSecond { get; }
}

public sealed class QueueMetrics
{
    public void Register(Func<int> depth, Func<long>? dropped, int? capacity);
    public void Deregister();                              // folds the live count in, then clears
    public void AddDropped(long count);
}
```

```csharp
// Namotion.Interceptor.OpcUa

public sealed class OpcUaClientDiagnostics : SourceDiagnostics
{
    public bool IsReconnecting { get; }
    public string? SessionId { get; }
    public int SubscriptionCount { get; }
    public int MonitoredItemCount { get; }

    public ReconnectDiagnostics Reconnects { get; }
    public PollingDiagnostics? Polling { get; }                // null when polling fallback is off
    public ReadAfterWriteDiagnostics? ReadAfterWrite { get; }  // null when read-after-write is off
}

public sealed class ReconnectDiagnostics
{
    public DateTimeOffset? LastConnectionTime { get; }   // survives a disconnect
    public long TotalAttempts { get; }
    public long TotalSucceeded { get; }
    public long TotalFailed { get; }
    public long TotalAbandoned { get; }
}

public sealed class OpcUaServerDiagnostics : ConnectorDiagnostics
{
    public int ActiveSessionCount { get; }
    public int ConsecutiveFailures { get; }                    // gauge, resets on successful start
}
```

```csharp
// Namotion.Interceptor.Mqtt / .WebSocket
// The client sources expose SourceDiagnostics directly. Only the servers add members.

public sealed class MqttServerDiagnostics : ConnectorDiagnostics
{
    public int ConnectedClientCount { get; }
}

public sealed class WebSocketServerDiagnostics : ConnectorDiagnostics
{
    public int ConnectionCount { get; }
    public long CurrentSequence { get; }
}
```

### `Diagnostics` is abstract with a single construction point

Revision 5 proposed virtual auto-properties. Compiled under this repo's settings (`src/Directory.Build.props:3-4`, nullable enabled plus warnings as errors) that produces `CS0108` because `ISubjectSource.Diagnostics` hides the inherited interface member, and two `CS8618` because the overrides are never assigned. It also allocates one diagnostics object per inheritance level, two of them dead for an OPC UA client.

Abstract on both bases, concrete only at the leaf, so exactly one object exists:

```csharp
public interface ISubjectConnector           { ConnectorDiagnostics Diagnostics { get; } }
public interface ISubjectSource : ISubjectConnector { new SourceDiagnostics Diagnostics { get; } }

// SubjectConnectorBase
public abstract ConnectorDiagnostics Diagnostics { get; }
ConnectorDiagnostics ISubjectConnector.Diagnostics => Diagnostics;

// SubjectSourceBase
public abstract override SourceDiagnostics Diagnostics { get; }
SourceDiagnostics ISubjectSource.Diagnostics => Diagnostics;

// OpcUaSubjectClientSource, the single construction point
public override OpcUaClientDiagnostics Diagnostics { get; } = new(...);
```

The `new` on the interface member is required and was missing. Consumers never cast, but the forwarder is an interface dispatch plus a virtual call, not the free read revision 5 claimed.

### `SubjectConnectorBase` seals `ExecuteAsync`

Revision 5 said servers "change base class" and the base would force liveness false on fault, exit and dispose. That is true for sources, whose `ExecuteAsync` is already `sealed override` with the cited catch (`SubjectSourceBase.cs:249-255`) and finally (`:258-261`). It is false for the three servers, each of which owns its own loop with its own finally (`OpcUaSubjectServer.cs:210`, `MqttSubjectServer.cs:146`, `WebSocketSubjectServer.cs:80`). A faulting server would have kept reporting operational, which is the defect used to reject the pull model.

So the base seals `ExecuteAsync` and hands down a template method:

```csharp
protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    Metrics.MarkStarted();
    try                { await RunAsync(stoppingToken); }
    catch (Exception e){ Metrics.ReportError(e); throw; }
    finally            { Metrics.MarkNotOperational(); }
}

protected abstract Task RunAsync(CancellationToken stoppingToken);
```

Each server renames its `ExecuteAsync` to `RunAsync`; `SubjectSourceBase` moves its sealed body into `RunAsync`. This also settles what revision 5 left contradictory: `MarkStarted` is **not** idempotent, it stamps once per `ExecuteAsync` entry, which is exactly a connector restart. The servers' internal restart loops live inside `RunAsync` and do not re-enter it, so a transport reconnect does not move the epoch while a host stop/start does, which is the intended semantics.

`SubjectConnectorBase` must derive from `BackgroundService`; all six connectors already do. Dispose-time forcing works because all four `DisposeAsync` implementations chain to `Dispose()` (`MqttSubjectServer.cs:655`, `WebSocketSubjectServer.cs:224`, `OpcUaSubjectClientSource.cs:731`, `WebSocketSubjectClientSource.cs:713`).

### Atomic state, not separate fields

Three places pair a value with a timestamp or an accumulator, and all three must be read consistently without a lock. Each stores one immutable record swapped with `Interlocked.Exchange` and read with a single volatile read:

- `(IsOperational, OperationalChangeTime)`, otherwise a reader can see the new flag with the previous timestamp, reporting "operational since the moment it went down".
- `(State, StateChangeTime)` in `SubjectSourceBase`, same hazard.
- `QueueMetrics`'s `(accumulated, dropProvider, depthProvider, capacity)`. Revision 5 specified separate fields with a lock only on deregistration, which cannot be lock-free, monotonic and non-double-counting at once: reading accumulated then provider can decrease across a deregistration, and the opposite order double counts. The spec's own test would catch it.

`TotalDropped` is then `snapshot.Accumulated + (snapshot.Dropped?.Invoke() ?? 0)`, so it advances during a burst rather than jumping at reconnect, and `Deregister` publishes a new record with the live count folded in and the providers cleared.

### Naming: direction, now that there is an inbound buffer

Earlier revisions named the queues `ChangeQueue` and `WriteRetries` and rejected direction prefixes, on the argument that both were outbound and no inbound counterpart existed. That argument was false. `SubjectPropertyWriter.cs:22` holds `private List<Action>? _updates = []`, and `StartBuffering` (`:51-61`) replaces that list *and* bumps the generation; the generation is the stale-snapshot guard, not the suppression mechanism. There is a real unbounded inbound buffer.

With three buffers across two directions, direction does disambiguating work: `OutboundChanges`, `OutboundRetries`, `InboundBuffer`.

`InboundBuffer.TotalDropped` counts buffers discarded by a superseding `StartBuffering`. Those discards are deliberate rather than data loss, since the generation guard exists because applying a stale snapshot would be wrong, and the member documents that distinction. It is worth counting because it is the only signal of how often initial loads are being superseded, which is reconnect thrash.

### The existing sub-blocks are renamed to the same convention

Eight cumulative counters omit the `Total` marker, so a reader cannot tell them from gauges. `TotalReads` renames for a different reason, so nine members move in total.

| Today | Becomes |
|---|---|
| `PollingDiagnostics.TotalReads` | `TotalSuccessfulReads` |
| `PollingDiagnostics.FailedReads` | `TotalFailedReads` |
| `PollingDiagnostics.ValueChanges` | `TotalValueChanges` |
| `PollingDiagnostics.SlowPolls` | `TotalSlowPolls` |
| `PollingDiagnostics.CircuitBreakerTrips` | `TotalCircuitBreakerTrips` |
| `ReadAfterWriteDiagnostics.Scheduled` | `TotalScheduledReads` |
| `ReadAfterWriteDiagnostics.Executed` | `TotalExecutedReads` |
| `ReadAfterWriteDiagnostics.Coalesced` | `TotalCoalescedReads` |
| `ReadAfterWriteDiagnostics.Failed` | `TotalFailedReads` |

`TotalReads` counts successful reads only (`PollingManager.cs:364-366`), so beside a new `TotalFailedReads` it would read as the sum.

The `ReadAfterWrite` members name their noun because that block name contains both "read" and "write", so a bare `TotalFailed` there reads as a failed write. `Reconnects` needs no such treatment.

`Total` is a prefix, matching the library's own usage: `TotalReads`, `TotalAttempts`, `TotalReconnectionAttempts`, with no suffix usage. A suffix was considered for matching Prometheus `_total`; that fails because both known consumers rename these into their own metric names rather than mapping one-to-one.

### The timestamp naming rule

No `*Since` anywhere. That vocabulary appears nowhere in this library, which uses `LastConnectedAt`, `RecoveredAt`, `DisruptedAt`, `ReadAt`, `StartTime` and several `*Timestamp`, and nowhere in .NET, which has `Process.StartTime` and `ExitTime`, `FileInfo.CreationTime` and `LastWriteTime`, and `Activity.StartTimeUtc`. The closest ecosystem analogue for "when the current state began" is Kubernetes' `lastTransitionTime`.

- **A timestamp without `Last`** pairs with a state member and moves whenever it moves: `State`/`StateChangeTime`, `IsOperational`/`OperationalChangeTime`. Non-null once the connector has started, which is why both are stamped at start rather than only on the first transition. Without that, a source that never leaves its initial `Synchronizing` (`SubjectSourceBase.cs:34`, with `TransitionStateTo` early-returning on a no-op at `:581-584`) would report null in exactly the "stale since when" case the member exists for.
- **A timestamp with `Last`** records a discrete past event and survives it: `Reconnects.LastConnectionTime`.

`StartTime` follows the BCL noun-phrase form (`Process.StartTime`) and is the name being deleted from `OpcUaServerDiagnostics`, so a reader migrating finds what they knew, and is the epoch every `Total*` counts from.

The discriminator is whether the value survives the state it describes.

### Documentation obligations

1. **`ThroughputDiagnostics` states the reference frame once.** Incoming means into the subject tree, outgoing means out of it, for clients and servers alike.
2. **`LastError` covers either direction, and is sticky**, which is a change from the OPC UA connectors' current clear-on-recovery behaviour.
3. **The three buffers document their relationship.** `OutboundChanges` growing means changes are produced faster than they flush; `OutboundRetries` growing means the far end is rejecting writes; `InboundBuffer` growing means an initial load is still in progress.
4. **`ReadAfterWrite` notes its counters are verification reads following an outbound write.**
5. **`InboundBuffer.TotalDropped` documents that discards are deliberate**, not loss.

**The implementation PR description must carry the full member tree**, base types first and each connector's additions under it, marking gauges and `Total*` counters. The tree is what made three naming defects visible during design; prose surfaced none of them.

### The counter convention

**A `Total` prefix means monotonic since `StartTime`, never rebased.** Anything that resets carries no `Total`, which is why `ConsecutiveFailures` keeps its name.

## Ownership, lifetime and accuracy

**Accumulator plus view.** A mutable `QueueMetrics` is owned for the connector's lifetime; `QueueDiagnostics` is a read-only view. This is what makes `TotalDropped` survive a per-cycle `ChangeQueueProcessor`.

**Depth and drops come from `ChangeQueueProcessor`, not from the subscription**, whose count is process-wide for the reason given above.

**Register takes an optional drop accessor.** The change queue supplies both; `WriteRetryQueue` has no drop counter of its own and reports through `AddDropped`, so it passes `null` and would otherwise force implementers to guess between `() => 0` and adding a counter that then double-counts.

**Processor handover.** `Register` on creation, `Deregister` before disposal. There are **five** creation sites, not four: `SubjectSourceBase.cs:233`, `OpcUaSubjectServer.cs:262`, `MqttSubjectServer.cs:188`, `WebSocketSubjectServer.cs:98`, and `WebSocketSubjectChangeProcessor.cs:33`. The last two share the factory `WebSocketSubjectHandler.CreateChangeQueueProcessor` (`:365`), so registration must live at the call site or embedded mode would wire itself into the server's metrics. All are `using var` today and become try/finally so deregistration precedes disposal.

Clearing the providers before disposal narrows rather than closes the race: a reader can read a non-null provider and be preempted. That is safe only because `_changes` and `_dropCount` survive `ChangeQueueProcessor.Dispose`, which is a dependency to state rather than rely on silently.

**The inbound buffer count must not take a lock.** `_updates` is guarded by `SubjectPropertyWriter`'s `Lock`, and `StartBuffering` holds it while calling `TransitionStateTo`, which takes `_stateLock` and raises `StateChanged`; a handler reading a lock-taking getter would close an ABBA cycle, the same hazard `ISubjectSource` already documents. A `volatile int` maintained where the list is mutated, all of which already happens under that lock, and a `Volatile.Read` on the getter.

**Timestamps are stored as interlocked ticks.** `DateTimeOffset?` has no atomic read, and AGENTS.md names torn reads as correctness. The repository already uses tick storage under `Interlocked` (`SubjectSourceBase.cs:531`, `ReconnectionMetrics.cs:19-26`).

**All reads are lock-free and none may throw.** Lock-free is not cheap: `ConcurrentQueue<T>.Count` is a segment walk, so `Depth` should not be polled tightly.

**`Depth` reads 0 where no buffer exists**, between connect cycles and whenever `bufferTime <= 0`, since `ChangeQueueProcessor` takes an immediate path then and never enqueues.

**`Capacity` is ambiguous at 0** between "queue not constructed" and "constructed with `maxQueueDepth: 0`", which drops everything immediately (`ChangeQueueProcessor.cs:269-272`). The plan should either forbid the latter at construction or document the collision.

**The direct-write discard is attributed to `OutboundRetries`**, which runs only when the retry queue is absent, so that block reports `Capacity == 0`, `Depth == 0` and a rising `TotalDropped`. Deliberate, and stated because it looks wrong otherwise.

## Production changes beyond plumbing

**Drop counting on the three live outbound paths.** The immediate value of the change.

**Liveness does not exist today.** Each connector calls `MarkOperational()` where it becomes healthy and `MarkNotOperational()` where it loses that; the base forces the latter on fault, exit and dispose. For the OPC UA client the transition points live in `SessionManager`, outside the inheritance hierarchy, so it needs the internal-forwarder pattern already used at `OpcUaSubjectClientSource.cs:52`. MQTT is simpler, since `OnDisconnectedAsync` and `OnReconnectedAsync` are on the source.

**`StateChangeTime`** replaces the conditional stamp in `TransitionStateTo` with an unconditional one, plus a stamp at start, and deletes `_lastSynchronizedTicks`.

**`LastError` state for MQTT and WebSocket**, neither of which tracks one, and removal of the OPC UA clearing paths.

**The inbound buffer count** in `SubjectPropertyWriter`.

**The OPC UA server keeps its throughput counters** (`:206`, `:418`), read by `HomeBlaze.OpcUa/OpcUaServer.cs:191-193` and asserted by the #425 regression tests. `StartTime` is not converted but replaced: it is nulled in the restart finally at `:274`, a per-run window, whereas `StartTime` never moves on an internal restart.

## Scope boundaries

**Outgoing throughput is not moved to the base.** `SubjectSourceBase.WriteChangesViaRetryQueueAsync` does not sit on every outbound path: the retry flush calls `WriteChangesInBatchesAsync` directly (`WriteRetryQueue.cs:153`), and so does `SourceTransactionWriter` (`:164`, `:375`, `:416`). It also needed a written count on `WriteResult`, whose `Success` is a shared static (`WriteResult.cs:45`) returned by eight production sites, so the base would read zero written on every fully-successful path.

Also out: incoming throughput for MQTT and WebSocket and both directions for their servers; making `LastError` self-clearing; bounding any queue (#281, gated on #352); the unclaimed-property discard.

## Breaking changes and migration

**Removed or moved**: `ISubjectSource.PendingWriteCount` and `LastSynchronizedAt`; on `OpcUaClientDiagnostics` the throughput pair, `LastError`, `IsConnected`, `PendingWriteCount`, `PendingReadAfterWrites`, `PollingItemCount`, the four reconnect counters and `LastConnectedAt`; on `OpcUaServerDiagnostics` the throughput pair, `LastError`, `IsRunning`, `StartTime`, `Uptime`; `MqttSubjectServer.IsListening` and `NumberOfClients`; `WebSocketSubjectServer.ConnectionCount` and `CurrentSequence`; nine renamed sub-block counters; `OpcUaSubjectClientSource.ClearLastError`.

**Added**: `ConnectorMetrics`, `SourceMetrics`, `ConnectorDiagnostics`, `SourceDiagnostics`, `QueueDiagnostics`, `QueueMetrics`, `ThroughputDiagnostics`, `ReconnectDiagnostics`, `SubjectConnectorBase`, `MqttServerDiagnostics`, `WebSocketServerDiagnostics`, `Diagnostics` on both interfaces, `StateChangeTime` on `ISubjectSource`.

**In-repo fallout**:

| Where | What |
|---|---|
| `HomeBlaze.OpcUa/OpcUaClient.cs:221-227`, `:311-318` | seven reads plus the null-out block; `IsConnected` is surfaced as a device state and changes meaning |
| `HomeBlaze.OpcUa/OpcUaServer.cs:191-193` | throughput pair and `ActiveSessionCount` |
| `Connectors.Tests/SourceStateTests.cs:71-84`, `:96`, `:105`, `:109-168` | the only file pinning `LastSynchronizedAt` semantics, including a 60-line concurrency test |
| `Connectors.Tests/SourceSubscriptionTests.cs:245`, `SubjectSourceExtensionsTests.cs:500`, `SourceMonitorTests.cs:601` | direct `ISubjectSource` implementers |
| `Connectors.Tests/SubjectSourceRetryQueueTests.cs:74` | `source.PendingWriteCount` |
| `Benchmark/SubjectTransactionBenchmark.cs:109` | fake `ISubjectSource` |
| `ConnectorTester.Tests/Connectors/FaultTargetResolverTests.cs:21` | hand-written `ISubjectConnector` |
| `WebSocket.Tests/WebSocketServerClientTests.cs:308,311,321,322,337,340,357,361` | `ConnectionCount` / `CurrentSequence` |
| `WebSocket.Tests/SequenceNumberTests.cs:78,94,97,132,135,138,141,375,444,498,501,504,549,552` | same |
| `WebSocket.Tests/Integration/OutageStateTests.cs:73,89` | `LastSynchronizedAt` |
| `OpcUa.Tests/Client/OutageStateTests.cs:105,132` | `LastSynchronizedAt` |
| OPC UA tests | roughly 40 reads of removed members across `OpcUaReconnectionTests`, `OpcUaStallDetectionTests`, `OpcUaConcurrencyTests`, `OpcUaReadWriteTests`, `OpcUaServerSelfWriteTests.cs:53,82,88`, `SelfEchoReproTests.cs:188` |
| five processor creation sites | `using var` becomes try/finally |
| three server classes | base becomes `SubjectConnectorBase`, `ExecuteAsync` renamed to `RunAsync` |

`OpcUaServerSelfWriteTests` and `SelfEchoReproTests` assert incoming throughput is 0 and are the #425 regression tests; `OpcUaReadWriteTests.cs:74-75` asserts the positive mirror.

**Snapshots**: Connectors, OpcUa, Mqtt (`Mqtt.Tests/…verified.txt:166,169,170`). WebSocket has none despite `WebSocketSubjectServer` being public: a pre-existing gap worth closing here, since this change moves its surface.

**Docs**: `connectors-opcua-client.md:648`, `:650`, `:746`, `:747`, `:750`, `:769`, `:774`, `:775`, `:776`, `:782`, `:784`; `connectors-opcua-server.md:259`; `connectors-opcua.md:48`, `:76`; `connectors.md:271`, `:272`, `:277`, `:783`, `:785`; `connectors-monitoring.md:160`; and `HomeBlaze/Data/Docs/architecture/design/observability.md:58`, which documents the server diagnostics surface.

## Error handling

**No diagnostics getter may throw.** A cleared provider reads `0`, a missing session manager makes `Polling` and `ReadAfterWrite` null, a never-started connector has `StartTime` null.

**A disposed or faulted connector reports not operational**, with `OperationalChangeTime` stamped at that moment.

**A restart is a new epoch.** `StartTime` moves on `ExecuteAsync` re-entry and every `Total*` resets with it; a transport reconnect inside `RunAsync` does not move it.

**`Capacity`**: `null` unbounded, `0` disabled, with the collision noted above.

**Null throughput is construction-time.** `ThroughputCounter.CurrentRate` returns `0.0` when idle (`ThroughputCounter.cs:55`), so null is the only way to distinguish idle from not-measured.

## Testing

Repo conventions: `When<Condition>_Then<ExpectedBehavior>`, explicit Arrange/Act/Assert, no hardcoded waits.

- **The three live outbound drop paths**, each failing when its `Interlocked.Add` is removed.
- **The disabled-queue drain does not count**, pinning the over-report an earlier revision would have shipped.
- **`TotalDropped` advances during a burst**, never decreases across a handover, and does not double-count. Written against `QueueMetrics` and a bounded processor directly, since no in-repo connector sets a bound.
- **A faulted, a disposed and a stopped connector all report not operational**, for a server as well as a source, since the server path is the one revision 5 got wrong.
- **`StateChangeTime` moves on every transition**, and is non-null on a source that never leaves its initial state.
- **The `(value, timestamp)` pairs are never observed torn.**
- **Concurrency**: a reader loop over every property while a writer loop recreates the processor.
- **Snapshot and migration**: the #425 regression assertions preserved through their new path.

Left to integration suites: per-connector liveness transitions for MQTT and WebSocket.

## Follow-ups

- Throughput for the MQTT and WebSocket servers, and incoming for all four.
- Counting the unclaimed-property discard.
- Making `WebSocketSubjectChangeProcessor` a connector so embedded mode reports its own metrics.
- #281 and #352: bounding the queues.
- Permanent documentation is updated to the implemented design as part of implementation; this spec is temporary.
