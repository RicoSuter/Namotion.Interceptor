# SunSpec device library design

Date: 2026-09-28
Status: draft, carried over from `2026-05-04-modbus-design.md` with review corrections. Brainstorm again before writing its plan.
Depends on: [2026-09-28-modbus-luxtronik-design.md](2026-09-28-modbus-luxtronik-design.md) (connector read side), and the connector write stage for controls.

## 1. Goals

`Namotion.Devices.SunSpec` (+ `.HomeBlaze` UI, + `.Tests`) models SunSpec devices on top of `Namotion.Interceptor.Modbus`. SunSpec devices describe themselves with a model chain that is discovered at runtime, so model instances live at addresses only known after connecting. The first hardware target is a SolarEdge SunSpec inverter; development starts against an in-process `SunSpecTestServer` because no device is available yet.

### In scope (first version)

- Chain-walk discovery through `IModbusDiscovery`
- Hand-coded Common (Model 1) and Inverter Three-Phase (Model 103)
- Several unit IDs on one connection (SolarEdge exposes inverter, meters and batteries under different unit IDs)
- HomeBlaze device and UI

### Out of scope (first version)

- Generating the full SunSpec model catalog from the SunSpec Alliance JSON definitions (second version)
- Models other than 1 and 103
- Vendor-specific blocks (for example SolarEdge 0xF000 control registers)
- Controls (need the connector write stage)

## 2. Architecture

```
Namotion.Devices.SunSpec.HomeBlaze   Blazor UI                                    net10.0
            |
Namotion.Devices.SunSpec             model classes, discovery, HomeBlaze device   net10.0
            |
Namotion.Interceptor.Modbus          generic connector
```

Locations: `src/HomeBlaze/Namotion.Devices.SunSpec/`, `src/HomeBlaze/Namotion.Devices.SunSpec.HomeBlaze/`, `src/HomeBlaze/Namotion.Devices.SunSpec.Tests/`.

## 3. Public surface (sketch)

```csharp
public interface ISunSpecModel : IInterceptorSubject, IModbusBaseAddressProvider
{
    int ModelId { get; }
}

public static class SunSpecModelRegistry
{
    // Factories instead of Type + Activator: AOT friendly and lets the factory set the base address.
    public static void Register(int modelId, Func<int, ISunSpecModel> factory);   // factory(baseAddress)
    public static ISunSpecModel? TryCreate(int modelId, int baseAddress);
}

[InterceptorSubject]
public partial class SunSpecModel1 : ISunSpecModel, IDeviceInfo
{
    public SunSpecModel1(int baseAddress) { BaseAddress = baseAddress; }
    public int BaseAddress { get; }
    public int ModelId => 1;

    [ModbusRegister(0,  ModbusDataType.String, Length = 16, Access = ModbusAccess.ReadOnly)] public partial string? Manufacturer { get; set; }
    [ModbusRegister(16, ModbusDataType.String, Length = 16, Access = ModbusAccess.ReadOnly)] public partial string? Model        { get; set; }
    [ModbusRegister(40, ModbusDataType.String, Length = 8,  Access = ModbusAccess.ReadOnly)] public partial string? Version      { get; set; }
    [ModbusRegister(48, ModbusDataType.String, Length = 16, Access = ModbusAccess.ReadOnly)] public partial string? SerialNumber { get; set; }
    // remaining Common fields; offsets verified against the SunSpec model 1 definition during implementation
}

[InterceptorSubject]
public partial class SunSpecModel103 : ISunSpecModel, IPowerSensor
{
    public SunSpecModel103(int baseAddress) { BaseAddress = baseAddress; }
    public int BaseAddress { get; }
    public int ModelId => 103;

    [ModbusRegister(0, ModbusDataType.U16, ScaleFactorProperty = nameof(CurrentScaleFactor), Access = ModbusAccess.ReadOnly)]
    public partial decimal? Current { get; set; }
    [ModbusRegister(4, ModbusDataType.S16, Access = ModbusAccess.ReadOnly)]
    public partial short? CurrentScaleFactor { get; set; }
    // ... remaining value / scale factor pairs, offsets relative to the first data register (after the ID and L header)
}

[InterceptorSubject]
public partial class SunSpecUnit : IModbusUnitIdProvider
{
    public SunSpecUnit(byte unitId) { UnitId = unitId; }
    public byte UnitId { get; }
    public partial Dictionary<int, ISunSpecModel> Models { get; set; }

    [Derived] public SunSpecModel1?   Common   => Models.GetValueOrDefault(1)   as SunSpecModel1;
    [Derived] public SunSpecModel103? Inverter => Models.GetValueOrDefault(103) as SunSpecModel103;
}

[InterceptorSubject]
public partial class SunSpecDevice : BackgroundService, IModbusDiscovery /* + IConnectionState, IMonitoredService, IConfigurable, ... */
{
    [Configuration] public partial string Host { get; set; }
    [Configuration] public partial int Port { get; set; }
    [Configuration] public partial int[] UnitIds { get; set; }   // int[], byte[] would serialize as base64

    public partial Dictionary<byte, SunSpecUnit> Units { get; set; }

    public Task DiscoverAsync(ModbusDiscoveryContext context, CancellationToken cancellationToken);   // chain walk, 4.1
}
```

Values are `decimal?` to match the HomeBlaze capability interfaces (`IPowerSensor`, `IElectricalCurrentSensor`, `IElectricalVoltageSensor`, `IElectricalFrequencySensor`, `ITemperatureSensor`, `IDeviceInfo`), which all use `decimal?`. The exact interface set is decided when the model classes are written. Power and energy must be converted to W and Wh.

## 4. Data flow

### 4.1 Discovery (`SunSpecDevice.DiscoverAsync`)

```
for each unitId in UnitIds:
  read 2 holding registers at 40000 (context.ReadHoldingRegistersAsync(40000, 2, unitId, cancellationToken))
  if not "SunS" (0x5375, 0x6E53): log warning, skip unit
  address = 40002
  models = new Dictionary<int, ISunSpecModel>()
  loop:
    (modelId, length) = read 2 registers at address
    if modelId == 0xFFFF: break
    if SunSpecModelRegistry.TryCreate(modelId, address + 2) is { } model: models[modelId] = model
    else: log information "unknown model", skip
    address += length + 2
    guard: abort the unit with an error if the chain overruns 65535 or exceeds a sanity bound
  units[unitId] = new SunSpecUnit(unitId) { Models = models }

new PropertyReference(this, nameof(Units)).SetValueFromSource(context.Source, null, null, units)
```

The connector builds the read plan after the handler returns, so the attached models are resolved, claimed and read in the initial load. Each reconnect runs the chain walk again and replaces `Units`; in steady state the result is equal, so only the new subject instances are swapped in.

Open: whether to keep existing model instances when the chain is unchanged (avoids detaching and reattaching subjects, keeps UI references stable). Decide during the brainstorm.

### 4.2 Reading

Handled entirely by the connector: `IModbusUnitIdProvider` on `SunSpecUnit` routes each model's registers to the right unit ID, `IModbusBaseAddressProvider` on each model supplies the discovered address, and `ScaleFactorProperty` applies the SunSpec scale factors from the same poll cycle.

## 5. Error handling

| Case | Behavior |
|---|---|
| No `SunS` marker on a unit | Warning, unit skipped, `Units` contains only found units. |
| Malformed chain (overrun, unexpected end) | Error, discovery abandoned for that unit, other units continue. |
| Unknown model ID | Information log, model skipped. |
| SunSpec "not implemented" values (0x8000 int16, 0xFFFF uint16, 0x80000000 int32, 0xFFFFFFFF uint32) | Mapped to `null` by the connector's `NotAvailableValue` (`SignedMinimum`, `UnsignedMaximum`) on each register attribute, ideally through SunSpec preset attributes derived from `ModbusRegisterAttribute`. |

## 6. Testing

- `SunSpecTestServer` on FluentModbus `ModbusTcpServer`: writes the `SunS` marker and a configurable chain (Common + Inverter) at startup, optionally under several unit IDs.
- Discovery: `device.Units[1].Inverter` exists with the correct `BaseAddress`.
- Value flow: known raw values and scale factors produce the expected scaled `decimal?` values.
- A layout fixture matching SolarEdge's published SunSpec implementation for regression coverage until hardware is available.
- Multi-unit: several unit IDs over one connection (verifies FluentModbus unit ID multiplexing).

## 7. Answered questions from the original spec

- Dictionary properties with subject values (`Dictionary<int, ISunSpecModel>`, `Dictionary<byte, SunSpecUnit>`) work with the generator and registry (see `Namotion.Devices.Gpio/GpioSubject.cs` and `HomeBlaze.Storage/VirtualFolder.cs`). The handler builds the dictionaries directly.
- There is no scoped "set source" mechanism in `SubjectChangeContext` (it only scopes timestamps), so discovery uses `SetValueFromSource` explicitly.
- Capability interfaces: see section 3.
- Still open: FluentModbus unit ID multiplexing on one TCP connection (covered by the multi-unit test).

## 8. Future work

- Generator (`tools/Namotion.Devices.SunSpec.Generator/`) consuming the SunSpec Alliance JSON models, emitting checked-in `[InterceptorSubject]` classes and one `[Derived]` accessor per model on `SunSpecUnit`, with a CI check that generated files match the JSON.
- Controls through the connector write stage.
- Other Modbus device libraries reusing the connector (for example Eastron SDM630, Schneider PM5560).

## 9. Subject rules

Same rules as the Luxtronik library (connector spec 5.2): child subjects and register values are partial properties with `internal set`, initialized in the constructor (`Models`, `Units` and every register property), `[State]` with units on values, and constant constructor-set metadata (`BaseAddress`, `ModelId`, `UnitId`) as plain get-only properties.
