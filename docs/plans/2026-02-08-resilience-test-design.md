# Resilience Test Application Design

## Goal

A single-process console application that runs continuously (days/weeks) to validate eventual consistency across all connectors (WebSocket, MQTT, OPC UA). It hosts a server and multiple clients in-process, mutates models concurrently from all sides, injects chaos (transport failures, service restarts), and periodically verifies that all models converge to the same state.

## Architecture

### High-Level Overview

```
┌───────────────────────────────────────────────────────────────┐
│                     Resilience Test Process                    │
│                                                               │
│  ┌──────────┐     ┌─────────┐     ┌──────────────────────┐   │
│  │  Server   │◄───►│ Proxy 1 │◄───►│ Client 1 (+ model)   │   │
│  │  (model)  │◄───►│ Proxy 2 │◄───►│ Client 2 (+ model)   │   │
│  │          │◄───►│ Proxy 3 │◄───►│ Client 3 (+ model)   │   │
│  └──────────┘     └─────────┘     └──────────────────────┘   │
│       ▲               ▲                    ▲                  │
│  ┌────┴────┐     ┌────┴────┐          ┌────┴────┐            │
│  │Mutation │     │  Chaos  │          │Mutation │            │
│  │Engine   │     │ Engines │          │Engines  │            │
│  └─────────┘     └─────────┘          └─────────┘            │
│                                                               │
│  ┌───────────────────────────────────────────────────────┐   │
│  │              Verification Engine                       │   │
│  │   (orchestrates mutate/converge cycles)                │   │
│  └───────────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────────────────────┘
```

### Verification Cycle

The test runs in repeating cycles:

```
|── mutate phase (configurable) ──|── converge phase (up to timeout) ──|── repeat
    all mutations active               mutations + chaos paused
    chaos active                        poll-compare JSON snapshots
    all sides write same properties     until all N+1 models match
```

1. **Mutate phase**: All mutation engines run concurrently, writing to the same properties on their respective models. Chaos engines disrupt transport and lifecycle. Duration is configurable (e.g., 5 minutes).
2. **Converge phase**: All mutation and chaos engines pause. Active chaos disruptions are recovered first (proxies resumed, services restarted). Poll-compare JSON-serialized snapshots of all models every second. If all snapshots match within the convergence timeout (e.g., 60 seconds), the cycle passes. If timeout expires with mismatches, the cycle fails and the process exits non-zero.

## Components

### 1. TCP Proxy

A lightweight in-process TCP relay that sits between each client and the server. Completely connector-agnostic since all connectors (WebSocket, MQTT, OPC UA) use TCP.

**Design:**

```
TcpProxy(listenPort, targetPort)
│
├── TcpListener on listenPort
│   └── Accept loop: for each incoming connection
│       ├── Open matching connection to targetPort
│       ├── Track both sockets in ActiveConnections list
│       └── Start two forwarding tasks:
│           ├── client→server: read from client, write to server
│           └── server→client: read from server, write to client
│
├── State: Open | Paused
└── Control API
```

**Forwarding loop** (per direction):

```csharp
while connected:
    bytes = await sourceStream.ReadAsync(buffer)
    if state == Paused:
        // drop bytes (don't buffer) - simulates real network partition
        continue
    await targetStream.WriteAsync(buffer, 0, bytes)
```

**Control API:**

| Method | Simulates | Detection by Connector |
|--------|-----------|----------------------|
| `CloseAllConnections()` | Process crash, clean disconnect | Immediate (TCP RST) |
| `PauseForwarding()` | Network partition, switch failure | Slow (heartbeat/receive timeout) |
| `ResumeForwarding()` | Network recovery | Connector reconnects on new connection |

Both chaos actions are used because they exercise fundamentally different failure paths:
- **Close**: Connector gets an exception on the next read/write, reconnects immediately. Tests the fast failure path.
- **Pause (drop bytes)**: Connection appears alive but nothing gets through. Connector must detect failure via heartbeat/receive timeout. This is the most common and dangerous real-world failure mode (cable unplugged, switch failure, cloud network hiccup). Tests the slow/timeout detection path.

Each client gets its own proxy instance so chaos can target individual clients independently.

**Port allocation:**

| Connector | Server Port | Client Proxy Ports |
|-----------|------------|-------------------|
| WebSocket | 8080 | 8081, 8082, 8083, ... |
| MQTT | 1883 | 1884, 1885, 1886, ... |
| OPC UA | 4840 | 4841, 4842, 4843, ... |

### 2. Mutation Engine (BackgroundService)

Each participant (server + each client) has its own mutation engine that randomly mutates the model. All sides write to the same properties concurrently to test last-write-wins semantics and eventual consistency.

**No registry-based discovery** - the mutator has hardcoded knowledge of the `TestNode` shape. It maintains a flat `List<TestNode>` of all known nodes (root + root.Collection children + root.Items values + any non-null ObjectRefs). This list is rebuilt after any structural mutation.

**Initial known list**: root + 20 collection children + 10 dict entries = 31 nodes, ~155 value properties.

**Operation types:**

| Category | Operations | Initially Enabled |
|----------|-----------|-------------------|
| Value update | Pick random node, pick random value property (string/decimal/int/bool), set random value | Yes |
| Object ref set | Pick random node, create new `TestNode`, assign to `ObjectRef`, add to known list | No (WebSocket only) |
| Object ref clear | Pick random node, set `ObjectRef` to null, remove from known list | No (WebSocket only) |
| Collection add | Create new `TestNode`, append to root's `Collection` | No (WebSocket only) |
| Collection remove | Remove random item from root's `Collection` | No (WebSocket only) |
| Collection move | Swap two positions in root's `Collection` | No (WebSocket only) |
| Dictionary add | Create new `TestNode`, add to root's `Items` with stable key | No (WebSocket only) |
| Dictionary remove | Remove random entry from root's `Items` | No (WebSocket only) |

Initially only value mutations are enabled (works with OPC UA and MQTT). Structural mutations are gated behind configuration and enabled when WebSocket connector support lands (PR #150).

**Value mutation loop** (initial implementation):

```csharp
while (!cancellationToken.IsCancellationRequested)
{
    _coordinator.WaitIfPaused(cancellationToken);

    var node = _knownNodes[_random.Next(_knownNodes.Count)];
    var property = _random.Next(4);
    switch (property)
    {
        case 0: node.StringValue = Guid.NewGuid().ToString("N")[..8]; break;
        case 1: node.DecimalValue = _random.Next(0, 100000) / 100m; break;
        case 2: node.IntValue = _random.Next(0, 100000); break;
        case 3: node.BoolValue = !node.BoolValue; break;
    }

    _mutationCount++;
    await Task.Delay(1000 / _mutationRate, cancellationToken);
}
```

**Graph size bounds** (for when structural mutations are enabled): Collection add only if `count < maxSize` (e.g., 30), remove only if `count > minSize` (e.g., 5). Prevents unbounded growth over multi-day runs.

**Collection/dictionary mutations use replace semantics** (interceptors only fire on property assignment, not in-place mutation):

```csharp
// Collection add - spread into new array
root.Collection = [..root.Collection, newNode];

// Collection remove
root.Collection = root.Collection.Where(n => n != target).ToArray();

// Collection move (swap positions)
var list = root.Collection.ToList();
(list[i], list[j]) = (list[j], list[i]);
root.Collection = list.ToArray();

// Dictionary add - create new dict
root.Items = new Dictionary<string, TestNode>(root.Items)
    { [key] = newNode };

// Dictionary remove
root.Items = root.Items
    .Where(kv => kv.Key != targetKey)
    .ToDictionary(kv => kv.Key, kv => kv.Value);
```

**Pausability** via shared `TestCycleCoordinator`:

```csharp
while (!cancellationToken.IsCancellationRequested)
{
    _coordinator.WaitIfPaused(cancellationToken);
    PerformRandomMutation();
    _mutationCount++;
    await Task.Delay(1000 / _mutationRate, cancellationToken);
}
```

**Thread safety**: Each engine only writes to its own model. The connector concurrently applies incoming updates to the same model. No additional locking needed - this is the real-world scenario.

**Statistics tracking**: Each engine maintains per-operation-type counters (values, refs, collection add/remove/move, dict add/remove) reported at end of each cycle.

### 3. Chaos Engine (BackgroundService)

A per-participant component that randomly triggers disruptions. Supports two modes (configurable independently or combined):

**Transport mode** - controls the TCP proxy:
- `CloseAllConnections()` - immediate disconnect, tests fast reconnection path
- `PauseForwarding()` - silent partition, tests timeout detection path

**Lifecycle mode** - controls the connector's BackgroundService:
- `StopAsync()` then `StartAsync()` - tests full service restart and re-initialization

**Loop:**

```csharp
while (!cancellationToken.IsCancellationRequested)
{
    _coordinator.WaitIfPaused(cancellationToken);
    await Task.Delay(random(IntervalMin, IntervalMax), cancellationToken);
    _coordinator.WaitIfPaused(cancellationToken);

    var action = PickRandomAction(); // based on Mode config
    log $"Chaos: {action} on {_targetName}"
    action.Execute();

    await Task.Delay(random(DurationMin, DurationMax), cancellationToken);
    action.Recover();
    log $"Chaos: {_targetName} recovered"
    _chaosEventCount++;
}
```

When `Mode = "both"`, randomly picks from all available actions (transport + lifecycle).

**Recovery on converge**: The chaos engine tracks its current active disruption. The verification engine calls `RecoverAllActiveChaosDisruptions()` before starting the poll-compare phase to ensure all connections are restored.

### 4. Verification Engine (BackgroundService)

The top-level orchestrator. Uses a `TestCycleCoordinator` to signal all engines.

**TestCycleCoordinator:**

```csharp
public class TestCycleCoordinator
{
    private readonly ManualResetEventSlim _runSignal = new(true);

    public void Pause() => _runSignal.Reset();
    public void Resume() => _runSignal.Set();
    public void WaitIfPaused(CancellationToken ct) => _runSignal.Wait(ct);
}
```

**Verification loop:**

```
while not cancelled:
    // 1. Mutate phase
    coordinator.Resume()
    log "Cycle {n}: Mutate phase started"
    await Task.Delay(MutatePhaseDuration)

    // 2. Transition to converge
    coordinator.Pause()
    RecoverAllActiveChaosDisruptions()  // resume proxies, restart services
    await Task.Delay(1s)  // grace period for in-flight operations

    // 3. Poll compare
    converged = false
    for poll in 1..ConvergenceTimeout:
        snapshots = models.Select(m =>
            JsonSerializer.Serialize(SubjectUpdate.CreateCompleteUpdate(m, processors)))
        if all snapshots equal:
            log PASS + convergence time + mutation count + chaos event count
            WriteStatistics()
            cycleLogger.RollToNextFile()
            converged = true
            break
        await Task.Delay(1s)

    if not converged:
        log FAIL + snapshot diffs + full snapshots
        WriteStatistics()
        Environment.Exit(1)
```

**Snapshot comparison**: `SubjectUpdate.CreateCompleteUpdate()` on each model, JSON-serialize, string compare. No custom deep-compare logic needed.

### 5. Cycle Logger Provider (ILoggerProvider)

A custom logger provider that writes all log output to both console and a per-cycle log file. All `ILogger` output from connectors, chaos engines, mutation engines, and verification engine goes to both destinations.

**Log file naming:**

```
logs/
├── cycle-001-pass-2026-02-08T14-00-00.log
├── cycle-002-pass-2026-02-08T14-05-32.log
├── cycle-003-FAIL-2026-02-08T14-10-45.log
```

**Log content**: Connector lifecycle events (reconnection, resync, errors), chaos events, and verification results. Individual mutation values are NOT logged.

**Per-cycle statistics** written at end of each cycle:

```
--- Cycle 3 Statistics ---
Duration: 5m 2s (converged in 3s)

  Participant        | Values | Refs | Col.Add | Col.Rm | Col.Mv | Dict.Add | Dict.Rm | Chaos
  server             |  2,841 |  312 |      98 |     87 |     45 |       32 |      28 |     1
  stable-client      |  5,682 |  624 |     196 |    174 |     90 |       64 |      56 |     0
  flaky-client       |  1,420 |  156 |      49 |     43 |     22 |       16 |      14 |     3
  flaky-client-2     |  9,470 | 1,040|     327 |    290 |    150 |      106 |       93 |     5

  Total mutations: 23,394 | Total chaos events: 9
  Result: PASS
```

This makes it easy to verify all operation types are exercised and to correlate failure patterns with specific operation mixes.

**On failure**, the log additionally includes:
- Snapshot diffs showing which properties diverged and values on each side
- Full JSON snapshots of all models

**File rolling**: The verification engine signals the logger provider to start a new file at the beginning of each cycle. The previous file is finalized with the cycle result (pass/fail) appended to the filename.

## Test Model

A single self-referential `TestNode` class. Property names directly describe what they test - when a failure says `TestNode.StringValue` diverged, the problem is immediately obvious. The mutator has hardcoded knowledge of this shape; no registry-based discovery needed.

```csharp
[InterceptorSubject]
public partial class TestNode
{
    [Path("opc", "StringValue")]
    [Path("mqtt", "StringValue")]
    [Path("ws", "StringValue")]
    public partial string StringValue { get; set; }

    [Path("opc", "DecimalValue")]
    [Path("mqtt", "DecimalValue")]
    [Path("ws", "DecimalValue")]
    public partial decimal DecimalValue { get; set; }

    [Path("opc", "IntValue")]
    [Path("mqtt", "IntValue")]
    [Path("ws", "IntValue")]
    public partial int IntValue { get; set; }

    [Path("opc", "BoolValue")]
    [Path("mqtt", "BoolValue")]
    [Path("ws", "BoolValue")]
    public partial bool BoolValue { get; set; }

    [Path("opc", "ObjectRef")]
    [Path("mqtt", "ObjectRef")]
    [Path("ws", "ObjectRef")]
    public partial TestNode? ObjectRef { get; set; }

    [Path("opc", "Collection")]
    [Path("mqtt", "Collection")]
    [Path("ws", "Collection")]
    public partial TestNode[] Collection { get; set; }

    [Path("opc", "Items")]
    [Path("mqtt", "Items")]
    [Path("ws", "Items")]
    public partial Dictionary<string, TestNode> Items { get; set; }

    public TestNode()
    {
        StringValue = string.Empty;
        DecimalValue = 0;
        IntValue = 0;
        BoolValue = false;
        ObjectRef = null;
        Collection = [];
        Items = new Dictionary<string, TestNode>();
    }
}
```

**Initial graph setup** (per participant):

```csharp
var root = new TestNode(context);

// 20 children in collection
root.Collection = Enumerable.Range(0, 20)
    .Select(_ => new TestNode(context))
    .ToArray();

// 10 entries in dictionary with stable keys
root.Items = Enumerable.Range(0, 10)
    .ToDictionary(i => $"item-{i}", i => new TestNode(context));
```

Root has 20 collection children + 10 dict entries = 31 nodes total (including root). Children and dict entries start with empty collections and default values. This gives ~155 value properties (31 nodes x 5 value props) being mutated concurrently - enough to stress sync without being overwhelming.

Nodes created via `ObjectRef` mutations (when enabled) are also added to the mutator's known node list and have their value properties mutated.

## Configuration

Per-connector configuration via environment-specific appsettings files loaded by launch profile. Common settings in `appsettings.json`, connector-specific overrides in `appsettings.{profile}.json`.

**Launch profiles** (`Properties/launchSettings.json`):

```json
{
  "profiles": {
    "opcua": {
      "commandName": "Project",
      "environmentVariables": { "DOTNET_ENVIRONMENT": "opcua" }
    },
    "mqtt": {
      "commandName": "Project",
      "environmentVariables": { "DOTNET_ENVIRONMENT": "mqtt" }
    },
    "websocket": {
      "commandName": "Project",
      "environmentVariables": { "DOTNET_ENVIRONMENT": "websocket" }
    }
  }
}
```

Usage: `dotnet run --launch-profile opcua`

**`appsettings.json`** (shared defaults):

```json
{
  "ResilienceTest": {
    "MutatePhaseDuration": "00:05:00",
    "ConvergenceTimeout": "00:01:00",
    "Server": {
      "MutationRate": 50,
      "Chaos": {
        "Mode": "lifecycle",
        "IntervalMin": "00:01:00",
        "IntervalMax": "00:05:00",
        "DurationMin": "00:00:05",
        "DurationMax": "00:00:30"
      }
    },
    "Clients": [
      {
        "Name": "stable-client",
        "MutationRate": 100,
        "Chaos": null
      },
      {
        "Name": "flaky-client",
        "MutationRate": 50,
        "Chaos": {
          "Mode": "both",
          "IntervalMin": "00:00:10",
          "IntervalMax": "00:00:30",
          "DurationMin": "00:00:02",
          "DurationMax": "00:00:15"
        }
      },
      {
        "Name": "flaky-client-2",
        "MutationRate": 200,
        "Chaos": {
          "Mode": "transport",
          "IntervalMin": "00:00:05",
          "IntervalMax": "00:00:20",
          "DurationMin": "00:00:01",
          "DurationMax": "00:00:10"
        }
      }
    ]
  }
}
```

**`appsettings.opcua.json`** (value mutations only):

```json
{
  "ResilienceTest": {
    "Connector": "opcua",
    "EnableStructuralMutations": false
  }
}
```

**`appsettings.mqtt.json`** (value mutations only):

```json
{
  "ResilienceTest": {
    "Connector": "mqtt",
    "EnableStructuralMutations": false
  }
}
```

**`appsettings.websocket.json`** (full mutations including structural):

```json
{
  "ResilienceTest": {
    "Connector": "websocket",
    "EnableStructuralMutations": true,
    "MutationWeights": {
      "Value": 60,
      "ObjectRef": 10,
      "CollectionAdd": 10,
      "CollectionRemove": 10,
      "CollectionMove": 5,
      "DictionaryAdd": 2.5,
      "DictionaryRemove": 2.5
    }
  }
}
```

**Configuration fields:**

| Field | Description |
|-------|-------------|
| `Connector` | Which connector to test: `websocket`, `mqtt`, `opcua` |
| `EnableStructuralMutations` | Enable collection/dict/ref mutations (default false, requires WebSocket) |
| `MutatePhaseDuration` | How long mutations run before convergence check |
| `ConvergenceTimeout` | Max time to wait for all models to match |
| `MutationRate` | Mutations per second for this participant |
| `MutationWeights` | Per-operation-type weights when structural mutations enabled |
| `Chaos.Mode` | `transport`, `lifecycle`, or `both` |
| `Chaos.IntervalMin/Max` | Random range between chaos events |
| `Chaos.DurationMin/Max` | Random range for disruption duration |
| `Chaos: null` | No chaos for this participant (stable) |

## Connector Wiring

Each participant creates its own `IInterceptorSubjectContext` and `TestNode` root model upfront, including the initial graph (20 collection children, 10 dict entries). The model reference is passed into the connector via the subject selector overload. This allows the verification engine to hold references to all models for snapshot comparison.

```csharp
// Server
var serverContext = CreateContext();
var serverModel = CreateTestGraph(serverContext); // root + 20 children + 10 dict entries
services.AddWebSocketSubjectServer(
    _ => serverModel,
    _ => new WebSocketServerConfiguration
    {
        Port = serverPort,
        PathProvider = new AttributeBasedPathProvider("ws"),
    });

// Client (pointing at proxy port, not server port)
var clientContext = CreateContext();
var clientModel = CreateTestGraph(clientContext);
services.AddWebSocketSubjectClientSource(
    _ => clientModel,
    _ => new WebSocketClientConfiguration
    {
        ServerUri = new Uri($"ws://localhost:{proxyPort}/ws"),
        PathProvider = new AttributeBasedPathProvider("ws"),
    });
```

All connector-specific configuration (timeouts, buffer times, reconnect delays, batch sizes) uses defaults. If a default config is not resilient, that's a bug to fix in the connector, not something to work around in the test.

Connector-specific wiring per type:

| Connector | Server Setup | Client URL Format |
|-----------|-------------|-------------------|
| WebSocket | `AddWebSocketSubjectServer` | `ws://localhost:{proxyPort}/ws` |
| MQTT | `AddMqttSubjectServer` | `BrokerHost=localhost, BrokerPort={proxyPort}` |
| OPC UA | `AddOpcUaSubjectServer` | `opc.tcp://localhost:{proxyPort}` |

## Project Structure

```
src/
└── Namotion.Interceptor.ResilienceTest/
    ├── Namotion.Interceptor.ResilienceTest.csproj
    ├── Program.cs
    ├── Properties/
    │   └── launchSettings.json
    ├── appsettings.json
    ├── appsettings.opcua.json
    ├── appsettings.mqtt.json
    ├── appsettings.websocket.json
    ├── Configuration/
    │   ├── ResilienceTestConfiguration.cs
    │   ├── ParticipantConfiguration.cs
    │   └── ChaosConfiguration.cs
    ├── Model/
    │   └── TestNode.cs
    ├── Engine/
    │   ├── TestCycleCoordinator.cs
    │   ├── MutationEngine.cs
    │   ├── ChaosEngine.cs
    │   └── VerificationEngine.cs
    ├── Chaos/
    │   └── TcpProxy.cs
    └── Logging/
        └── CycleLoggerProvider.cs
```

## Exit Behavior

- **Success**: Process runs indefinitely, cycling through mutate/converge phases. Manual stop via Ctrl+C.
- **Failure**: Process exits with non-zero exit code on first convergence failure. The cycle log file contains all information needed to investigate.
