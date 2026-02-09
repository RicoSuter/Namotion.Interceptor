# Connector Tester

An in-process integration test that validates **eventual consistency** across Namotion.Interceptor connectors under chaos conditions. It hosts a server and multiple clients in a single process, mutates their object models concurrently, injects network and lifecycle disruptions, and verifies that all participants eventually reach identical state. Exits with code 1 on failure for CI/CD integration.

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
          |  TcpProxy   |                   |  TcpProxy   |
          |  (port N+1) |                   |  (port N+2) |
          +------+------+                   +------+------+
                 |                                 |
          +------+------+                   +------+------+
          |   Client 1  |                   |   Client 2  |
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

2. **Converge phase**: The VerificationEngine pauses all engines via the TestCycleCoordinator, recovers any active chaos disruptions, then polls snapshots every second. Each snapshot serializes the full object graph using `SubjectUpdate.CreateCompleteUpdate()`. Structural property timestamps (Collection, Dictionary, Object) are stripped since they represent local creation time, not synced state. Value property timestamps are preserved and must converge.

A cycle **passes** when all participant snapshots are identical JSON. It **fails** if the convergence timeout expires. On failure, the process logs snapshot diffs and exits with code 1.

### Key Design Decisions

- **Global mutation counter**: A static `Interlocked.Increment` counter ensures every mutation produces a globally unique value, preventing the equality interceptor from dropping duplicate changes.
- **Explicit timestamp scoping**: Mutations use `SubjectChangeContext.WithChangedTimestamp()` so all interceptors and change queue observers see the same timestamp.
- **Thread-safe chaos recovery**: `Interlocked.Exchange` on the active disruption field prevents double recovery during convergence.

## Supported Connectors

| Connector | Status | Chaos Modes | Notes |
|-----------|--------|-------------|-------|
| OPC UA | Working | lifecycle | Server stop/start. 60s convergence timeout. `decimal` round-trips through `double`. |
| MQTT | Working | transport, lifecycle, both | Keep-alive (2s) required for proxy PAUSE detection. Server-authoritative relay. 2 min convergence timeout. |
| WebSocket | Planned | - | Config exists but no wiring in Program.cs yet. |

### Connector-Specific Behaviors

**OPC UA**: Uses `OpcUaValueConverter` for type mapping. `decimal` values lose precision beyond ~15 significant digits due to `decimal` -> `double` -> `decimal` round-trip. `BufferTime=100ms` batches changes.

**MQTT**: Uses server-authoritative relay pattern where client publishes are intercepted, applied to the server model, and re-published to all clients. Ticks-based timestamp serialization (`UtcTicks`) for full precision. Short keep-alive interval (2s) is critical for detecting proxy PAUSE disruptions, which silently drop bytes without closing connections. QoS=AtLeastOnce with retained messages.

## Running

Run with a launch profile to select the connector:

```bash
# OPC UA
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile opcua

# MQTT
dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile mqtt
```

Launch profiles set the `DOTNET_ENVIRONMENT` variable, which loads the corresponding `appsettings.{environment}.json` file.

### What to Look For

**Success**: Each cycle prints `PASS` with convergence time:
```
=== Cycle 1: PASS (converged in 3.2s, cycle 33s) ===
```

**Failure**: Prints `FAIL` with snapshot diffs, then exits with code 1:
```
=== Cycle 3: FAIL (did not converge within 00:02:00) ===
Mismatch between server and flaky-client
```

### Log Files

Per-cycle log files are written to a `logs/` directory (relative to working directory):

```
logs/
  cycle-001-pass-2026-02-08T22-40-38.log
  cycle-002-pass-2026-02-08T22-40-54.log
  cycle-003-FAIL-2026-02-08T22-35-00.log
```

Files are created as `pending` and renamed to `pass` or `FAIL` on cycle completion. Each file contains all log output (INFO+) for that cycle with timestamps.

**Use the log files to analyze results and diagnose problems.** On failure, the `FAIL` log contains full snapshot diffs showing exactly which participants diverged and what their state was. Look for:
- Chaos event timing (`Chaos: pause/close/lifecycle on ...`) to correlate disruptions with convergence delays.
- Reconnection logs (`Attempting to reconnect`, `Reconnected successfully`) to verify recovery is working.
- Mutation counts in the statistics block to confirm all engines were active.
- Snapshot diffs at the end of failed cycles to identify which properties or participants didn't converge.

## Configuration

Configuration is loaded from `appsettings.json` with environment-specific overrides (e.g., `appsettings.mqtt.json`). The root section is `"ConnectorTester"`.

### Full Example

```json
{
  "ConnectorTester": {
    "Connector": "mqtt",
    "EnableStructuralMutations": false,
    "MutatePhaseDuration": "00:00:30",
    "ConvergenceTimeout": "00:02:00",
    "Server": {
      "Name": "server",
      "MutationRate": 200,
      "Chaos": {
        "Mode": "lifecycle",
        "IntervalMin": "00:00:05",
        "IntervalMax": "00:00:10",
        "DurationMin": "00:00:02",
        "DurationMax": "00:00:05"
      }
    },
    "Clients": [
      {
        "Name": "stable-client",
        "MutationRate": 50,
        "Chaos": null
      },
      {
        "Name": "flaky-client",
        "MutationRate": 50,
        "Chaos": {
          "Mode": "transport",
          "IntervalMin": "00:00:03",
          "IntervalMax": "00:00:08",
          "DurationMin": "00:00:02",
          "DurationMax": "00:00:05"
        }
      }
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

### Participant Configuration

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Name` | string | `""` | Participant identifier (appears in logs) |
| `MutationRate` | int | `50` | Target mutations per second |
| `Chaos` | object? | `null` | Chaos configuration (`null` = no chaos) |

### Chaos Configuration

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Mode` | string | `"transport"` | Chaos mode (see below) |
| `IntervalMin` | TimeSpan | `00:01:00` | Minimum wait before next disruption |
| `IntervalMax` | TimeSpan | `00:05:00` | Maximum wait before next disruption |
| `DurationMin` | TimeSpan | `00:00:05` | Minimum disruption hold time |
| `DurationMax` | TimeSpan | `00:00:30` | Maximum disruption hold time |

### Chaos Modes

| Mode | Target | Actions | Description |
|------|--------|---------|-------------|
| `transport` | Client (TcpProxy) | `pause`, `close` | **pause**: Drops bytes silently (simulates network partition). **close**: Disposes all TCP connections (simulates crash). |
| `lifecycle` | Server (connector) | `lifecycle` | Stops and restarts the connector service (e.g., OPC UA server or MQTT broker). |
| `both` | Either | All of above | Randomly picks from transport and lifecycle actions. |

### Port Allocation

Ports are assigned automatically based on connector type:

| Connector | Server Port | Client Proxies |
|-----------|------------|----------------|
| OPC UA | 4840 | 4841, 4842, ... |
| MQTT | 1883 | 1884, 1885, ... |
| WebSocket | 8080 | 8081, 8082, ... |

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

4. **Add `[Path]` attributes** to `TestNode.cs` properties for the new connector's path provider key.

## Troubleshooting

### Convergence Timeout

If cycles consistently fail to converge:
- Check that chaos recovery is working (look for "force-recovering" log messages during converge phase).
- For MQTT, ensure keep-alive is short enough (2s) to detect proxy PAUSE within the convergence window.
- Increase `ConvergenceTimeout` if the connector needs more time for full state synchronization.

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
    ChaosConfiguration.cs               # Chaos timing and mode
  Model/
    TestNode.cs                          # Test data model with path annotations
  Engine/
    TestCycleCoordinator.cs             # Pause/resume synchronization
    MutationEngine.cs                   # Random property mutations
    ChaosEngine.cs                      # Transport/lifecycle disruptions
    VerificationEngine.cs               # Cycle orchestration and snapshot comparison
  Chaos/
    TcpProxy.cs                         # TCP relay for network chaos injection
  Logging/
    CycleLoggerProvider.cs              # Per-cycle log file writer
  Properties/
    launchSettings.json                 # Launch profiles (opcua, mqtt, websocket)
  appsettings.json                      # Base configuration
  appsettings.opcua.json                # OPC UA profile
  appsettings.mqtt.json                 # MQTT profile
  appsettings.websocket.json            # WebSocket profile (placeholder)
```
