# Connector Tester

An in-process integration test that validates **eventual consistency** across Namotion.Interceptor connectors under chaos conditions. It hosts a server and multiple clients in a single process, mutates their object models concurrently, injects disruptions via `IChaosTarget` (kill and disconnect), and verifies that all participants eventually reach identical state. Exits with code 1 on failure for CI/CD integration.

## Architecture

```
                         +------------------+
                         |     Server       |
                         |  TestNode graph  |
                         |  MutationEngine  |
                         |  ChaosEngine?    |
                         +--------+---------+
                                  |
                          Connector (OPC UA / MQTT)
                                  |
                 +----------------+----------------+
                 |                                 |
          +------+------+                   +------+------+
          |   Client A  |                   |   Client B  |
          |  TestNode   |                   |  TestNode   |
          |  Mutation   |                   |  Mutation   |
          |  Chaos?     |                   |  Chaos?     |
          +-------------+                   +-------------+

    VerificationEngine orchestrates mutate/converge cycles
    TestCycleCoordinator pauses all engines during convergence
```

### Mutate/Converge Cycle

Each test cycle has two phases:

1. **Mutate phase**: All MutationEngines and ChaosEngines run concurrently. Engines randomly mutate value properties (`StringValue`, `DecimalValue`, `IntValue`) on TestNode objects across the graph (1 root + 20 collection items + 10 dictionary items = 31 objects per participant).

2. **Converge phase**: The VerificationEngine pauses all engines via the TestCycleCoordinator, recovers any active chaos disruptions, waits a grace period (20s for OPC UA) for reconnection, then polls snapshots every 5 seconds. Each snapshot serializes the full object graph using `SubjectUpdate.CreateCompleteUpdate()`. Structural property timestamps (Collection, Dictionary, Object) are stripped since they represent local creation time, not synced state. Value property timestamps are preserved and must converge.

A cycle **passes** when all participant snapshots are identical JSON. It **fails** if the convergence timeout expires. On failure, the process logs snapshot diffs, gracefully shuts down all hosted services, and exits with code 1.

### Chaos via IChaosTarget

Each connector implements `IChaosTarget` (separate from the production `ISubjectConnector` interface) with two chaos modes:

- **Kill** (`KillAsync`): Hard kill — stops the connector entirely. The background service loop auto-restarts.
  - *OPC UA Server*: Cancels the server loop token, closes transport listeners (TCP RST to all clients), disposes without graceful shutdown.
  - *OPC UA Client*: Clears the session without sending `CloseSession` to the server (disposes local socket). Health check detects missing session and triggers full reconnection.
  - *MQTT*: Stops the underlying service and lets the background loop restart it.

- **Disconnect** (`DisconnectAsync`): Soft kill — breaks the transport connection without stopping the connector. Lets the SDK's built-in reconnection logic detect the failure and recover.
  - *OPC UA Server*: Delegates to `KillAsync` (no meaningful "soft disconnect" for a multi-connection server).
  - *OPC UA Client*: Disposes the transport channel. Keep-alive failure triggers `SessionReconnectHandler.BeginReconnect`, exercising the session preservation/transfer path.
  - *MQTT*: Delegates to `KillAsync`.

The ChaosEngine picks an action based on the configured `Mode` ("kill", "disconnect", or "both"). In "both" mode, each event randomly chooses between kill and disconnect. After the action, the engine holds a "disruption window" for a random duration (representing the outage period for verification purposes).

### Key Design Decisions

- **Global mutation counter**: A static `Interlocked.Increment` counter ensures every mutation produces a globally unique value, preventing the equality interceptor from dropping duplicate changes.
- **Explicit timestamp scoping**: Mutations use `SubjectChangeContext.WithChangedTimestamp()` so all interceptors and change queue observers see the same timestamp.
- **IChaosTarget separation**: Chaos testing uses a dedicated `IChaosTarget` interface (separate from production `ISubjectConnector`). Two modes: `KillAsync` (hard kill with auto-restart) and `DisconnectAsync` (transport disconnect with SDK reconnection).
- **Shutdown timeout**: The OPC UA server's `ShutdownServerAsync` wraps `application.StopAsync()` with a 10s timeout to prevent hang when clients keep reconnecting during graceful shutdown.

## Supported Connectors

| Connector | Status | Notes |
|-----------|--------|-------|
| OPC UA | Working | Server kill drops TCP connections, client kill abandons session. 1 min convergence timeout. `decimal` round-trips through `double`. |
| MQTT | Working | Server and client kill/disconnect. 2 min convergence timeout. |
| WebSocket | Planned | Config exists but no wiring in Program.cs yet. |

### Connector-Specific Behaviors

**OPC UA**: Uses `OpcUaValueConverter` for type mapping. `decimal` values lose precision beyond ~15 significant digits due to `decimal` -> `double` -> `decimal` round-trip. `BufferTime=100ms` batches changes. Server chaos closes transport listeners before dispose, so clients get an immediate TCP RST rather than waiting for keep-alive timeout. Client chaos disposes the session without `CloseAsync`, simulating an abrupt disconnection.

**MQTT**: Uses server-authoritative relay pattern where client publishes are intercepted, applied to the server model, and re-published to all clients. Ticks-based timestamp serialization (`UtcTicks`) for full precision. QoS=AtLeastOnce with retained messages.

## Running

Run with a launch profile to select the connector:

```bash
# OPC UA (Release mode recommended for performance)
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile opcua -c Release

# MQTT
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile mqtt -c Release
```

Launch profiles set the `DOTNET_ENVIRONMENT` variable, which loads the corresponding `appsettings.{environment}.json` file.

### What to Look For

**Success**: Each cycle prints `PASS` with convergence time and chaos event counts:
```
=== Cycle 1: PASS (converged in 5.1s, cycle 85s) ===
--- Cycle 1 Statistics ---
Duration: 85s (converged in 5.1s)
Total mutations: 17,200 | Total chaos events: 7
  server: 11,300 value mutations
  client-a: 2,950 value mutations
  client-b: 2,950 value mutations
  client-b: disconnect at 21:08:37 (2.8s)
  server: kill at 21:08:38 (5.4s)
```

**Failure**: Prints `FAIL` with snapshot diffs, then exits with code 1:
```
=== Cycle 3: FAIL (did not converge within 00:01:00) ===
Mismatch between server and client-b
```

### Log Files

Per-cycle log files are written to a `logs/` directory (relative to working directory):

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

## Configuration

Configuration is loaded from `appsettings.json` with environment-specific overrides (e.g., `appsettings.opcua.json`). The root section is `"ConnectorTester"`.

### OPC UA Example (appsettings.opcua.json)

```json
{
  "ConnectorTester": {
    "Connector": "opcua",
    "EnableStructuralMutations": false,
    "MutatePhaseDuration": "00:01:00",
    "ConvergenceTimeout": "00:01:00",
    "Server": {
      "MutationRate": 200,
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
        "MutationRate": 50,
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
        "MutationRate": 50,
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
      { "Name": "full-chaos", "Participants": ["server", "client-a", "client-b"] }
    ]
  }
}
```

### Configuration Reference

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Connector` | string | `"opcua"` | Protocol to test: `"opcua"`, `"mqtt"`, or `"websocket"` |
| `EnableStructuralMutations` | bool | `false` | Reserved for future collection/dictionary mutations |
| `MutatePhaseDuration` | TimeSpan | `00:05:00` | How long mutations run before convergence check |
| `ConvergenceTimeout` | TimeSpan | `00:01:00` | Max time to wait for all snapshots to match |
| `Server` | object | - | Server participant configuration |
| `Clients` | array | `[]` | Client participant configurations |
| `ChaosProfiles` | array | `[]` | Named chaos profiles that rotate round-robin per cycle. Empty = all engines always active. |

### Participant Configuration

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Name` | string | `""` | Participant identifier (appears in logs) |
| `MutationRate` | int | `50` | Target mutations per second |
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
    "Server": { "MutationRate": 500 },
    "Clients": [
      { "Name": "client-a", "MutationRate": 25 },
      { "Name": "client-b", "MutationRate": 25 }
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

4. **Implement `IChaosTarget`** on the connector's background service so ChaosEngine can inject disruptions. Implement `KillAsync` (hard kill) and `DisconnectAsync` (transport disconnect).

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

Structural properties (Collection, Dictionary, Object) have local creation timestamps that differ per participant. These are **stripped during snapshot comparison** and should not cause failures. If you see mismatches involving only structural timestamps, the stripping logic in `VerificationEngine.CreateSnapshot()` may need updating.

### Decimal Precision (OPC UA)

OPC UA maps `decimal` through `double`, which has ~15-17 significant digits. The mutation engine uses `counter / 100m`, which stays well within this range. If you add mutations with larger decimal values, expect precision loss.

### Wrong Connector Running

If the test appears to run the wrong connector, verify you're using `--launch-profile` (not `--environment`). The launch profile sets `DOTNET_ENVIRONMENT` which controls appsettings loading.

## Project Structure

```
Namotion.Interceptor.ConnectorTester/
  Program.cs                             # Entry point, server/client wiring
  GlobalUsings.cs                        # Global using directives
  Configuration/
    ConnectorTesterConfiguration.cs      # Top-level config
    ParticipantConfiguration.cs          # Per-participant config
    ChaosConfiguration.cs               # Chaos timing config
    ChaosProfileConfiguration.cs        # Chaos profile config
  Model/
    TestNode.cs                          # Test data model with path annotations
  Engine/
    TestCycleCoordinator.cs             # Pause/resume synchronization
    MutationEngine.cs                   # Random property mutations
    ChaosEngine.cs                      # Kill/disconnect disruptions
    VerificationEngine.cs               # Cycle orchestration and snapshot comparison
  Logging/
    CycleLoggerProvider.cs              # Per-cycle log file writer
  Properties/
    launchSettings.json                 # Launch profiles (opcua, mqtt, websocket)
  appsettings.json                      # Base configuration
  appsettings.opcua.json                # OPC UA profile
  appsettings.mqtt.json                 # MQTT profile
  appsettings.websocket.json            # WebSocket profile (placeholder)
```
