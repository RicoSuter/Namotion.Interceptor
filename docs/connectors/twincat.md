# TwinCAT ADS

The `Namotion.Interceptor.Connectors.TwinCAT` package provides integration between Namotion.Interceptor and Beckhoff TwinCAT PLCs via the ADS (Automation Device Specification) protocol, enabling bidirectional synchronization between C# objects and PLC variables. It operates as a client source, connecting to a running TwinCAT runtime.

## Key Features

- Bidirectional synchronization between C# objects and PLC variables
- Attribute-based mapping with `[AdsVariable]` for direct symbol path control
- Automatic notification-to-polling demotion when notification limits are exceeded
- Batch read/write operations via `SumSymbolRead`/`SumSymbolWrite` with individual fallback
- Debounced rescan on connection restore, PLC state change, and symbol version change
- Circuit breaker pattern for connection resilience
- Write retry queue for buffering during disconnection
- Comprehensive diagnostics for monitoring connection health

## Client Setup

Connect to a TwinCAT PLC by configuring a client with `AddTwinCatSubjectClientSource`. The client automatically establishes connections, subscribes to variable changes, and synchronizes values with your C# properties.

```csharp
[InterceptorSubject]
public partial class PlcModel
{
    [AdsVariable("GVL.Temperature")]
    public partial double Temperature { get; set; }

    [AdsVariable("GVL.Speed")]
    public partial int Speed { get; set; }
}

builder.Services.AddTwinCatSubjectClientSource<PlcModel>(
    host: "192.168.1.100",
    pathProviderName: "ads",
    amsPort: 851);

// Use in application
var plc = serviceProvider.GetRequiredService<PlcModel>();
await host.StartAsync();
Console.WriteLine(plc.Temperature); // Read property synchronized with PLC
plc.Speed = 100; // Writes to PLC
```

## Configuration

### Simple Configuration

The generic overload provides a concise way to register a client source with sensible defaults:

```csharp
builder.Services.AddTwinCatSubjectClientSource<PlcModel>(
    host: "192.168.1.100",       // PLC IP or hostname
    pathProviderName: "ads",      // Name for AttributeBasedPathProvider
    amsPort: 851,                 // AMS port (default: 851 for TwinCAT3)
    amsNetId: "192.168.1.100.1.1" // AMS Net ID (default: "{host}.1.1")
);
```

### Advanced Configuration

For full control over all settings, use the advanced overload with `AdsClientConfiguration`:

```csharp
builder.Services.AddTwinCatSubjectClientSource(
    subjectSelector: sp => sp.GetRequiredService<PlcModel>(),
    configurationProvider: sp => new AdsClientConfiguration
    {
        // Connection (required)
        Host = "192.168.1.100",
        AmsNetId = "192.168.1.100.1.1",
        AmsPort = 851,
        PathProvider = new AttributeBasedPathProvider("ads", '.'),

        // Timeouts
        Timeout = TimeSpan.FromSeconds(5),

        // Reading strategy
        DefaultReadMode = AdsReadMode.Auto,
        DefaultCycleTime = 100,        // Notification cycle time in ms
        DefaultMaxDelay = 0,           // Max delay for notification batching in ms
        MaxNotifications = 500,        // Max concurrent notifications before demotion
        PollingInterval = TimeSpan.FromMilliseconds(100),

        // Resilience
        CircuitBreakerFailureThreshold = 5,
        CircuitBreakerCooldown = TimeSpan.FromSeconds(60),
        WriteRetryQueueSize = 1000,
        HealthCheckInterval = TimeSpan.FromSeconds(5),
        RescanDebounceTime = TimeSpan.FromSeconds(1),

        // Performance tuning
        BufferTime = TimeSpan.FromMilliseconds(8),
        RetryTime = TimeSpan.FromSeconds(1),

        // Type conversion
        ValueConverter = new AdsValueConverter()
    });
```

### Configuration Reference

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Host` | string | *required* | PLC host IP or hostname |
| `AmsNetId` | string | *required* | AMS Net ID (e.g., "192.168.1.100.1.1") |
| `AmsPort` | int | 851 | AMS port for TwinCAT3 PLC runtime |
| `PathProvider` | IPathProvider | *required* | Maps properties to ADS symbol paths |
| `Timeout` | TimeSpan | 5s | ADS communication timeout |
| `DefaultReadMode` | AdsReadMode | Auto | Default read mode for variables without explicit config |
| `DefaultCycleTime` | int | 100 | Default notification cycle time in ms |
| `DefaultMaxDelay` | int | 0 | Default max delay for notification batching in ms |
| `MaxNotifications` | int | 500 | Max concurrent ADS notifications before demotion |
| `PollingInterval` | TimeSpan | 100ms | Polling timer interval for polled/demoted variables |
| `WriteRetryQueueSize` | int | 1000 | Max queued write retries (0 to disable) |
| `HealthCheckInterval` | TimeSpan | 5s | Connection monitoring interval |
| `RescanDebounceTime` | TimeSpan | 1s | Coalesce rapid rescan requests |
| `BufferTime` | TimeSpan | 8ms | Batch inbound updates |
| `RetryTime` | TimeSpan | 1s | Failed write retry delay |
| `CircuitBreakerFailureThreshold` | int | 5 | Failures before circuit opens |
| `CircuitBreakerCooldown` | TimeSpan | 60s | Circuit breaker recovery period |
| `ValueConverter` | AdsValueConverter | new() | Type conversion handler |
| `RouterConfiguration` | IConfiguration? | null | Custom loopback port (testing) |

## Property Mapping

### Using [AdsVariable]

The `[AdsVariable]` attribute maps a property directly to an ADS symbol path. It extends `[Path]`, so it works with the standard `AttributeBasedPathProvider`.

```csharp
[InterceptorSubject]
public partial class PlcModel
{
    // Simple mapping
    [AdsVariable("GVL.Temperature")]
    public partial double Temperature { get; set; }

    // With per-property read mode and timing
    [AdsVariable("GVL.Speed", ReadMode = AdsReadMode.Notification, CycleTime = 50)]
    public partial int Speed { get; set; }

    // Polled variable with custom priority
    [AdsVariable("GVL.DiagCounter", ReadMode = AdsReadMode.Polled)]
    public partial long DiagCounter { get; set; }

    // Low-priority variable (demoted first when notification limit reached)
    [AdsVariable("GVL.AmbientTemp", Priority = 10)]
    public partial double AmbientTemperature { get; set; }
}
```

### Using [Path]

Alternatively, use the generic `[Path]` attribute with the connector name:

```csharp
[InterceptorSubject]
public partial class PlcModel
{
    [Path("ads", "GVL.Temperature")]
    public partial double Temperature { get; set; }
}
```

### [AdsVariable] Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SymbolPath` | string | *required* | The ADS symbol path (e.g., "GVL.Temperature") |
| `ReadMode` | AdsReadMode | Auto | How this variable is read from the PLC |
| `CycleTime` | int | *global default* | Notification cycle time in ms |
| `MaxDelay` | int | *global default* | Max delay for notification batching in ms |
| `Priority` | int | 0 | Demotion priority — higher values are demoted first |

### Complex Hierarchies

The library automatically handles nested object hierarchies, traversing through subject references, collections, and dictionaries:

```csharp
[InterceptorSubject]
public partial class Factory
{
    public partial ProductionLine[] Lines { get; set; }
}

[InterceptorSubject]
public partial class ProductionLine
{
    [AdsVariable("GVL.Line.Speed")]
    public partial int Speed { get; set; }

    public partial Machine? MainMachine { get; set; }
}

[InterceptorSubject]
public partial class Machine
{
    [AdsVariable("GVL.Machine.Temperature")]
    public partial double Temperature { get; set; }
}
```

The subject graph loader recursively traverses:
- **Subject references**: Uses property segment as path prefix (dot-separated)
- **Subject collections**: Uses `[index]` notation for array elements
- **Subject dictionaries**: Uses `.{key}` notation for dictionary entries
- **Circular references**: Detected and skipped via HashSet tracking

## Read Modes

The connector supports three read modes that control how variables are read from the PLC:

### Notification (Push-Based)

The PLC sends real-time device notifications whenever a value changes. This is the most responsive mode with the lowest latency, but each notification consumes a TwinCAT resource.

```csharp
[AdsVariable("GVL.CriticalSensor", ReadMode = AdsReadMode.Notification, CycleTime = 10)]
public partial double CriticalSensor { get; set; }
```

### Polled (Pull-Based)

The client periodically reads values in batches using `SumSymbolRead`. This mode is efficient for many variables but introduces latency equal to the polling interval.

```csharp
[AdsVariable("GVL.DiagCounter", ReadMode = AdsReadMode.Polled)]
public partial long DiagCounter { get; set; }
```

### Auto (Default)

Starts as notification mode but automatically demotes to polling when the `MaxNotifications` limit is exceeded. This is the recommended default for most applications.

```csharp
[AdsVariable("GVL.Temperature")] // ReadMode = AdsReadMode.Auto by default
public partial double Temperature { get; set; }
```

### Automatic Demotion Algorithm

When the total number of notification-mode variables exceeds `MaxNotifications`, the system automatically demotes excess variables from notifications to polling using a two-pass algorithm:

1. **Pass 1**: Count all properties, categorizing them by read mode (Notification, Polled, Auto)
2. **Pass 2**: If `notificationCount > MaxNotifications`, demote Auto-mode properties to Polled:
   - Sort Auto-mode properties by **Priority** descending (higher values demoted first)
   - Tiebreaker: **CycleTime** descending (slower cycle times demoted first)
   - Demote until the notification count is within the limit

**Notification-mode properties are never demoted** — only Auto-mode properties participate in demotion. Polled-mode properties are never promoted.

**Example**: With `MaxNotifications = 500` and 600 Auto-mode variables, the 100 variables with the highest Priority (then slowest CycleTime) are demoted to polling.

## Read and Write Operations

### Batch Operations

The connector uses TwinCAT's `SumSymbolRead` and `SumSymbolWrite` commands for efficient batch I/O:

- **Initial state load**: Reads all property values in a single batch operation
- **Outgoing writes**: Writes all pending changes in a single batch operation
- **Polling**: Reads all polled variables in a single batch per polling interval

### Individual Fallback

If `SumSymbolRead` or `SumSymbolWrite` returns `DeviceServiceNotSupported` (or throws it), the connector automatically falls back to individual `ReadValue`/`WriteValue` calls per symbol. This ensures compatibility with older TwinCAT runtimes that don't support sum commands.

### Error Classification

Write failures are classified as **transient** (retry-safe) or **permanent** (don't retry):

| Classification | Error Codes | Behavior |
|---------------|-------------|----------|
| **Permanent** | SymbolNotFound, InvalidSize, InvalidData, ServiceNotSupported, InvalidAccess, InvalidOffset | Logged and dropped |
| **Transient** | PortNotFound, MachineNotFound, ClientPortNotOpen, DeviceError, Timeout, Busy | Retried via write queue |
| **Unknown** | All other codes | Treated as transient (safer) |

Only transient failures are returned to the retry queue. Permanent failures are logged at Warning level and excluded from retry, preventing indefinite retry loops for invalid writes.

## Type Conversions

The `AdsValueConverter` handles bidirectional type conversion between PLC types and .NET types:

| PLC Type | .NET Type | Direction |
|----------|-----------|-----------|
| `DATE_AND_TIME` (DateTime) | DateTimeOffset | PLC → .NET |
| DateTimeOffset | DateTime (UTC) | .NET → PLC |

All other types pass through unchanged. For custom type mappings, extend `AdsValueConverter`:

```csharp
public class CustomAdsValueConverter : AdsValueConverter
{
    public override object? ConvertToPropertyValue(
        object? adsValue, RegisteredSubjectProperty property)
    {
        if (property.Type == typeof(MyEnum) && adsValue is int intValue)
            return (MyEnum)intValue;
        return base.ConvertToPropertyValue(adsValue, property);
    }

    public override object? ConvertToAdsValue(
        object? propertyValue, RegisteredSubjectProperty property)
    {
        if (propertyValue is MyEnum enumValue)
            return (int)enumValue;
        return base.ConvertToAdsValue(propertyValue, property);
    }
}
```

## Resilience

### Connection Retry with Circuit Breaker

The connector automatically retries connections when the PLC is unavailable. A circuit breaker prevents resource exhaustion during prolonged outages.

```csharp
new AdsClientConfiguration
{
    CircuitBreakerFailureThreshold = 5,          // Open after 5 consecutive failures
    CircuitBreakerCooldown = TimeSpan.FromSeconds(60), // Wait before retrying
    HealthCheckInterval = TimeSpan.FromSeconds(5)       // Time between retry attempts
}
```

**Behavior:**
- Connection attempts use `ConnectWithRetryAsync` with exponential backoff
- After `CircuitBreakerFailureThreshold` consecutive failures, the circuit breaker opens
- While open, connection attempts are skipped until the cooldown period expires
- Successful connection resets the circuit breaker

### Write Retry Queue

The client automatically queues write operations when the connection is lost. Queued writes are flushed in FIFO order when the connection is restored. This is provided by the `SubjectSourceBackgroundService`.

```csharp
new AdsClientConfiguration
{
    WriteRetryQueueSize = 1000 // Buffer up to 1000 writes (default)
}
```

- Ring buffer semantics: drops oldest when full
- Automatic flush after reconnection
- Set to 0 to disable

### Write Behavior During Rescan

When a rescan is in progress (triggered by connection restore, PLC state change, or symbol version change), the symbol path cache is temporarily cleared. Writes that arrive during this window are handled as follows:

- **Unresolved symbol paths** (cache temporarily cleared): Treated as transient failures and queued for retry. After the rescan completes and the symbol cache is rebuilt, the retry succeeds.
- **Transient ADS errors** (timeout, busy, port not found): Queued for retry via the write retry queue.
- **Permanent ADS errors** (symbol not found, invalid size/data): Logged at Warning level and dropped — not retried. This prevents indefinite retry of writes for symbols that no longer exist on the PLC (e.g., after a PLC program update).

This ensures that configuration changes and command triggers are not silently lost during brief rescan windows, while writes to permanently removed symbols are cleaned up automatically.

### Debounced Rescan

When the connection state changes, the PLC enters Run state, or the symbol version changes, the connector triggers a full rescan. Multiple rapid events (common during reconnection) are coalesced into a single rescan using debouncing:

1. Event handler calls `RequestRescan()`, recording the timestamp and signaling a semaphore
2. The `ExecuteAsync` background loop wakes up and waits for the debounce period to elapse
3. If new events arrive during the debounce wait, the timer restarts
4. After the debounce period, a single `FullRescan()` executes

A full rescan:
1. Clears all existing subscriptions and caches
2. Recreates the symbol loader from the current connection
3. Reloads the subject graph (property → symbol path mappings)
4. Re-registers all notification and polling subscriptions
5. Loads initial state and replays buffered updates

```csharp
new AdsClientConfiguration
{
    RescanDebounceTime = TimeSpan.FromSeconds(1) // Coalesce events within 1 second
}
```

### Connection Events

The connector responds to four connection events:

| Event | Response |
|-------|----------|
| **Connection Restored** | Start buffering updates, request debounced rescan |
| **Connection Lost** | Start buffering updates (pause direct writes) |
| **PLC Entered Run State** | Request debounced rescan |
| **Symbol Version Changed** | Request debounced rescan |

### First-Occurrence Error Logging

To reduce log noise during sustained error conditions, the connector uses a first-occurrence logging pattern:

- **First occurrence**: Logged at Warning level
- **Subsequent occurrences**: Logged at Debug level
- **After recovery**: The first-occurrence flag is cleared, so the next error is logged at Warning again

This applies to connection failures, symbol lookup failures, and batch polling errors.

## Diagnostics

Monitor client health in production via the `Diagnostics` property on the source:

```csharp
var source = serviceProvider.GetRequiredKeyedService<TwinCatSubjectClientSource>(key);
var diagnostics = source.Diagnostics;

Console.WriteLine($"Connected: {diagnostics.IsConnected}");
Console.WriteLine($"PLC State: {diagnostics.State}");
Console.WriteLine($"Notifications: {diagnostics.NotificationVariableCount}");
Console.WriteLine($"Polled: {diagnostics.PolledVariableCount}");
Console.WriteLine($"Reconnection Attempts: {diagnostics.TotalReconnectionAttempts}");
Console.WriteLine($"Circuit Breaker Open: {diagnostics.IsCircuitBreakerOpen}");
```

### Diagnostic Properties

| Property | Type | Description |
|----------|------|-------------|
| `State` | AdsState? | Current PLC state (Run, Stop, etc.) |
| `IsConnected` | bool | Whether the ADS client is connected |
| `NotificationVariableCount` | int | Active notification subscriptions |
| `PolledVariableCount` | int | Active polling subscriptions |
| `TotalReconnectionAttempts` | long | Total reconnection attempts since startup |
| `SuccessfulReconnections` | long | Successful reconnections |
| `FailedReconnections` | long | Failed reconnections |
| `LastConnectedAt` | DateTimeOffset? | Last successful connection time |
| `IsCircuitBreakerOpen` | bool | Whether the circuit breaker is currently open |
| `CircuitBreakerTripCount` | long | Number of times the circuit breaker has tripped |

## Thread Safety

The connector ensures thread-safe operations across all ADS interactions:

- Property caches use `ConcurrentDictionary` with `PropertyReference.Comparer`
- Reconnection and state counters use `Interlocked` operations
- The rescan signal uses `SemaphoreSlim` with a capacity of 1
- Subscription and polling collections are safely cleared and rebuilt during rescan

Property updates from ADS notifications and polling callbacks are applied via `SubjectPropertyWriter`, which handles buffering during initialization and ensures correct ordering.

## Lifecycle Management

The TwinCAT connector hooks into the interceptor lifecycle system (see [Subject Lifecycle Tracking](../tracking.md#subject-lifecycle-tracking)) to clean up resources when subjects are detached via `SourceOwnershipManager`.

### Automatic Cleanup on Subject Detach

When a subject is detached from the object graph:

- ADS notification subscriptions for the subject's properties are disposed
- Properties are removed from the batch polling collection (and polling is marked dirty)
- Symbol-to-property and property-to-symbol cache entries are removed
- Source ownership is released

### Automatic Cleanup on Property Release

When an individual property is released:

1. ADS notification subscription is disposed (if notification mode)
2. Property is removed from the polled collection (if polling mode)
3. Bidirectional symbol-path lookups are cleared

## Architecture

```
┌──────────────────────────────────────────────────┐
│  Subject Graph (with [AdsVariable] attributes)   │
└──────────────┬───────────────────────────────────┘
               │
               ▼
┌──────────────────────────────────────────────────┐
│  TwinCatSubjectClientSource                      │
│  (BackgroundService + ISubjectSource)            │
│                                                  │
│  ├─ AdsConnectionManager                         │
│  │   ├─ AdsClient (connection to PLC)            │
│  │   ├─ CircuitBreaker (retry logic)             │
│  │   └─ Events: ConnectionRestored/Lost,         │
│  │            AdsStateChanged, SymbolVersion     │
│  │                                               │
│  ├─ AdsSubscriptionManager                       │
│  │   ├─ Notification subscriptions               │
│  │   ├─ Batch polling (SumSymbolRead)            │
│  │   └─ Auto-demotion algorithm                  │
│  │                                               │
│  ├─ AdsSubjectLoader                             │
│  │   └─ LoadSubjectGraph() → symbol paths        │
│  │                                               │
│  └─ ExecuteAsync loop                            │
│      └─ Debounced rescan on events               │
└──────────────────────────────────────────────────┘
```

### Initialization Sequence

1. `StartListeningAsync()` connects to the PLC via `ConnectWithRetryAsync()`
2. `FullRescan()` loads the subject graph and registers subscriptions
3. `LoadInitialStateAsync()` batch-reads all property values from the PLC
4. The `ExecuteAsync` background loop handles health checks and debounced rescans
5. Inbound notifications and polling updates are applied via `SubjectPropertyWriter`
6. Outbound property changes are written via `WriteChangesAsync()` with batch operations

## Known Limitations

The following items are known limitations of the current implementation. They are tracked for future improvement.

### No active health probing

The connector relies on the `AdsClient`'s internal connection state machine for reconnection. If the `AdsClient` instance itself becomes unresponsive (e.g., due to a Beckhoff SDK bug or ADS router restart), no `ConnectionStateChanged` event fires and the system stays disconnected. The `ExecuteAsync` loop of the `BackgroundService` already runs periodically on the `HealthCheckInterval` and would be the natural place to add active health probing (e.g., periodic `ReadStateAsync` calls to detect a dead `AdsClient` and recreate it).

### Subject detach performance

When a subject is detached from the object graph, `OnSubjectDetaching` iterates all entries in the symbol-to-property cache (`O(n)` scan) to find and remove entries belonging to the detached subject. For very large symbol sets (thousands of variables) with frequent detach operations, this could become a bottleneck. A reverse lookup (subject to symbol paths) would improve this to `O(1)` per subject.

### Null value write behavior

When a C# property value converts to `null` via `AdsValueConverter.ConvertToAdsValue`, the write is silently skipped with a Debug-level log message. PLCs generally do not have a concept of `null`, so passing `null` to the Beckhoff SDK could cause unexpected behavior. If you need to write a "zero" or default value, ensure your `AdsValueConverter` returns a non-null default instead of `null`.
