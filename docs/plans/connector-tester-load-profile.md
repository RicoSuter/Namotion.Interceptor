# ConnectorTester Load Profile: Design

This document describes the design for adding load testing capabilities to the ConnectorTester, unifying it with the throughput patterns currently implemented in the sample apps (WebSocket.SampleServer, Mqtt.SampleClient).

## Motivation

Today the ConnectorTester focuses on **correctness under chaos** — small object graphs (~31 nodes), moderate mutation rates (1000/sec server, 100/sec clients), fault injection, and snapshot-based eventual consistency verification.

The sample apps focus on **throughput** — large object graphs (20k Persons), high mutation rates (20k updates/sec), and latency/memory profiling via `PerformanceProfiler`.

These are separate applications with duplicated infrastructure. This design unifies them into a single tool by adding a load test profile to the ConnectorTester using the existing configuration model.

## Goals

- Add load test profiles for all three connectors (OPC UA, MQTT, WebSocket).
- Reuse the existing ConnectorTester configuration model — no new abstractions.
- Support batched parallel value mutations matching the sample app throughput pattern.
- Add performance metrics (latency percentiles, throughput, memory) with CSV output.
- Support multi-process mode via a `--participant` CLI arg for isolated load testing.
- Enable eventual deletion of the sample apps and SamplesModel library.

## Non-Goals

- Verification in multi-process mode. Multi-process runs mutations and metrics only.
- Replacing the existing chaos profiles. Chaos and load are separate launch profiles.
- Structural mutation load testing. Structural mutations remain optional at low rates using the existing `MutationEngine`.

## Design

### Launch Profiles

New launch profiles follow the existing pattern where `DOTNET_ENVIRONMENT` selects the appsettings file:

| Profile | Environment | Appsettings |
|---------|-------------|-------------|
| `opcua` (existing) | `opcua` | `appsettings.opcua.json` |
| `mqtt` (existing) | `mqtt` | `appsettings.mqtt.json` |
| `websocket` (existing) | `websocket` | `appsettings.websocket.json` |
| `opcua-load` (new) | `opcua-load` | `appsettings.opcua-load.json` |
| `mqtt-load` (new) | `mqtt-load` | `appsettings.mqtt-load.json` |
| `websocket-load` (new) | `websocket-load` | `appsettings.websocket-load.json` |

### Configuration

New fields added to `ConnectorTesterConfiguration` with backward-compatible defaults:

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `ObjectCount` | int | 31 | Number of objects in the test graph (collection children + dictionary items) |
| `BatchSize` | int | 0 | Objects per mutation batch. `0` = use existing random single-mutation via `MutationEngine`. `> 0` = batched parallel value updates. |
| `BatchIntervalMs` | int | 20 | Milliseconds between batches (when `BatchSize > 0`) |
| `MetricsReportingInterval` | TimeSpan | `00:01:00` | How often performance metrics are logged to console and CSV |

Existing fields (`MutatePhaseDuration`, `ConvergenceTimeout`, `ValueMutationRate`, `StructuralMutationRate`, `Chaos`, `ChaosProfiles`) are unchanged. The load profile simply uses different values — longer mutate phase, higher rates, no chaos.

### Load Profile Appsettings

Each load profile has server + one client, high throughput, no chaos:

```json
{
  "ConnectorTester": {
    "Connector": "opcua",
    "ObjectCount": 20000,
    "BatchSize": 400,
    "BatchIntervalMs": 20,
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

### Mutation Behavior

When `BatchSize > 0`, a new `BatchMutationEngine` replaces the existing `MutationEngine` for value mutations:

- Divides `ObjectCount` objects into batches of `BatchSize`.
- Each tick (every `BatchIntervalMs`), updates all objects in one batch with parallel tasks.
- Updates cycle through value properties (`StringValue`, `DecimalValue`, `IntValue`) using the existing global mutation counter for unique values.
- Structural mutations (if `StructuralMutationRate > 0`) still use the existing `MutationEngine`.

When `BatchSize = 0`, the existing `MutationEngine` runs as today — random single-property mutations at `ValueMutationRate` per second.

### Performance Metrics

A `PerformanceProfiler` (duplicated from `SamplesModel`, adapted for ConnectorTester) collects and reports:

- **Throughput**: Actual updates per second vs target rate.
- **Latency**: Changed latency (time from mutation to local observation) and received latency (time from remote source timestamp). Percentiles: P50, P90, P95, P99, P99.9.
- **Memory**: Heap size, MB allocated per second, process working set.
- **Queue depth**: Property change queue backlog.
- **Registry**: Reachable subject count.

Metrics are logged to console every `MetricsReportingInterval` and written to a CSV file in the `logs/` directory (hardcoded path, alongside per-cycle log files).

The profiler runs whenever `BatchSize > 0` (load mode). It does not run in chaos-only mode.

### Multi-Process Mode

An optional `--participant <name>` CLI argument runs a single participant in its own process:

```bash
# Terminal 1: Run server only
dotnet run --launch-profile opcua-load -- --participant server

# Terminal 2: Run client only
dotnet run --launch-profile opcua-load -- --participant client
```

When `--participant` is specified:
- Only the named participant (server or client) is started.
- The verification engine is **not started** — no mutate/pause/verify cycle.
- Mutations run continuously with performance metrics.
- The participant connects to the connector (OPC UA server, MQTT broker, etc.) as normal.

When `--participant` is omitted, all participants run in-process with the full verification cycle (existing behavior). This applies to both chaos and load profiles.

### Verification in Load Mode (Single Process)

In single-process load mode, the existing verification cycle works unchanged:

1. Mutations run for `MutatePhaseDuration` (e.g., 30 minutes).
2. All engines pause via `TestCycleCoordinator`.
3. Snapshots are compared with `ConvergenceTimeout`.
4. On convergence, mutations resume for the next cycle.

This validates correctness under sustained high throughput without requiring any changes to the verification engine.

### Object Graph Scaling

When `ObjectCount` is set, `TestNode` creation scales accordingly. The existing model (root + collection children + dictionary items) is used — `ObjectCount` controls the total number of child objects created.

The split between collection children and dictionary items can follow the existing ratio (20 collection + 10 dictionary = 31 total) scaled proportionally, or be simplified to all collection children for load testing. This is an implementation detail.

## New Files

| File | Description |
|------|-------------|
| `Engine/BatchMutationEngine.cs` | Batched parallel value mutation engine |
| `Engine/PerformanceProfiler.cs` | Latency/throughput/memory metrics collection and CSV output |
| `appsettings.opcua-load.json` | OPC UA load profile configuration |
| `appsettings.mqtt-load.json` | MQTT load profile configuration |
| `appsettings.websocket-load.json` | WebSocket load profile configuration |

## Modified Files

| File | Changes |
|------|---------|
| `Configuration/ConnectorTesterConfiguration.cs` | Add `ObjectCount`, `BatchSize`, `BatchIntervalMs`, `MetricsReportingInterval` |
| `Program.cs` | Parse `--participant` arg, conditionally start participant subset, wire `BatchMutationEngine` and `PerformanceProfiler` |
| `Model/TestNode.cs` | Support configurable child count |
| `Properties/launchSettings.json` | Add `opcua-load`, `mqtt-load`, `websocket-load` profiles |

## Future

Once the load profiles are validated and produce equivalent or better metrics than the sample apps:

1. Delete `Namotion.Interceptor.WebSocket.SampleServer`
2. Delete `Namotion.Interceptor.Mqtt.SampleClient`
3. Delete `Namotion.Interceptor.SamplesModel`
