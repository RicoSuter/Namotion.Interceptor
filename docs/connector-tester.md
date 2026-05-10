# Connector Tester

The Connector Tester verifies connector correctness and performance. Run it after any connector change to confirm that **eventual consistency holds under chaos** and that **throughput meets expectations under load**.

## Quick Start

Run all commands from the repository root. Clear previous logs before each run:

```bash
rm -rf logs/

# Chaos test: correctness under kill/disconnect disruptions (exits with code 1 on failure)
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile opcua-chaos --configuration Release
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile mqtt-chaos --configuration Release
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile websocket-chaos --configuration Release

# Load test: throughput and latency at 20k changes/sec
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile opcua-load --configuration Release
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile mqtt-load --configuration Release
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile websocket-load --configuration Release
```

**Chaos profiles** inject kill/disconnect faults and verify all participants converge to identical state. **Load profiles** push high throughput (configurable via `ValueMutationRate`) and report latency percentiles, memory, and allocation rate. Both modes use the same verification cycle: mutate, pause, compare snapshots. Both should pass before merging connector changes.

## How It Works

The tester hosts a server and one or more clients in a single process, connected via the selected connector (OPC UA, MQTT, or WebSocket). Each participant has its own `TestNode` object graph and `MutationEngine` that continuously modifies properties. A `VerificationEngine` orchestrates repeating mutate/converge cycles: mutations run for a configured duration, then all engines pause and snapshots are compared. If all participants have identical state, the cycle passes. If not, the process logs the diff and exits with code 1.

```
                         +------------------+
                         |     Server       |
                         |  TestNode graph  |
                         |  MutationEngine  |
                         |  ChaosEngine?    |
                         +--------+---------+
                                  |
                          Connector (OPC UA / MQTT / WebSocket)
                                  |
                 +----------------+----------------+
                 |                                 |
          +------+------+                   +------+------+
          |   Client A  |                   |   Client B  |
          |  TestNode   |                   |  TestNode   |
          |  Mutation   |                   |  Mutation   |
          |  Chaos?     |                   |  Chaos?     |
          +-------------+                   +-------------+

    MutationEngine: RandomMutationEngine (chaos) or BatchMutationEngine (load)
    VerificationEngine orchestrates mutate/converge cycles
    TestCycleCoordinator pauses all engines during convergence
    PerformanceProfiler collects latency/throughput/memory metrics
```

### Mutation Engines

- **RandomMutationEngine** (chaos profiles, `NumberOfBatches = 0`): Picks a random node and random property, one at a time, at the configured `ValueMutationRate`. Good for chaos testing where mutation patterns should be unpredictable. When `UseTransactions` is enabled, each tick's batch of mutations is wrapped in a single transaction.

- **BatchMutationEngine** (load profiles, `NumberOfBatches > 0`): Mutates `ValueMutationRate` nodes per second, spread across `NumberOfBatches` parallel batches within 1-second windows. Uses a `PeriodicTimer` at 110% of the required tick rate (e.g., 50 batches → 55 ticks/sec → ~18ms interval) to guarantee all batches complete within each second with 10% headroom for scheduling jitter. Each participant mutates a single fixed property (`participantIndex % 4`) to avoid OPC UA subscription coalescing — server always mutates `StringValue`, first client always mutates `DecimalValue`, etc. When `UseTransactions` is enabled, each batch is wrapped in a transaction and runs sequentially (transactions are not thread-safe with `Parallel.For`).

### Mutate/Converge Cycle

Each test cycle has two phases:

1. **Mutate phase**: All MutationEngines and ChaosEngines run concurrently for `MutatePhaseDuration`.

2. **Converge phase**: The VerificationEngine pauses all engines via the TestCycleCoordinator, recovers any active chaos disruptions, waits a grace period (20s for OPC UA) for reconnection, then polls snapshots every 5 seconds. `SnapshotComparer.Capture` produces a normalized JSON per participant from `SubjectUpdate.CreateCompleteUpdate()`. Normalization strips timestamps from Collection/Dictionary/Object properties (these are local graph-creation moments and never sync), replaces each participant's root subject ID with a constant placeholder (`"ROOT"`), sorts dictionary items by their key, and sorts the subjects map and per-subject property keys for deterministic ordering. Collection items keep their source order: order is part of equality.

`SnapshotComparer.SnapshotsMatch` decides convergence. It first tries string equality (the common case after normalization). On mismatch it walks the JSON tree and compares fields. The only special case is the `timestamp` field on Value properties: a null timestamp on either side matches any value (the explicit "never written" state preserved by `SubjectChangeContext.NullTimestampTicks`); two non-null timestamps must be equal. All other fields use strict JSON equality, so any new field added to `SubjectPropertyUpdate` is automatically part of the comparison.

A cycle **passes** when all participant snapshots match by these rules. It **fails** if the convergence timeout expires. On failure, the process writes per-participant JSON snapshots to disk, logs per-property diffs with write timestamps, runs a re-sync diagnostic, gracefully shuts down all hosted services, and exits with code 1.

### Chaos via IFaultInjectable

Each connector implements `IFaultInjectable` (separate from the production `ISubjectConnector` interface) with a single `InjectFaultAsync(FaultType, CancellationToken)` method supporting two chaos modes:

- **Kill** (`FaultType.Kill`): Hard kill — stops the connector entirely. The background service loop auto-restarts.
  - *OPC UA Server*: Cancels the server loop token, closes transport listeners (TCP RST to all clients), disposes without graceful shutdown.
  - *OPC UA Client*: Attempts graceful session close, then kills the transport channel. Health check detects missing session and triggers full reconnection.
  - *MQTT*: Cancels the force-kill CTS, causing the processing loop to exit and restart.
  - *WebSocket Server*: Cancels the force-kill CTS, triggering full teardown and rebuild of the Kestrel HTTP listener.
  - *WebSocket Client*: Sets force-kill flag and cancels the force-kill CTS; the monitor loop catch block aborts the WebSocket and reconnects.

- **Disconnect** (`FaultType.Disconnect`): Soft kill — breaks the transport connection without stopping the connector. Lets the connector's built-in reconnection logic detect the failure and recover.
  - *OPC UA Server*: Delegates to Kill (no meaningful "soft disconnect" for a multi-connection server).
  - *OPC UA Client*: Disposes the transport channel. Keep-alive failure triggers `SessionReconnectHandler.BeginReconnect`, exercising the session preservation/transfer path.
  - *MQTT Server*: Enumerates connected clients and disconnects each one individually.
  - *MQTT Client*: Calls `DisconnectAsync()` on the MQTT client for a graceful disconnect.
  - *WebSocket Server*: Closes all client connections via `CloseAllConnectionsAsync()`.
  - *WebSocket Client*: Aborts the underlying `ClientWebSocket`, triggering reconnection.

The ChaosEngine picks an action based on the configured `Mode` ("kill", "disconnect", or "both"). In "both" mode, each event randomly chooses between kill and disconnect. After the action, the engine holds a "disruption window" for a random duration (representing the outage period for verification purposes).

### Key Design Decisions

- **Global mutation counter**: A static `Interlocked.Increment` counter ensures every mutation produces a globally unique value, preventing the equality interceptor from dropping duplicate changes.
- **Explicit timestamp scoping**: Mutations use `SubjectChangeContext.WithChangedTimestamp()` so all interceptors and change queue observers see the same timestamp.
- **IFaultInjectable separation**: Chaos testing uses a dedicated `IFaultInjectable` interface (separate from production `ISubjectConnector`) with `InjectFaultAsync(FaultType, CancellationToken)`. Two modes: `FaultType.Kill` (hard kill with auto-restart) and `FaultType.Disconnect` (transport disconnect with reconnection).
- **Shutdown timeout**: The OPC UA server's `ShutdownServerAsync` wraps `application.StopAsync()` with a 10s timeout to prevent hang when clients keep reconnecting during graceful shutdown.

## Supported Connectors

| Connector | Status | Notes |
|-----------|--------|-------|
| OPC UA | Working | Server kill drops TCP connections, client kill abandons session. 1 min convergence timeout. `decimal` round-trips through `double`. |
| MQTT | Working | Server and client kill/disconnect. 2 min convergence timeout. |
| WebSocket | Working | Server kill cancels background loop, client kill aborts socket. Sequence gap detection triggers reconnection. |

### Connector-Specific Behaviors

**OPC UA**: Uses `OpcUaValueConverter` for type mapping. `decimal` values lose precision beyond ~15 significant digits due to `decimal` -> `double` -> `decimal` round-trip. `BufferTime=100ms` batches changes. Server chaos closes transport listeners before dispose, so clients get an immediate TCP RST rather than waiting for keep-alive timeout. Client chaos disposes the session without `CloseAsync`, simulating an abrupt disconnection.

**MQTT**: Uses server-authoritative relay pattern where client publishes are intercepted, applied to the server model, and re-published to all clients. Ticks-based timestamp serialization (`UtcTicks`) for full precision. QoS=AtLeastOnce with retained messages.

**WebSocket**: Uses Hello/Welcome handshake for initial state delivery. Server broadcasts all changes to all clients (including originator) with monotonic sequence numbers. Client tracks sequences and triggers reconnection on gap detection. Server kill cancels the background loop CTS; client kill aborts the underlying `ClientWebSocket`. Disconnect mode closes all server connections or aborts the client socket respectively. Circuit breaker (5 failures, 60s cooldown) pauses reconnection during prolonged outages.

## Running

### Multi-Process Mode

Run server and client in separate processes for isolated load testing:

```bash
# Terminal 1: Run server only
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile opcua-load --configuration Release -- --participant server

# Terminal 2: Run client only
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile opcua-load --configuration Release -- --participant client
```

When `--participant` is specified, only the named participant starts and the verification engine is skipped. Mutations run continuously with performance metrics.

### What to Look For

**Success**: Each cycle prints `PASS` with convergence time and chaos event counts:
```
=== Cycle 1: PASS (converged in 5.1s, cycle 85s) ===
--- Cycle 1 Statistics ---
Duration: 85s (converged in 5.1s)
Total mutations: 17,200 | Total chaos events: 7
  server: 11,300 value mutations, 0 structural mutations
  client-a: 2,950 value mutations, 0 structural mutations
  client-b: 2,950 value mutations, 0 structural mutations
  client-b: disconnect at 21:08:37 (2.8s)
  server: kill at 21:08:38 (5.4s)
```

**Failure**: Prints `FAIL`, runs failure diagnostics, then exits with code 1:

```
=== Cycle 3: FAIL (did not converge within 00:01:00) ===
Snapshot [server] written to logs/cycle003-fail-server.json
Snapshot [client-a] written to logs/cycle003-fail-client-a.json
  ROOT.IntValue: server=42 (written 12:34:56.789), client-a=37 (written 12:34:55.110)
Re-sync check: client-a converged after applying reference complete update -> transient delivery gap
```

The per-cycle JSON files are formatted (indented) and can be diffed with any text tool. The `cycleNNN-fail-{participant}.json` files are the canonical artifact for investigating divergence.

### Log Files

All log files are written to `logs/` in the repository root (the working directory):

```
logs/
  cycle-001-pass-2026-02-08T22-40-38.log
  cycle-002-pass-2026-02-08T22-40-54.log
  cycle-003-FAIL-2026-02-08T22-35-00.log
```

Files are created as `pending` and renamed to `pass` or `FAIL` on cycle completion. Each file contains all log output (INFO+) for that cycle with timestamps. To prevent disk exhaustion during long-running tests, only the 50 most recent passing log files are kept. FAIL logs are always preserved.

**Use the log files to analyze results and diagnose problems.** On failure, the `FAIL` log contains full snapshot diffs showing exactly which participants diverged and what their state was. Look for:
- Chaos event timing (`Chaos: force-killing ...`) to correlate disruptions with convergence delays.
- Reconnection logs (`OPC UA server connection lost. Beginning reconnect...`, `SDK reconnection initialization completed successfully.`) to verify recovery.
- Mutation counts in the statistics block to confirm all engines were active.
- Snapshot diffs at the end of failed cycles to identify which properties or participants didn't converge.

### Performance Logs

Performance metrics are written to `logs/performance-{participant}.csv` for every profile (chaos and load). Each file has a header row and one data row per reporting interval. Columns: Timestamp, Participant, Recv/s, Recv-E2E-Avg, Recv-E2E-P50, Recv-E2E-P90, Recv-E2E-P95, Recv-E2E-P99, Recv-E2E-P999, Recv-E2E-Max, Recv-Proc, Published, Received, CPU%, ProcessMB, HeapMB, AllocMB/s. Files are reset on each run.

### Cycle Logs

`logs/cycles.csv` records one row per verification cycle with post-GC memory measurements. Columns: Timestamp, Cycle, Result, Profile, CycleSec, ConvergeSec, ValueMutations, StructuralMutations, ChaosEvents, HeapMB, ProcessMB. Memory is measured after a full GC + LOH compaction, giving a stable baseline for leak detection. The file is reset on each run.

## Configuration

Configuration is loaded from `appsettings.json` with environment-specific overrides (e.g., `appsettings.opcua-chaos.json`). The root section is `"ConnectorTester"`. Use `--launch-profile` to select a profile (e.g., `--launch-profile opcua-chaos`), which sets `DOTNET_ENVIRONMENT` and loads the corresponding `appsettings.{environment}.json` file.

### Chaos Profile Example (appsettings.opcua-chaos.json)

```json
{
  "ConnectorTester": {
    "Connector": "opcua",
    "MutatePhaseDuration": "00:01:00",
    "ConvergenceTimeout": "00:05:00",
    "Server": {
      "ValueMutationRate": 1000,
      "Chaos": {
        "IntervalMin": "00:00:10",
        "IntervalMax": "00:00:20",
        "DurationMin": "00:00:03",
        "DurationMax": "00:00:05",
        "Mode": "both"
      }
    },
    "Clients": [
      {
        "Name": "client-a",
        "ValueMutationRate": 100,
        "Chaos": {
          "IntervalMin": "00:00:10",
          "IntervalMax": "00:00:20",
          "DurationMin": "00:00:02",
          "DurationMax": "00:00:04",
          "Mode": "both"
        }
      },
      {
        "Name": "client-b",
        "ValueMutationRate": 100,
        "Chaos": {
          "IntervalMin": "00:00:08",
          "IntervalMax": "00:00:15",
          "DurationMin": "00:00:02",
          "DurationMax": "00:00:04",
          "Mode": "both"
        }
      }
    ],
    "ChaosProfiles": [
      { "Name": "no-chaos", "Participants": [] },
      { "Name": "server-only", "Participants": ["server"] },
      { "Name": "client-a-only", "Participants": ["client-a"] },
      { "Name": "all-clients", "Participants": ["client-a", "client-b"] },
      { "Name": "full-chaos", "Participants": ["server", "client-a", "client-b"] }
    ]
  }
}
```

### Load Profile Example (appsettings.opcua-load.json)

```json
{
  "ConnectorTester": {
    "Connector": "opcua",
    "CollectionCount": 20000,
    "DictionaryCount": 0,
    "NumberOfBatches": 50,
    "MutatePhaseDuration": "00:30:00",
    "ConvergenceTimeout": "00:05:00",
    "MetricsReportingInterval": "00:01:00",
    "Server": {
      "Name": "server",
      "ValueMutationRate": 20000
    },
    "Clients": [
      {
        "Name": "client",
        "ValueMutationRate": 20000
      }
    ]
  }
}
```

### Configuration Reference

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Connector` | string | `"opcua"` | Protocol to test: `"opcua"`, `"mqtt"`, or `"websocket"` |
| `CollectionCount` | int | `20` | Number of collection children in the test graph |
| `DictionaryCount` | int | `10` | Number of dictionary entries in the test graph |
| `NumberOfBatches` | int | `0` | Batches per second. `0` = RandomMutationEngine, `> 0` = BatchMutationEngine. Each batch mutates `ceil(ValueMutationRate / NumberOfBatches)` nodes. |
| `MetricsReportingInterval` | TimeSpan | `00:01:00` | How often performance metrics are logged |
| `MutatePhaseDuration` | TimeSpan | `00:01:00` | How long mutations run before convergence check |
| `ConvergenceTimeout` | TimeSpan | `00:01:00` | Max time to wait for all snapshots to match |
| `Server` | object | - | Server participant configuration |
| `Clients` | array | `[]` | Client participant configurations |
| `ChaosProfiles` | array | `[]` | Named chaos profiles that rotate round-robin per cycle. Empty = all engines always active. |

### Participant Configuration

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Name` | string | `""` | Participant identifier (appears in logs) |
| `ValueMutationRate` | int | `50` | Value mutations per second |
| `StructuralMutationRate` | int | `0` | Structural mutations per second (0 = disabled) |
| `UseTransactions` | bool | `false` | Wrap each mutation batch in a transaction (`BeginTransactionAsync`/`CommitAsync`). When enabled, `BatchMutationEngine` runs sequentially (transactions are not thread-safe with `Parallel.For`). |
| `Chaos` | object? | `null` | Chaos configuration (`null` = no chaos) |

### Chaos Configuration

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `IntervalMin` | TimeSpan | `00:01:00` | Minimum wait before next disruption |
| `IntervalMax` | TimeSpan | `00:05:00` | Maximum wait before next disruption |
| `DurationMin` | TimeSpan | `00:00:05` | Minimum disruption hold time |
| `DurationMax` | TimeSpan | `00:00:30` | Maximum disruption hold time |
| `Mode` | string | `"both"` | `"kill"`, `"disconnect"`, or `"both"` (random choice) |

### Chaos Profile Configuration

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Name` | string | `""` | Profile identifier (appears in cycle logs) |
| `Participants` | array | `[]` | Participant names that have chaos active in this profile |

### Tuning Chaos Intervals

Chaos intervals must be shorter than `MutatePhaseDuration` to ensure disruptions actually occur during each cycle. As a guideline:
- Set `IntervalMax` to at most half of `MutatePhaseDuration` to guarantee at least one event per cycle.
- Each chaos event takes `Interval + Duration` time, so account for both when calculating expected events per cycle.
- Server chaos is more impactful (affects all clients) so can use longer intervals. Client chaos is local and can be more frequent.

### Chaos Profiles

By default, all chaos engines run every cycle. To vary which participants experience chaos per cycle, define `ChaosProfiles` — a list of named profiles that rotate round-robin:

```json
"ChaosProfiles": [
  { "Name": "no-chaos", "Participants": [] },
  { "Name": "server-only", "Participants": ["server"] },
  { "Name": "client-b-only", "Participants": ["client-b"] },
  { "Name": "full-chaos", "Participants": ["server", "client-a", "client-b"] }
]
```

Each profile lists which participants have chaos active for that cycle. Profiles rotate in order: cycle 1 uses the first profile, cycle 2 uses the second, and so on (wrapping around).

**Constraints:**
- Only participants with a `Chaos` configuration get a chaos engine. Referencing a participant without `Chaos` config in a profile logs a warning and has no effect.
- When `ChaosProfiles` is empty or omitted, all chaos engines run every cycle (current default behavior).
- Cycle logs show the active profile: `=== Cycle 3: Mutate phase started (01:00) [profile: server-only] ===`

## Failure Scenario Coverage

### Covered (OPC UA)

| Scenario | Kill | Disconnect | Recovery Mechanism |
|----------|------|------------|-------------------|
| Abrupt server crash | Server loop cancelled, TCP listeners closed (RST to clients) | Delegates to Kill | Background loop auto-restarts with exponential backoff (1s-30s + jitter) |
| Abrupt client crash | Session disposed without CloseSession RPC | Transport channel disposed, session preserved | Health check detects missing session, triggers full reconnection |
| Network partition | N/A | Client transport disposed, keep-alive detects within 5-10s | SDK `SessionReconnectHandler.BeginReconnect` with subscription transfer |
| Server restart (clean state) | Full server restart, new node manager | N/A | Client subscription transfer fails, falls back to full state reload |
| Session stall / hung reconnect | N/A | N/A | Stall detection after 30s forces SDK handler reset, triggers manual reconnection |
| Subscription creation failure | N/A | N/A | `SubscriptionHealthMonitor` retries failed items every 5s, falls back to polling |
| Port already in use on restart | N/A | N/A | Retried with exponential backoff |
| Resource exhaustion (polling) | N/A | N/A | Circuit breaker (5 failures, 60s cooldown) |
| Concurrent server + client chaos | Both engines run independently, overlapping disruptions possible | Same | Each connector recovers independently |
| Bidirectional mutations during chaos | Server and clients mutate concurrently during disruptions | Same | WriteRetryQueue buffers outbound writes, full state sync on reconnect |

### Covered (WebSocket)

| Scenario | Kill | Disconnect | Recovery Mechanism |
|----------|------|------------|-------------------|
| Abrupt server crash | Force-kill CTS cancelled, full Kestrel teardown and rebuild | All client connections closed | Background loop rebuilds HTTP listener and restarts |
| Abrupt client crash | Force-kill CTS cancelled, WebSocket aborted, monitor loop reconnects | Socket aborted, receive loop exits | Monitor loop reconnects with exponential backoff |
| Sequence gap detection | N/A | N/A | Client detects missed sequence number, exits receive loop, reconnects with full state via Welcome |
| Heartbeat timeout | N/A | N/A | Receive timeout fires, client exits receive loop and reconnects |
| Concurrent server + client chaos | Both engines run independently | Same | Each side recovers independently, Welcome handshake re-syncs state |
| Bidirectional mutations during chaos | Server and clients mutate concurrently | Same | WriteRetryQueue buffers outbound writes, Welcome snapshot on reconnect |

### Not Covered

| Scenario | Why Not Covered | Production Impact |
|----------|-----------------|-------------------|
| Partial network failure (slow/lossy connections) | Keep-alive uses binary pass/fail detection | Low - TCP keepalive settings at OS level handle this |
| Certificate expiry mid-session | No certificate rotation mechanism | Low for tests, relevant for >6 month production deployments |
| Server returning persistent error status codes | Polling ignores some error codes | Low - circuit breaker limits impact |
| Out-of-order or duplicate notifications | No deduplication logic | Very low - OPC UA SDK handles within subscriptions |
| Notification duplication after subscription transfer | Full state read overwrites stale values | Low - brief inconsistency only |
| Server overload / adaptive backpressure | No response-time-based throttling | Low - not a connectivity failure |
| Flapping connections (rapid up/down cycles) | Backoff resets on each success | Low - stall detection provides upper bound |

### What the Tester Detects

The snapshot comparison **reliably detects**:
- Value divergence between any participants
- Collection/dictionary structural mismatches
- Object reference integrity breaks
- Data loss that isn't recovered within the convergence window

The snapshot comparison **does not detect** (by design):
- Transient state changes lost to deduplication (ChangeQueueProcessor keeps only last value per property within its 8ms buffer window)
- Timestamp accuracy for same-value updates (equality interceptor suppresses writes when value is unchanged, even if timestamp differs)
- Temporal ordering of changes (only final converged state is checked, not causality)
- Multi-property atomicity (no transaction support; A and B may converge independently)

The failure diagnostics localize bugs:

- **Per-cycle JSON snapshots** (`logs/cycleNNN-fail-{participant}.json`): full normalized state per participant for diff investigation.
- **Per-property diffs with timestamps**: lists every diverged property with each side's value and write timestamp. `written never` means the property was never written via the interceptor chain on that participant. `written T` means it was written at time T.
- **Re-sync classifier**: applies the reference participant's complete state to each diverged participant and re-compares. If the result matches, the failure is a **transient delivery gap** (look at the connector wire: lost or out-of-order messages, missed reconnect catch-up). If it still diverges, the failure is in the snapshot logic, `SubjectUpdate.CreateCompleteUpdate`, `ApplySubjectUpdate`, or the `TestNode` model itself: the connector wire is exonerated.

The MutationEngine's global counter ensures every mutation produces a unique value, which prevents the equality interceptor from ever suppressing test mutations. This makes the tester resilient to the same-value suppression issue, though that issue could still affect production workloads.

## Long-Running Tests

The tester is designed to run continuously for extended periods (days/weeks). Key features for long-running viability:

- **Log rotation**: Only the 50 most recent passing log files are kept. FAIL logs are always preserved.
- **Graceful shutdown on failure**: On convergence failure, the host shuts down cleanly (flushing logs, disposing connectors) before setting exit code 1.
- **Reduced GC pressure**: Snapshot polling uses a 5-second interval to minimize allocation churn from JSON serialization.
- **Bounded retry queues**: `WriteRetryQueue` uses a ring buffer (default 1000 items) to prevent unbounded memory growth during long disconnections.

For multi-day runs, consider using longer cycle durations and lower mutation rates to reduce overall resource consumption:

```json
{
  "ConnectorTester": {
    "MutatePhaseDuration": "00:02:00",
    "ConvergenceTimeout": "00:02:00",
    "Server": { "ValueMutationRate": 500 },
    "Clients": [
      { "Name": "client-a", "ValueMutationRate": 25 },
      { "Name": "client-b", "ValueMutationRate": 25 }
    ],
    "ChaosProfiles": []
  }
}
```

## Adding a New Connector

1. **Add `appsettings.{name}.json`** with `"Connector": "{name}"` and desired chaos/timing settings.

2. **Add a launch profile** in `Properties/launchSettings.json`:
   ```json
   "{name}": {
     "commandName": "Project",
     "environmentVariables": { "DOTNET_ENVIRONMENT": "{name}" }
   }
   ```

3. **Wire the connector in `Program.cs`**:
   - Add a `case "{name}":` in the server connector switch.
   - Add a `case "{name}":` in the client connector switch (inside the client loop).
   - Pick a base port in the `serverPort` switch.

4. **Implement `IFaultInjectable`** on the connector's background service so ChaosEngine can inject disruptions. Implement `KillAsync` (hard kill) and `DisconnectAsync` (transport disconnect).

5. **Add `[Path]` attributes** to `TestNode.cs` properties for the new connector's path provider key.

## Troubleshooting

### Convergence Timeout

If cycles consistently fail to converge:
- Check that chaos recovery is working (look for "Chaos: force-killing" and "recovered from force-kill" log messages).
- Increase `ConvergenceTimeout` if the connector needs more time for full state synchronization.
- Verify the grace period in `VerificationEngine` is sufficient for your connector's reconnection chain.

### No Chaos Events

If cycles pass but show 0 chaos events, the chaos intervals are too long relative to `MutatePhaseDuration`. Reduce `IntervalMin`/`IntervalMax` so that `IntervalMax < MutatePhaseDuration`.

### Structural Timestamp Mismatch

Structural properties (Collection, Dictionary, Object) have local creation timestamps that differ per participant. These are **stripped during snapshot comparison** and should not cause failures. If you see mismatches involving only structural timestamps, the stripping logic in `SnapshotComparer.Capture()` may need updating.

### Decimal Precision (OPC UA)

OPC UA maps `decimal` through `double`, which has ~15-17 significant digits. The mutation engine uses `counter / 100m`, which stays well within this range. If you add mutations with larger decimal values, expect precision loss.

### Wrong Connector Running

If the test appears to run the wrong connector, verify you're using `--launch-profile` (not `--environment`). The launch profile sets `DOTNET_ENVIRONMENT` which controls appsettings loading.
