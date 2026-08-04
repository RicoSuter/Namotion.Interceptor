# Modbus + SunSpec design

Date: 2026-05-04
Status: design (pre-implementation)
Scope: Modbus TCP connector for the Namotion.Interceptor stack, plus a SunSpec device library on top, with the SolarEdge inverter family as the v1 target.

## 1. Goals

v1 ships a generic Modbus TCP connector (`Namotion.Interceptor.Modbus`) and a SunSpec device library (`Namotion.Devices.SunSpec`) on top of it. The connector is reusable for any Modbus device with a static register map; SunSpec rides on the same connector and adds runtime chain-walk discovery.

The SolarEdge SunSpec inverter is the v1 hardware target; we do not yet have one, so initial development uses an in-process `SunSpecTestServer` based on `FluentModbus.Server`.

### In scope (v1)

- `[ModbusRegister]` property attribute (offset, type, address space, word order, scale, length, access)
- `[ModbusUnitId]` class attribute, `IModbusBaseAddressProvider` and `IModbusUnitIdProvider` interfaces
- Read holding registers (FC3) and input registers (FC4); write holding registers (FC6 single, FC16 multi)
- u16, s16, u32, s32, f32 numeric types; multi-register strings
- Word order overrides (`AB_CD`, `CD_AB`, `BA_DC`, `DC_BA`)
- Scale factors: dynamic (paired `_SF` register) or static
- Greedy contiguous batching up to PDU limit
- `OnConnectAsync` discovery hook
- `ModbusClientDiagnostics` mirroring `OpcUaClientDiagnostics`
- SunSpec chain-walk discovery + hand-coded Common (Model 1) and Inverter Three-Phase (Model 103)
- Reconnect/retry inherited from `SubjectSourceBackgroundService`
- HomeBlaze UI components for the SunSpec device library

### Out of scope (v1)

- Modbus RTU (serial)
- Coils (FC1, FC5, FC15) and discrete inputs (FC2)
- Bit-packed registers
- BCD encoding
- Per-property polling intervals
- Code generation of the full SunSpec model catalog (deferred to v2)
- SunSpec models other than 1 and 103 (deferred to v2 generator)
- Vendor-specific Modbus blocks (e.g. SolarEdge 64xxx control registers)

## 2. Architecture

Three packages, layered:

```
+-------------------------------------------------+
|  Namotion.Devices.SunSpec.HomeBlaze (UI)        |
|    Blazor widget / edit / setup components     |
+-------------------------------------------------+
                     |
                     v
+-------------------------------------------------+
|  Namotion.Devices.SunSpec (device library)      |
|    Model classes (Model 1, 103, ...)            |
|    Chain-walk discovery + stitching             |
|    Optional ref to HomeBlaze.Abstractions       |
+-------------------------------------------------+
                     |
                     v
+-------------------------------------------------+
|  Namotion.Interceptor.Modbus (protocol)         |
|    ISubjectSource implementation                |
|    [ModbusRegister] attribute, codecs, batcher  |
|    Polling, scale-factor handling, OnConnect    |
|    ModbusClientDiagnostics                      |
+-------------------------------------------------+
                     |
                     v
              FluentModbus / Modbus TCP
```

### Package locations

- `src/Namotion.Interceptor.Modbus/` (protocol library, .NET 9)
- `src/Namotion.Interceptor.Modbus.Tests/` (unit + integration tests)
- `src/HomeBlaze/Namotion.Devices.SunSpec/` (device library; optionally references `HomeBlaze.Abstractions`)
- `src/HomeBlaze/Namotion.Devices.SunSpec.Tests/` (device + discovery tests)
- `src/HomeBlaze/Namotion.Devices.SunSpec.HomeBlaze/` (Blazor UI)

### Module responsibilities

- **`Namotion.Interceptor.Modbus`** is the only module that talks to Modbus wire. It owns connection management, polling loop, address resolution, scale-factor computation, batching, reconnect handling, diagnostics. It does not know about SunSpec.
- **`Namotion.Devices.SunSpec`** is the only module that knows SunSpec. It owns the model class catalog (hand-coded for v1, generated in v2), the chain-walk discovery, and the `OnConnectAsync` hook that attaches discovered subjects via `property.SetValueFromSource(source, ...)`. It depends on the Modbus library and on the source generator; otherwise self-contained. Optionally references `HomeBlaze.Abstractions` so model classes can implement generic capability interfaces (`IElectricalPowerSensor`, `IElectricalEnergySensor`, `ITemperatureSensor`, ...).
- **`Namotion.Devices.SunSpec.HomeBlaze`** owns Blazor UI only. The device library above is headless-capable.

### Key invariants

1. The Modbus connector resolves a property's wire address as `(unitId, space, baseAddress + relativeOffset)` once at registration time. Subjects must be registered before polling starts; values set via `IModbusBaseAddressProvider` and `IModbusUnitIdProvider` must be assigned before the subject enters the registry.
2. SunSpec discovery is orchestrated by the connector via the `OnConnectAsync` hook. The connector invokes the hook once after a successful connect; the hook (defined in `Namotion.Devices.SunSpec`) does the SunSpec-specific chain walk and registers discovered subjects via `property.SetValueFromSource`. The connector then resolves their addresses and includes them in the next poll cycle.
3. Scale-factor properties are paired in the same poll cycle and computed by the connector before the scaled property's value is written.
4. Modbus connector exposes the OPC UA-compatible idiom (extension methods on `IServiceCollection` for DI, and `CreateModbusClientSource(this IInterceptorSubject)` for imperative wire-up in HomeBlaze devices).

## 3. Public surface

### `Namotion.Interceptor.Modbus`

```csharp
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ModbusRegisterAttribute(int offset, ModbusType type) : Attribute
{
    public int Offset { get; } = offset;
    public ModbusType Type { get; } = type;
    public AddressSpace Space { get; init; } = AddressSpace.HoldingRegister;
    public WordOrder WordOrder { get; init; } = WordOrder.AB_CD;
    public string? ScaleFactorProperty { get; init; }   // dynamic, paired _SF register
    public double Scale { get; init; } = 1.0;            // static (mutually exclusive with ScaleFactorProperty)
    public int Length { get; init; }                     // strings only: register count
    public ModbusAccess Access { get; init; } = ModbusAccess.ReadWrite;
}

public enum ModbusType   { U16, S16, U32, S32, F32, String }
public enum AddressSpace { HoldingRegister, InputRegister, Coil, DiscreteInput }
public enum WordOrder    { AB_CD, CD_AB, BA_DC, DC_BA }
public enum ModbusAccess { ReadOnly, WriteOnly, ReadWrite }

[AttributeUsage(AttributeTargets.Class)]
public sealed class ModbusUnitIdAttribute(byte unitId) : Attribute { /* ... */ }

public interface IModbusBaseAddressProvider { int  BaseAddress { get; } }
public interface IModbusUnitIdProvider      { byte UnitId      { get; } }

public sealed class ModbusClientConfiguration
{
    public required string Host { get; init; }
    public int Port { get; init; } = 1502;
    public TimeSpan PollInterval  { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RetryTime     { get; init; } = TimeSpan.FromSeconds(10);
    public Func<ModbusConnectContext, CancellationToken, Task>? OnConnectAsync { get; set; }
}

public sealed record ModbusConnectContext(
    IInterceptorSubject Root,
    ModbusTcpClient     Client,    // FluentModbus type, exposed directly
    ISubjectSource      Source);

public interface IModbusSubjectClientSource : ISubjectSource
{
    ModbusClientDiagnostics Diagnostics { get; }
}

public sealed class ModbusSubjectClientSource : IModbusSubjectClientSource { /* ... */ }

public class ModbusClientDiagnostics
{
    public bool   IsConnected            { get; }
    public bool   IsReconnecting         { get; }
    public long   TotalReconnectionAttempts { get; }
    public long   SuccessfulReconnections { get; }
    public long   FailedReconnections    { get; }
    public long   AbandonedReconnections { get; }
    public DateTimeOffset? LastConnectedAt { get; }
    public Exception? LastError           { get; }
    public double IncomingChangesPerSecond { get; }
    public double OutgoingChangesPerSecond { get; }
    public long   TotalPolls             { get; }
    public long   FailedBatches          { get; }
    public double AveragePollDurationMs  { get; }
    public DateTimeOffset? LastPollAt     { get; }
    public int    DiscoveredUnitCount    { get; }
}

public static class ModbusSubjectExtensions
{
    public static IServiceCollection AddModbusSubjectClientSource<TSubject>(
        this IServiceCollection services,
        string host, int port = 1502, TimeSpan? pollInterval = null)
        where TSubject : IInterceptorSubject;

    public static IServiceCollection AddModbusSubjectClientSource(
        this IServiceCollection services,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, ModbusClientConfiguration> configurationProvider);

    public static ModbusSubjectClientSource CreateModbusClientSource(
        this IInterceptorSubject subject,
        ModbusClientConfiguration configuration,
        ILogger logger);
}
```

### `Namotion.Devices.SunSpec`

```csharp
public interface ISunSpecModel : IInterceptorSubject, IModbusBaseAddressProvider
{
    int ModelId { get; }
}

public static class SunSpecModelRegistry
{
    public static Type? Lookup(int modelId);
    public static void  Register(int modelId, Type clrType);
}

[InterceptorSubject]
public partial class SunSpecModel1 : ISunSpecModel
{
    public int BaseAddress { get; init; }
    public int ModelId => 1;

    [ModbusRegister(0,  ModbusType.String, Length = 16)] public partial string? Manufacturer { get; set; }
    [ModbusRegister(16, ModbusType.String, Length = 16)] public partial string? Model        { get; set; }
    [ModbusRegister(32, ModbusType.String, Length = 8)]  public partial string? Version      { get; set; }
    [ModbusRegister(40, ModbusType.String, Length = 16)] public partial string? SerialNumber { get; set; }
    // ... remaining Common fields
}

[InterceptorSubject]
public partial class SunSpecModel103 : ISunSpecModel  // optionally + IElectricalPowerSensor etc.
{
    public int BaseAddress { get; init; }
    public int ModelId => 103;

    [ModbusRegister(0,  ModbusType.U16, ScaleFactorProperty = nameof(A_SF))]
    public partial double A    { get; set; }
    [ModbusRegister(4,  ModbusType.S16)] public partial short A_SF { get; set; }

    [ModbusRegister(12, ModbusType.S16, ScaleFactorProperty = nameof(W_SF))]
    public partial double W    { get; set; }
    [ModbusRegister(13, ModbusType.S16)] public partial short W_SF { get; set; }
    // ... ~25 more value/SF pairs.
    // Offsets are relative to the first data register (BaseAddress + 2 of the chain entry,
    // i.e. after the ID+L header). See Section 4.2.
}

[InterceptorSubject]
public partial class SunSpecUnit : IModbusUnitIdProvider
{
    public byte UnitId { get; init; }
    public partial Dictionary<int, ISunSpecModel>? Models { get; set; }

    [Derived] public SunSpecModel1?   Common   => Models?.GetValueOrDefault(1)   as SunSpecModel1;
    [Derived] public SunSpecModel103? Inverter => Models?.GetValueOrDefault(103) as SunSpecModel103;
    // v2 generator emits one [Derived] accessor per registered model class.
}

[InterceptorSubject]
public partial class SunSpecDevice : BackgroundService /* + IThing etc. */
{
    public partial string Host    { get; set; }
    public partial int    Port    { get; set; }
    public partial byte[] UnitIds { get; set; }

    public partial Dictionary<byte, SunSpecUnit>? Units { get; set; }

    protected override async Task ExecuteAsync(CancellationToken ct) { /* see Section 4.1 */ }
}

public static class SunSpecConnectorExtensions
{
    public static ModbusClientConfiguration ConfigureSunSpec(
        this ModbusClientConfiguration configuration,
        params byte[] unitIds);   // sets configuration.OnConnectAsync = SunSpecDiscovery.HookAsync
}
```

## 4. Data flow

### 4.1 Startup / connect

```
SunSpecDevice.ExecuteAsync (BackgroundService)
  build ModbusClientConfiguration { Host, Port }.ConfigureSunSpec(UnitIds)
  source  = ((IInterceptorSubject)this).CreateModbusClientSource(configuration, logger)
  service = new SubjectSourceBackgroundService(source, this.Context, ...)
  AttachHostedServiceAsync(source);  AttachHostedServiceAsync(service)
  Task.Delay(Infinite, ct)

SubjectSourceBackgroundService loop:
  source.StartListeningAsync(propertyWriter, ct)
    FluentModbus ModbusTcpClient.Connect(host, port)
    await configuration.OnConnectAsync(new ModbusConnectContext(root, client, source), ct)   // 4.2
    resolve absolute (unitId, space, address, count) for currently-registered properties
    start poll timer (PollInterval)
  source.LoadInitialStateAsync(ct)   // optional first synchronous read
  poll loop runs to ct; on exception: dispose, wait RetryTime, reconnect
```

### 4.2 SunSpec discovery (`SunSpecDiscovery.HookAsync`)

```
For each unitId in root.UnitIds:
  read 2 registers at 40000 (FC3, unitId)
  if value != "SunS" magic (0x53756E53): log warning, skip unit
  offset = 40002
  models = new Dictionary<int, ISunSpecModel>()
  loop:
    read 2 registers at offset -> (modelId, length)
    if modelId == 0xFFFF: break
    if SunSpecModelRegistry.Lookup(modelId) is Type t:
      models[modelId] = (ISunSpecModel)Activator.CreateInstance(t, baseAddress: offset + 2)
    else: log info "unknown modelId X", skip
    offset += length + 2

  unit = new SunSpecUnit { UnitId = unitId, Models = models }

After all unitIds:
  unitsDict = new Dictionary<byte, SunSpecUnit> { ... }
  rootUnitsProperty.SetValueFromSource(source, null, null, unitsDict)
```

The `SetValueFromSource` triggers the registry's subject-attach for `SunSpecUnit` and each `ISunSpecModel`, so they appear in the registered tree without write-back through the connector.

### 4.3 Read poll (every `PollInterval`)

```
For each registered property carrying [ModbusRegister]:
  resolve (UnitId, Space, AbsoluteAddress, RegisterCount)

Group properties by (UnitId, Space).  Sort by AbsoluteAddress.

For each group:
  build contiguous batches up to PDU limit (125 regs for FC3, similar for FC4)
  for each batch:
    issue FC3 / FC4 for (unitId, space, startAddress, count)
    decode each property's bytes per (Type, WordOrder)
    if property has ScaleFactorProperty:
      sf = lookup paired SF value from same poll's results (or last-poll cache if cross-batch)
      value = raw * 10^sf
    elif Scale != 1.0:
      value = raw * Scale
    else:
      value = raw
    propertyWriter(property, value, timestamp)
```

Scale-factor pairing: the address resolver collects SF dependencies at startup. The poll planner orders batches so SF batches run before dependents within the same poll cycle. Most often SF and dependent live in the same model and fall into the same batch, making this a no-op.

### 4.4 Write (v1: holding registers only)

```
ChangeQueueProcessor flushes batched changes -> source.WriteChangesAsync(changes)

For each change:
  resolve (UnitId, Space, AbsoluteAddress, RegisterCount)
  if Space in (InputRegister, DiscreteInput) or Access == ReadOnly: WriteResult.Failed
  if ScaleFactorProperty:
    sf = lastPollCache[change.Property.ScaleFactorProperty]
    if sf is null: WriteResult.Deferred("SF not yet known"); WriteRetryQueue picks it up after next poll
    raw = scaledValue / 10^sf
  else:
    raw = scaledValue / Scale
  encode raw per (Type, WordOrder)

Group by (UnitId, Space). Issue FC6 (single 16-bit register) or FC16 (multi-register) per group.
On per-batch failure: failures flow back through WriteResult; SubjectSourceBackgroundService's
WriteRetryQueue handles retries on next reconnect.
```

### 4.5 Reconnect

`SubjectSourceBackgroundService` already owns this loop. On exception other than the cancellation token: dispose listener, wait `RetryTime` (default 10 s), call `StartListeningAsync` again. This re-runs `OnConnectAsync`. Discovery rebuilds the subject tree from scratch and overwrites `device.Units` (idempotent in steady state; structural changes propagate as normal subject change events).

### 4.6 Dynamic additions after the hook

For subjects attached to the context after `StartListeningAsync` returns, the source re-walks the registered tree at the start of each poll cycle and re-resolves any newly-seen subjects. Cheap for typical subject counts. Subscription to registry events is a v2 optimisation if profiling shows the walk costs.

## 5. Error handling

| Category | Behavior |
|---|---|
| TCP connect failure / drop | Already handled by `SubjectSourceBackgroundService`. Logs warning, waits `RetryTime` (default 10 s), restarts `StartListeningAsync` (which re-runs discovery). Values stay at last-known state. |
| Per-batch read failure (Modbus exception, timeout) | Log warning, skip the batch this cycle, other batches continue. v1: no batch-splitting on retry; v2 may add isolation-on-failure to identify a bad register. |
| Write failure | Returns `WriteResult.Failed`. `WriteRetryQueue` (existing) retries on next reconnect; after max retries, logs error and drops. |
| Discovery: no SunS marker on a unit | Log warning, skip that unit. `device.Units` gets only the units that were found. |
| Discovery: malformed chain (length overrun, unexpected end) | Log error, abandon discovery for that unit. Other units still processed. |
| Unknown modelId on chain | Log info, skip that model. Unit built with only its known models. |
| Address resolution config error (bad `[ModbusRegister]`: negative offset, missing `BaseAddress` where required, type/length mismatch) | Throw `ModbusConfigurationException` at startup. Fail loudly. |
| Scale factor not yet known at write time | `WriteResult.Deferred`; change retries after next successful poll. Logged at debug. |
| String longer than declared `Length` | Truncate at runtime, log warning. |

`ModbusClientDiagnostics` exposes throughput, reconnect counts, last error, poll/batch statistics, and discovered unit count for HomeBlaze widgets and operators.

## 6. Testing

### Unit tests (`Namotion.Interceptor.Modbus.Tests`)

- Attribute parsing -> address resolution: `(BaseAddress, Offset, UnitId attribute, IModbusUnitIdProvider, ScaleFactorProperty)` interactions.
- Type codec: encode + decode round-trip for each `ModbusType` x each `WordOrder`.
- Scale factor: dynamic (paired SF property) and static (`Scale = 0.1`), both read and write directions.
- Batch planner: contiguous regions grouped, PDU limit (125 regs for FC3) respected, multiple batches when range exceeds limit.

### Integration tests (`Namotion.Interceptor.Modbus.Tests`, `[Category("Integration")]`)

FluentModbus.Server hosted in-process. Tests:
- Round-trip read: write registers on the server, confirm property gets the correctly scaled value.
- Round-trip write: set property, confirm server registers hold the correctly encoded raw bytes.
- Reconnect: stop + restart server, confirm source recovers and polling resumes.
- Multi-unit-ID dispatch: server with 3 unit IDs, source reads all three.

### SunSpec tests (`Namotion.Devices.SunSpec.Tests`)

- `SunSpecTestServer` helper: extends FluentModbus.Server, writes the `"SunS"` marker plus a configurable model chain (Common 1 + Inverter 103) into holding registers at startup. ~50 lines.
- Discovery test: server set up with Common + Inverter; source connects, asserts `device.Units[1].Inverter` is non-null with correct `BaseAddress`.
- End-to-end value flow: server holds known values; after one poll cycle, assert `device.Units[1].Inverter.W == expectedScaledValue`.
- Layout fixture: at least one test using register addresses + values matching SolarEdge's published SunSpec implementation, so when a real device shows up there is plausible regression coverage.

HomeBlaze E2E tests for the UI components follow the standard repo pattern (Playwright) once the UI project is built.

## 7. Implementation order

1. `SunSpecTestServer` (FluentModbus.Server-based) with a fixture matching SolarEdge's published Common (1) + Inverter (103) layout.
2. `Namotion.Interceptor.Modbus`: `[ModbusRegister]`, type codecs, word-order codec, address resolver, batch planner, source skeleton with `OnConnectAsync` hook (no real discovery yet).
3. Hand-coded `SunSpecModel1` and `SunSpecModel103` in `Namotion.Devices.SunSpec`.
4. `SunSpecDiscovery` chain walker, plugged in via `OnConnectAsync`.
5. End-to-end integration test: source connects, discovers, polls, asserts values.
6. Write path (FC6 / FC16 with reverse scale).
7. `ModbusClientDiagnostics`.
8. `Namotion.Devices.SunSpec.HomeBlaze` UI components.

## 8. Future work

- **v2 SunSpec generator.** CLI tool at `tools/Namotion.Devices.SunSpec.Generator/` that consumes the SunSpec Alliance JSON model definitions and emits `[InterceptorSubject]` partial classes into `Namotion.Devices.SunSpec/Generated/`. CI check fails if generated files differ from the JSON source. Replaces or coexists with the v1 hand-coded Model 1 + Model 103. Generator also emits one `[Derived]` accessor on `SunSpecUnit` per known model (`Common`, `Inverter`, `Meter`, `Battery`, ...).
- **v1.5 features.** Bit-packed registers, BCD encoding, coils + discrete inputs (FC1/FC2/FC5/FC15), Modbus RTU.
- **v2 features.** Per-property polling rates, registry-event-driven incremental address resolution, batch-splitting on failure.
- **Additional device libraries.** `Namotion.Devices.Eastron.SDM630`, `Namotion.Devices.Schneider.PM5560`, etc., each shipping a hand-written `[InterceptorSubject]` subject class and reusing the connector.
- **Luxtronik (Alpha Innotec / Alterra SWCV 92H3).** Unclear whether unit has Modbus TCP out of the box; primary protocol is proprietary on port 8888. To be confirmed when a device is available; may require a separate `Namotion.Interceptor.Luxtronik` connector parallel to Modbus.

## 9. Open questions to verify during implementation

- That partial properties typed as `Dictionary<int, ISunSpecModel>?` and `Dictionary<byte, SunSpecUnit>?` work with the source generator and integrate with `CreateSubjectDictionary` for polymorphic dynamic attachment. OPC UA already does similar polymorphic dictionaries; expected to work but worth confirming early.
- The exact set of `HomeBlaze.Abstractions` capability interfaces to implement on `SunSpecModel103` (`IElectricalPowerSensor`, `IElectricalEnergySensor`, etc.). Confirmed against the abstractions assembly during implementation.
- Whether `SubjectChangeContext` provides a scoped "set source" mechanism so that the discovery hook can use plain property setters instead of explicit `SetValueFromSource`. If not, the hook uses the explicit form (as OPC UA's loader does); if added later, the hook simplifies.
- Compatibility of FluentModbus's `ModbusTcpClient` with multi-unit-ID multiplexing on a single TCP connection. Expected to work; verify on the test server.
