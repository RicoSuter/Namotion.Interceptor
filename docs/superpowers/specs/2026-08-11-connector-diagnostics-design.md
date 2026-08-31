# Connector diagnostics: a shared, grouped model

Status: designed, not implemented. Closes #277.

Revision 5. Revisions 1 and 3 were each reviewed adversarially. Between them the reviews found two factual errors about existing code and a cluster of implementation defects, all corrected here. Two arguments the reviews made are answered rather than accepted, in "Why drop counting matters despite being zero in-repo" and in the naming section, so the next reader does not relitigate them.

Line citations were verified against the tree at the time of writing. Freeze the file before implementation, since they drift.

## Problem

Connector diagnostics are defined per connector, but most of what they report is connector-agnostic. Five consequences:

1. **Diagnostics exist only for OPC UA.** MQTT and WebSocket have no diagnostics type, so what an operator can learn depends on which connector they use.
2. **Client and server name the same concepts differently.** Liveness is `IsConnected` on the OPC UA client, `IsRunning` on the OPC UA server, `IsListening` on the MQTT server. Failure history is four reconnect counters on the client and `ConsecutiveFailures` on the server. The server has `StartTime` and `Uptime`; the client has `LastConnectedAt` and no uptime.
3. **The layering is inverted.** The retry queue, the change queue and the throughput counters live in `Namotion.Interceptor.Connectors`, but there is no diagnostics surface at that layer, so every number is re-exposed per connector. This is why #277 reads as a "cross-layer bridging" problem: it is a missing abstraction, not a bridging problem.
4. **Sub-blocks are applied inconsistently.** Read-after-write has both a nested `ReadAfterWrite` block and a flat `PendingReadAfterWrites`. Polling has the same duplication via `PollingItemCount`.
5. **Outbound writes are dropped and nothing is counted.** Three retry-queue paths drop today; a fourth fires only for connectors that bound their change queue.

## Why drop counting matters despite being zero in-repo

Every in-repo connector constructs `ChangeQueueProcessor` with `maxQueueDepth: null` (`SubjectSourceBase.cs:240`, `OpcUaSubjectServer.cs:136`, `MqttSubjectServer.cs:103`, `WebSocketSubjectHandler.cs:375`), so `ChangeQueueProcessor.DropCount` is always 0 here. A review argued from this that the machinery should be cut. It should not:

- `maxQueueDepth` is a **public constructor parameter**. Any consumer-written connector can bound its queue today, and bounding an unbounded queue feeding a slow sink is the correct advice. The counter is live for them on day one.
- The processor is created **per connect cycle**, so a naive implementation rebases the count on every reconnect: a metric that silently resets exactly when it matters. The accumulator must exist before the first bounded connector, not after.

Independently of any bound, **three retry-queue paths drop data today and only log it**:

- `WriteRetryQueue` ring-buffer overflow (`WriteRetryQueue.cs:71-87`).
- `ReconcileRetryQueueAsync` no-setter and per-change-exception branches (`SubjectSourceBase.cs:486-499`).
- The direct-write discard (`SubjectSourceBase.cs:270-287`).

These are unconditional outbound data loss in normal operation and are the immediate value of this change.

**Deliberately not counted**: the disabled-queue drain at `SubjectSourceBase.cs:345-351`. Revision 3 counted it. That branch drains the *entire* subscription with no ownership filter (the filter is only in the else-branch at `:356-367`), and the subscription carries context-wide changes including other sources' properties and this source's own inbound applies. Counting it would report other sources' traffic as this source's lost writes.

**Also not counted, and worth an issue rather than a silent omission**: `connectors.md:783` documents that writes to properties a source has not claimed yet are discarded (`SubjectSourceBase.cs:364-367`). That is a fifth loss path, outside this change because it needs an ownership-aware accumulator.

## Decisions

### Monitoring and diagnostics stay separate

`source.State` answers "can I trust these values". `source.Diagnostics` answers "what is the transport doing". Monitoring does not move, because it drives program behaviour (`WaitForSynchronizationAsync` blocks on `State`; `GetSourceState()` gates persistence), because it addresses differently (per property and per branch, not per connector), and because its members are load-bearing internals with a documented lock-free requirement (`ISubjectSource.cs:11-22`) that exists so `SourceMonitor` can read them under its own lock without an ABBA cycle.

`PendingWriteCount` is the one misfiled member. `connectors-monitoring.md:160` already documents it as orthogonal to `State`, describing the outbound retry queue. It moves; the rest stay.

A read-only `State` mirror on `SourceDiagnostics` was considered and rejected: it reintroduces the two-spellings pattern this change removes, and invites drift. Docs cross-reference instead.

**`LastSynchronizedAt` is replaced by `StateChangedAt`, and this is a behaviour fix rather than a rename.** `_lastSynchronizedTicks` is stamped in exactly one place, the transition **into** `Synchronized` (`SubjectSourceBase.cs:589-592`). It is never updated while synchronized and never updated when synchronization is lost, so it records when the last good period *began*, not when it ended. A source that synchronized a week ago and dropped an hour ago reports a week. `connectors-monitoring.md:160` promises the opposite, that a `Synchronizing` source can be reported as "stale, last confirmed at T", and the implementation has never supported that claim. The doc is wrong and must be corrected regardless of what we do to the member.

`StateChangedAt` is stamped unconditionally in `TransitionStateTo`, where the lock is already held and `now` is already computed for the `SourceEvent`. Paired with the existing `State` it answers both questions the old member could not: `Synchronized` with `StateChangedAt` reads as in sync since T, and `Synchronizing` with `StateChangedAt` reads as stale since T. It also extends to `Stopped` and `Unclaimed`, which a synchronization-specific member could not.

`SynchronizedSince` with null-while-unsynchronized was considered. It fixes the name but discards the ability to say how long a source has been stale, which is what an operator actually wants during an outage, and it would compile everywhere while silently turning an existing "last confirmed at T" display into "never synchronized" during exactly that outage.

This is the one place the change reaches into `ISubjectSource` beyond `PendingWriteCount`.

### One liveness spelling, applied consistently

**`IsOperational` and `OperationalChangedAt` are the only liveness members.** `IsConnected`, `IsRunning` and `IsListening` are all removed. `IsReconnecting` survives because it is a distinct sub-state, not a second spelling. Counts such as `ActiveSessionCount` are not liveness and are unaffected.

The same rule removes three duplicate spellings left on the connector classes themselves: `MqttSubjectServer.NumberOfClients` (duplicating `ConnectedClientCount`), and `WebSocketSubjectServer.ConnectionCount` and `CurrentSequence` (duplicating the identically named diagnostics members).

`PollingDiagnostics.IsRunning` survives, because it is a sub-component's state rather than connector liveness. It needs a doc note so the rule does not look unevenly applied.

### The base owns liveness and last error; connectors push

`ConnectorDiagnostics` holds `OperationalChangedAt` and `LastError` as its own state, and `SubjectConnectorBase` exposes `MarkOperational()`, `MarkNotOperational()` and `ReportError(Exception)`. Connectors call them at their transition points.

The alternative, abstract members each connector pulls from its own state, was in revision 3 and is worse for two reasons. First, it forced every source to define a concrete diagnostics type even when it added nothing, so `MqttClientDiagnostics` and `WebSocketClientDiagnostics` existed purely to satisfy the abstract members. With push, both disappear and those sources expose `SourceDiagnostics` directly. Second, `OperationalChangedAt` must be cleared on paths the connector does not own: the base's catch (`SubjectSourceBase.cs:249-255`), its `finally` (`:258-261`) and `Dispose()` (`:618`). A pulled member cannot be cleared there. Revision 3 delegated clearing to `ReportConnectionLost()`, which only OPC UA calls, so an MQTT or WebSocket source that faulted or was disposed would have kept reporting operational.

Consequence: `ConnectorDiagnostics` and `SourceDiagnostics` are concrete, not abstract.

### Generic on the connector, specific on the implementation

Servers are not sources. The split is therefore not client versus server:

- **Every connector**: liveness, last error, start epoch, throughput, outbound change queue.
- **Sources only**: the write retry queue.
- **Protocol-specific**: sessions, subscriptions, monitored items, polling, read-after-write, active sessions.

### Full break, no forwarding shims

Existing names move rather than being preserved or obsoleted. A deliberate call: the doubled surface of a deprecation cycle is not worth carrying for a diagnostics API.

## Type model

```csharp
// Namotion.Interceptor.Connectors

public class ConnectorDiagnostics
{
    public ConnectorDiagnostics(SubjectConnectorBase owner, ThroughputDiagnostics throughput);

    public bool IsOperational { get; }                 // owner pushes; the base forces it false
                                                       // on fault, exit and dispose
    public DateTimeOffset? OperationalChangedAt { get; }   // when IsOperational last flipped;
                                                       // null only before the first start
    public Exception? LastError { get; }               // sticky, either direction

    public DateTimeOffset? StartedAt { get; }          // totals epoch, does not move on reconnect

    public ThroughputDiagnostics Throughput { get; }
    public QueueDiagnostics ChangeQueue { get; }
}

public class SourceDiagnostics : ConnectorDiagnostics
{
    public SourceDiagnostics(SubjectSourceBase owner, ThroughputDiagnostics throughput);

    public QueueDiagnostics WriteRetries { get; }
}

public sealed class QueueDiagnostics                   // read-only view over QueueMetrics
{
    public int Depth { get; }                          // approximate; 0 when no buffer exists
    public int? Capacity { get; }                      // null unbounded, 0 disabled
    public long TotalDropped { get; }                  // accumulated + live, monotonic
}

public sealed class ThroughputDiagnostics
{
    public ThroughputDiagnostics(ThroughputCounter? incoming, ThroughputCounter? outgoing);
    public static ThroughputDiagnostics NotInstrumented { get; }

    public double? IncomingPerSecond { get; }          // null when not instrumented
    public double? OutgoingPerSecond { get; }
}

public sealed class QueueMetrics
{
    public void Register(Func<int> depth, Func<long> dropped, int? capacity);
    public void Deregister();                          // folds the live drop count in, then clears
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
    public DateTimeOffset? LastConnectionEstablishedAt { get; }            // persists while down
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

### `Diagnostics` is a covariant property override, not a hidden auto-property

`OpcUaSubjectClientSource:62` and `OpcUaSubjectServer:72` already declare their own `Diagnostics`. A non-virtual property on the base would be member hiding: CS0108 under the solution's warnings-as-errors, and with `new` an interface caller would receive the base's separate instance.

C# 9 supports covariant returns on get-only **property overrides**, so the base declares it virtual and each connector overrides with its concrete type:

```csharp
// SubjectConnectorBase
public virtual ConnectorDiagnostics Diagnostics { get; }
ConnectorDiagnostics ISubjectConnector.Diagnostics => Diagnostics;

// SubjectSourceBase
public override SourceDiagnostics Diagnostics { get; }
SourceDiagnostics ISubjectSource.Diagnostics => Diagnostics;

// OpcUaSubjectClientSource
public override OpcUaClientDiagnostics Diagnostics { get; }
```

Interfaces genuinely have no covariant returns, which is why the explicit forwarders are needed at the interface level. Revision 3 used that fact to justify a non-virtual property, which foreclosed the class mechanism that solves the concrete level. Revision 1's claim that consumers "never cast, with no hop" was also wrong; the correct claim is that consumers never cast, and the forwarder is a non-virtual property read.

### Accessibility

Everything a connector in another assembly needs is public. `InternalsVisibleTo` on `Namotion.Interceptor.Connectors` covers test assemblies only, and all three servers build their processors from other assemblies, so an internal accessor would be unreachable for every one of them. Public: `QueueMetrics`, the `ConnectorDiagnostics`, `SourceDiagnostics` and `ThroughputDiagnostics` constructors, and the depth and drop accessors on `ChangeQueueProcessor`.

### Naming: purpose, not direction

Both queues are outbound and there is no inbound counterpart, so a direction prefix would disambiguate nothing. They are named after what they hold and match the internals a consumer can grep: `ChangeQueue` matches `ChangeQueueProcessor`, `WriteRetries` matches `WriteRetryQueue` and the `writeRetryQueueSize` knob. `WriteRetries` deliberately does not repeat the internal class name, since a `QueueDiagnostics`-typed property called `WriteRetryQueue` would read as an instance of that class.

Direction stays in the throughput names, where both directions exist and the name does disambiguating work.

Grouping both queues into one `Outbound` block with `Pending` and `Retries` was rejected: retries are source-only, and the split already encodes that in the type system rather than through a nullable member.

### The existing sub-blocks are renamed to the same convention

Eight cumulative counters in `PollingDiagnostics` and `ReadAfterWriteDiagnostics` omit the `Total` marker, so a reader cannot tell them from gauges.

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

`TotalReads` already carried the prefix but counts **successful** reads only (`PollingMetrics.RecordRead()` fires only on success), so beside a new `TotalFailedReads` it would read as the sum. It becomes `TotalSuccessfulReads`.

The `ReadAfterWrite` members name their noun because that block name contains both "read" and "write", so a bare `TotalFailed` there reads as a failed write. `Reconnects` needs no such treatment: its parent supplies an unambiguous noun.

`Total` is a prefix, matching the library's own usage. The honest evidence is three members, not the six revision 3 claimed: `TotalReads`, `TotalAttempts` and `TotalReconnectionAttempts`. Of the others, `TotalCount` is a structured-logging placeholder, and `TotalWrites` and `TotalFailureCount` are per-batch fields on `OpcUaWriteException` rather than counters. Three precedents and no suffix usage in the library still settle it, and the prefix reads as English. A suffix was considered for matching Prometheus `_total`; that argument fails because both known consumers rename these into their own metric names rather than mapping one-to-one.

### The timestamp naming rule

Two kinds, and no `*Since` anywhere. That vocabulary appears nowhere in this library, which uses `LastConnectedAt`, `RecoveredAt`, `DisruptedAt`, `ReadAt`, `StartTime` and several `*Timestamp` members, and nowhere in .NET, which has `Process.StartTime` and `ExitTime`, `FileInfo.CreationTime` and `LastWriteTime`, and `Activity.StartTimeUtc`. The closest ecosystem analogue for "when the current state began" is Kubernetes' `lastTransitionTime` on Conditions, which is also transition-named.

- **`*ChangedAt`** pairs with a state member and moves whenever that state moves: `State` with `StateChangedAt`, `IsOperational` with `OperationalChangedAt`. Non-null once the connector has started, so a consumer reads the state and how long it has held in one place.
- **`Last*At`** records a discrete past event and survives whatever came after it: `Reconnects.LastConnectionEstablishedAt`, which still answers "when did we last have a connection" while disconnected.

`StartedAt` is a `Last*At` in spirit, the connector's own start, and is the epoch every `Total*` counts from. It does not move on reconnect.

The distinction that matters is whether the value survives the state it describes. `LastConnectionEstablishedAt` does, which is why it takes event naming. A `*ChangedAt` cannot, because it always describes the present. Collapsing the two makes counters appear to rebase exactly when they matter most.

### Documentation obligations

Five things are not inferable from the surface:

1. **`ThroughputDiagnostics` states the reference frame once.** Incoming means into the subject tree, outgoing means out of it, for clients and servers alike.
2. **`LastError` covers either direction.**
3. **`ConnectorDiagnostics` says there is deliberately no inbound queue.** Inbound suppression is a generation bump in `SubjectPropertyWriter.StartBuffering`, not a buffer with a depth.
4. **`ChangeQueue` and `WriteRetries` document their pipeline relationship.** `ChangeQueue` growing means changes are produced faster than they flush; `WriteRetries` growing means the far end is rejecting writes.
5. **`ReadAfterWrite` notes its counters are verification reads following an outbound write.**

**The implementation PR description must carry the full member tree**, base types first and each connector's additions under it, marking gauges and `Total*` counters. The tree is what made two naming defects visible during design, the flat-versus-nested duplication and the eight convention violations. Prose surfaced neither.

### The counter convention

**A `Total` prefix means monotonic since `StartedAt`, never rebased.** Anything that resets carries no `Total`, which is why `ConsecutiveFailures` keeps its name.

## Ownership, lifetime and accuracy

**Accumulator plus view, following `ThroughputCounter`.** A mutable `QueueMetrics` is owned by the connector for its whole lifetime; `QueueDiagnostics` is a read-only view. This is what makes `TotalDropped` survive a per-cycle `ChangeQueueProcessor`.

**Depth and drops both come from `ChangeQueueProcessor`, not from the subscription.** Revision 1 read depth from the source-lifetime `PropertyChangeQueueSubscription`. That was wrong: `PropertyChangeInterceptor` fans every committed change to every queue subscription unfiltered (`:196`, `:244`), so its count is process-wide, not this connector's outbound buffer.

**`TotalDropped` is accumulated plus live.** `QueueMetrics.Register` takes both a depth and a drop accessor. The reported total is the accumulated value plus the live processor's count, so it advances during a burst rather than jumping at reconnect. `Deregister` folds the live count into the accumulator and clears the provider in one step under a lock, so the value can neither double-count nor momentarily decrease.

**Processor handover.** `Register` on creation, `Deregister` before disposal. All four creation sites currently use `using var` (`SubjectSourceBase.cs:233`, `OpcUaSubjectServer.cs:262`, `MqttSubjectServer.cs:188`, `WebSocketSubjectServer.cs:98`) and must become try/finally so deregistration precedes disposal. Clearing the provider before disposal narrows rather than closes the race: a reader can read a non-null provider and be preempted. That is safe only because the accessors read a `ConcurrentQueue<T>` that survives disposal, which is a dependency to state rather than rely on silently.

**`StartedAt` is stamped once by `MarkStarted()`**, which is idempotent. All three servers restart inside their own `ExecuteAsync` (`OpcUaSubjectServer.cs:239`, `MqttSubjectServer.cs:146`, `WebSocketSubjectServer.cs:82`), and the obvious call site sits inside that loop, which would move the epoch on every restart and rebase every counter. `SubjectSourceBase` calls it from its sealed `ExecuteAsync`; servers call it themselves and the idempotence makes placement forgiving.

**Timestamps are stored as interlocked ticks.** `DateTimeOffset?` is a multi-field struct with no atomic read, and AGENTS.md names torn reads as a correctness concern. The repository already uses tick storage under `Interlocked` (`SubjectSourceBase.cs:531`, `ReconnectionMetrics.cs:19-26`). The OPC UA server's plain `DateTimeOffset? _startTime` (`OpcUaSubjectServer.cs:36`) is converted.

**All reads are lock-free and none may throw.** Lock-free is not the same as cheap: `ConcurrentQueue<T>.Count` is a segment walk, so `Depth` is the one member a caller should not poll tightly.

**`Depth` is a snapshot; `TotalDropped` is monotonic.** An operator alarms on the total and glances at the depth.

**`Depth` reads 0 where no buffer exists**, which is both between connect cycles and whenever `bufferTime <= 0`. `ChangeQueueProcessor` takes an immediate path in that configuration and never enqueues, so there is nothing to measure and `Capacity` advertises a bound that is never enforced. Documented rather than hidden.

## Production changes beyond plumbing

**Drop counting on the three live paths**: `WriteRetryQueue` ring-buffer overflow, `ReconcileRetryQueueAsync`'s no-setter and exception branches, and the direct-write discard. Each gets an `Interlocked.Add` into the owning `QueueMetrics`. This is the immediate value of the change.

**Liveness does not exist today and must be derived.** The OPC UA client has `SessionManager.IsConnected` and `ReconnectionMetrics.LastConnectedAt` but no notion of a current healthy period, and computing one as `IsConnected ? LastConnectedAt : null` is wrong during the initial load and during `PerformFullStateSyncIfNeededAsync`. Each connector calls `MarkOperational()` where it becomes healthy and `MarkNotOperational()` where it loses that; the base additionally forces the latter on fault, exit and dispose. Both stamp `OperationalChangedAt`.

**`StateChangedAt` is one line plus a deletion.** `TransitionStateTo` already holds `_stateLock` and already computes `now` for the `SourceEvent`, so the conditional stamp on `Synchronized` becomes unconditional and `_lastSynchronizedTicks` is removed.

**`LastError` state for MQTT and WebSocket.** Neither client source nor either server tracks one today. The base captures what it sees; connectors call `ReportError` for failures it cannot.

**The OPC UA server keeps its throughput.** Revision 1 wrongly said servers cannot report it. `OpcUaSubjectServer` already counts both directions (`:206`, `:418`), read by `HomeBlaze.OpcUa/OpcUaServer.cs:191-193` and asserted by the #425 regression tests. Its counters are passed into `ThroughputDiagnostics` unchanged.

## Scope boundaries

**Outgoing throughput is not moved to the base.** Revision 3 moved it, on the belief that `SubjectSourceBase.WriteChangesViaRetryQueueAsync` sits on the outbound path for every source. It does not: the retry-queue flush calls `source.WriteChangesInBatchesAsync` directly (`WriteRetryQueue.cs:153`), and so does `SourceTransactionWriter` (`:164`, `:375`, `:416`). The mechanism also needed a written count on `WriteResult`, whose `Success` is a shared static (`WriteResult.cs:45`) returned by eight production sites and many test doubles, so the base would have read zero written on every fully-successful path. That is a public struct contract change on the hot write path, forced on every external implementer, bought for one rate. Each connector keeps its own counter; MQTT and WebSocket report null until someone wires them.

Also out:

- **Incoming throughput for MQTT and WebSocket, and both directions for their servers.** Nullable rates make the absence honest rather than reporting a misleading zero.
- **Embedded WebSocket mode.** `WebSocketSubjectChangeProcessor` is a plain `BackgroundService`, not an `ISubjectConnector`, so it has no owner for its metrics. Documented rather than left uneven.
- **The unclaimed-property discard** (`SubjectSourceBase.cs:364-367`), which needs an ownership-aware accumulator.
- **Making `LastError` self-clearing.** It stays sticky.
- **Bounding any queue.** That is #281, gated on #352.

## Breaking changes and migration

**Removed or moved**: `ISubjectSource.PendingWriteCount` and `ISubjectSource.LastSynchronizedAt` (the latter replaced by `StateChangedAt`, a behaviour fix, see the monitoring section); on `OpcUaClientDiagnostics` the throughput pair, `LastError`, `IsConnected`, `PendingWriteCount`, `PendingReadAfterWrites`, `PollingItemCount`, the four reconnect counters and `LastConnectedAt` (renamed to `LastConnectionEstablishedAt`); on `OpcUaServerDiagnostics` the throughput pair, `LastError`, `IsRunning`, `StartTime` and `Uptime`; `MqttSubjectServer.IsListening` and `NumberOfClients`; `WebSocketSubjectServer.ConnectionCount` and `CurrentSequence`; the eight renamed sub-block counters.

`LastSynchronizedAt` is the one member whose replacement changes behaviour rather than only its name, and it is the only change that touches a source's monitoring surface beyond `PendingWriteCount`. Every direct `ISubjectSource` implementer listed below must supply `StateChangedAt` instead, and `SubjectSourceBase` supplies it for everything that derives from the base.

`Uptime` is dropped because with two timestamps it is ambiguous; consumers subtract whichever they mean.

**Added**: `ConnectorDiagnostics`, `SourceDiagnostics`, `QueueDiagnostics`, `QueueMetrics`, `ThroughputDiagnostics`, `ReconnectDiagnostics`, `SubjectConnectorBase`, `MqttServerDiagnostics`, `WebSocketServerDiagnostics`, and `Diagnostics` on both interfaces.

**In-repo fallout**:

| Where | What |
|---|---|
| `HomeBlaze.OpcUa/OpcUaClient.cs:221-227`, `:311-318` | seven reads plus the null-out block |
| `HomeBlaze.OpcUa/OpcUaServer.cs:191-193` | throughput pair and `ActiveSessionCount` |
| `Connectors.Tests/SourceSubscriptionTests.cs:245`, `SubjectSourceExtensionsTests.cs:500`, `SourceMonitorTests.cs:601` | direct `ISubjectSource` implementers |
| `Connectors.Tests/SubjectSourceRetryQueueTests.cs:74` | reads `source.PendingWriteCount` |
| `Benchmark/SubjectTransactionBenchmark.cs:135` | fake `ISubjectSource` |
| `ConnectorTester.Tests/Connectors/FaultTargetResolverTests.cs:21` | hand-written `ISubjectConnector`; must return a non-null `ConnectorDiagnostics`, which the public constructor now permits |
| OPC UA tests, 46 `Diagnostics.` reads | `OpcUaReconnectionTests`, `OpcUaStallDetectionTests`, `OpcUaConcurrencyTests`, `OpcUaReadWriteTests.cs:74-75`, `Client/OutageStateTests.cs:122`, `Integration/Testing/OpcUaTestClient.cs:120`, `OpcUaServerSelfWriteTests.cs:53,82,88`, `SelfEchoReproTests.cs:188` |
| four processor creation sites | `using var` becomes try/finally so `Deregister` precedes disposal |
| three server classes | change base to `SubjectConnectorBase` |

`OpcUaServerSelfWriteTests` and `SelfEchoReproTests` assert incoming throughput is 0 and are the #425 regression tests; `OpcUaReadWriteTests.cs:74-75` asserts the positive mirror. All must keep asserting through the new path.

**Snapshots**: three. `Connectors`, `OpcUa`, and `Mqtt` (`Mqtt.Tests/VerifyChecksTests.PublicApi.verified.txt:166` pins `MqttSubjectServer`'s base type, `:170` pins `NumberOfClients`). WebSocket has no snapshot test despite `WebSocketSubjectServer` being public: a pre-existing gap worth closing.

**Docs**: `connectors-opcua-client.md:648`, `:650`, the dependency graph and responsibility table at `:746`, `:747`, `:759`, `:769`, `:774`, `:776`, `:784`, and the back-reference passage at `:782` which cites `OutboundWriter` as the preferred pattern; `connectors-opcua-server.md:259`; `connectors-opcua.md:48`, `:76`; `connectors.md:272`, `:277`, `:783`, `:785`; `connectors-monitoring.md:160`. `connectors.md:785` names `PendingWriteCount` as the observable signal for #362 and must be rewritten to the new path.

**Known external fallout**: OPC UA telemetry gauges bound to the throughput pair, and any server-style connector implementing `ISubjectConnector`.

## Error handling

**No diagnostics getter may throw.** A cleared provider reads `0`, a missing session manager makes `Polling` and `ReadAfterWrite` null, a never-started connector has `StartedAt` and `OperationalChangedAt` null.

**A disposed or faulted connector reports not operational.** The base forces `IsOperational` false on its catch, `finally` and `Dispose` paths and stamps `OperationalChangedAt` with the moment it did, so a consumer can see when the connector went down rather than only that it is down. Every other member returns last-known or zero after disposal.

**A restart is a new epoch only when the connector restarts, not when its transport does.** `StartedAt` moves on a genuine restart and every `Total*` counter resets with it; a scraper seeing the epoch move knows the counters restarted.

**`Capacity` semantics**: `null` unbounded, `0` disabled. The retry queue is not constructed when `writeRetryQueueSize <= 0` (`SubjectSourceBase.cs:61`), and that reports `0`. Between connect cycles `Capacity` reports the last known bound, since the connector's configuration has not changed.

**Null throughput is a construction-time property**, not a runtime one. `ThroughputCounter.CurrentRate` returns `0.0` when idle (`ThroughputCounter.cs:55`), so null is the only way to distinguish "idle" from "not measured" and must never be used for the former.

## Testing

Repo conventions: `When<Condition>_Then<ExpectedBehavior>`, explicit Arrange/Act/Assert, no hardcoded waits.

- **The three live drop paths**, each failing when its `Interlocked.Add` is removed.
- **The disabled-queue drain does not count**, pinning the over-report that revision 3 would have shipped.
- **`TotalDropped` advances during a burst**, not only at deregistration, and never decreases across a handover.
- **Totals do not rebase across a processor recreate.** Written against `QueueMetrics` and a bounded `ChangeQueueProcessor` directly, since no in-repo connector sets a bound; the test documents that it stands in for a consumer-bounded connector.
- **`MarkStarted` is idempotent**: a server restart loop does not move `StartedAt`.
- **A faulted and a disposed source both report not operational**, with `OperationalChangedAt` stamped at the moment they did.
- **`StateChangedAt` moves on every transition**, including into `Synchronizing` and `Stopped`. The regression case is explicit: a source that synchronizes, stays synchronized, then drops must report the drop time and not the synchronization time. That is the defect the member replaces.
- **Concurrency**: a reader loop over every property while a writer loop recreates the processor; no exception escapes and `TotalDropped` never decreases.
- **Snapshot and migration**: three snapshots accepted; the #425 regression assertions preserved through their new path.

Left to integration suites: per-connector `OperationalChangedAt` transitions for MQTT and WebSocket.

## Follow-ups

- Throughput for the MQTT and WebSocket servers, and incoming for all four.
- Counting the unclaimed-property discard, which needs an ownership-aware accumulator.
- Making `WebSocketSubjectChangeProcessor` a connector so embedded mode reports diagnostics.
- A public API snapshot test for `Namotion.Interceptor.WebSocket`.
- #281 and #352: bounding the queues, which is what makes `ChangeQueue` fully meaningful in-repo.
- Permanent documentation is updated to the implemented design as part of implementation; this spec is temporary.
