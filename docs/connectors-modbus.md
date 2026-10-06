# Modbus

The `Namotion.Interceptor.Modbus` package polls Modbus TCP devices into C# objects. Properties are mapped to registers with attributes, and the connector reads them on a fixed interval. This version is a read-only client: local changes are not sent to the device and are replaced by the device value on the next poll.

Dependencies: [FluentModbus](https://github.com/Apollo3zehn/FluentModbus) (MIT)

## Key Features

- Attribute-based mapping of holding registers, input registers, coils and discrete inputs
- U16, S16, U32, S32, U64, S64, F32, String and Boolean values, all four word orders
- Static scaling and dynamic scale factors, also combined (value = raw * scale * 10^exponent)
- Conversion to integer and floating point types, `decimal`, `bool`, `string`, enums, flags enums and their nullable forms
- "Not available" raw patterns mapped to `null`
- Per-subject base addresses, unit IDs and scale factor providers for reusable model classes
- Contiguous read batching with a configurable gap, split until the next connect when the device rejects a request
- Discovery hook for firmware gating and runtime discovery
- Automatic reconnection and diagnostics

## Client Setup

```csharp
var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry();

builder.Services.AddSingleton(new HeatMeter(context));
builder.Services.AddModbusSubjectClientSource<HeatMeter>("192.168.1.50");

[InterceptorSubject]
public partial class HeatMeter
{
    [ModbusRegister(0, ModbusDataType.S16, AddressSpace = ModbusAddressSpace.InputRegister, Scale = 0.1)]
    public partial decimal? FlowTemperature { get; set; }

    [ModbusRegister(10, ModbusDataType.U32, AddressSpace = ModbusAddressSpace.InputRegister)]
    public partial long? Energy { get; set; }
}
```

`ModbusRegister` lives in `Namotion.Interceptor.Modbus.Attributes`, the data types and the address and unit ID providers in `Namotion.Interceptor.Modbus`, and the client types (`ModbusClientConfiguration`, `ModbusSubjectClientSource`, `IModbusDiscovery`, `ModbusDiscoveryContext`) in `Namotion.Interceptor.Modbus.Client`. The registration extensions are in `Microsoft.Extensions.DependencyInjection`.

The context needs `WithRegistry()`, because the connector walks the subject tree when it connects, and lifecycle tracking (added by `WithFullPropertyTracking()` or `WithRegistry()`), because the source claims the properties it reads. Without lifecycle tracking, resolving or creating the source throws `InvalidOperationException`; without the registry, every connect attempt fails with `InvalidOperationException`. The configuration is validated when the source is resolved or created and throws `ArgumentException` for a value out of range.

Register a source with its own configuration through the `AddModbusSubjectClientSource(subjectSelector, configurationProvider)` overload, and several sources with `AddKeyedModbusSubjectClientSource`, which makes each one resolvable as a keyed `ModbusSubjectClientSource`. Only one unnamed source can be registered.

To create a source for a subject at runtime, for example in a device subject that owns its connection, use `subject.CreateModbusClientSource(configuration, logger)`, start it as a hosted service (for example with `AttachHostedServiceAsync`) and dispose it when done.

## Register Mapping

`[ModbusRegister(address, dataType)]` maps one property:

| Setting | Default | Meaning |
|---|---|---|
| `AddressSpace` | `HoldingRegister` | `HoldingRegister`, `InputRegister`, `Coil` or `DiscreteInput`. The bit spaces require `Boolean`, and `Boolean` requires a bit space |
| `WordOrder` | `HighWordFirst` | Register and byte order of 32-bit and 64-bit values |
| `Scale` | `1.0` | Static factor, requires a `float`, `double` or `decimal` property |
| `ScaleFactorProperty` | none | Name of an S16 register property on the same subject holding a power-of-ten exponent. Combines with `Scale` (value = raw * scale * 10^exponent), and the named property must not be excluded |
| `Length` | 0 | Register count of `String` values, 1 to 125 |
| `NotAvailableValue` | `None` | Raw pattern mapped to `null`: `SignedMaximum` (0x7FFF, 0x7FFFFFFF or 0x7FFFFFFFFFFFFFFF), `SignedMinimum` (0x8000, 0x80000000 or 0x8000000000000000) or `UnsignedMaximum` (0xFFFF, 0xFFFFFFFF or 0xFFFFFFFFFFFFFFFF) |
| `Access` | `ReadWrite` | Declares writability for a later write stage, not enforced yet |

Addresses are raw protocol addresses, without the `3xxxx`/`4xxxx` documentation prefixes and without the +1 offset some tools use.

Values convert as follows:

- Integer data types convert to any integer type that holds every value of the data type (U16 into `int` but not `short`, U64 into `ulong` but not `long`), to `float`, `double` and `decimal` (unscaled or scaled), to `bool` (non-zero is `true`) and to enums whose underlying type holds every value, including flags enums. Undefined enum values pass through.
- Scaled values require a `float`, `double` or `decimal` property. `decimal` properties scale in decimal arithmetic, so a raw 234 with `Scale = 0.1` is exactly `23.4`.
- F32 converts to `float`, `double` or `decimal`. A NaN, an infinity or a value beyond the `decimal` range becomes `null` on a `decimal?` property.
- String reads two ASCII characters per register up to the first NUL and trims trailing spaces.
- `NotAvailableValue` requires a nullable property and an integer data type, and is checked before scaling.
- With a dynamic scale factor, a mapped value is not applied until its scale factor was read once, and is applied again whenever the scale factor changes. A scale factor reading as its own `NotAvailableValue` is unknown, so its dependents are not updated until it is available again.
- `Scale` combined with a scale factor multiplies both, so a percentage reported as 0 to 100 with an exponent can be converted to a fraction with `Scale = 0.01`.

A subject implementing `IModbusBaseAddressProvider` makes its addresses relative to `BaseAddress`, so one class can describe a repeated block. Base addresses are not inherited by child subjects. `IModbusUnitIdProvider` sets the unit ID for a subject and its children, the nearest one taking precedence; otherwise `ModbusClientConfiguration.UnitId` applies. Base addresses and unit IDs are read on every connect, when the connector builds its read plan.

A subject whose scale factors live on another subject, such as a repeated block sharing the scale factors of its enclosing block, implements `IModbusScaleFactorProvider`. Its `TryGetScaleFactorProperty(propertyName)` returns the S16 property holding the exponent of that mapping, or `null`. It is asked once per mapping on every connect. A mapping takes its scale factor from either the provider or `ScaleFactorProperty`, never both; the value is still converted with the scale factor of the same poll cycle.

Device libraries can derive from `ModbusRegisterAttribute` to preset values such as `AddressSpace` and `NotAvailableValue`. A property carries at most one register attribute, derived ones included. Mappings may overlap, so one register can back several properties, for example a status word read both as an enum and as a flag.

Invalid mappings (for example `Scale` on an `int` property, or `Length` on a non-string) throw `ModbusConfigurationException` naming the property path when the source connects. The connect attempt fails and is retried after `RetryTime`.

## Discovery

A root subject implementing `IModbusDiscovery` has `DiscoverAsync` called on every connect and reconnect, before the register bindings are resolved. The `ModbusDiscoveryContext` offers raw reads of all four spaces (`ReadHoldingRegistersAsync(address, count, unitId, cancellationToken)` and its siblings, from the configured unit ID unless one is given), `Source` for applying values with `SetValueFromSource`, and `ExcludeProperty` to leave a mapped property unread and unclaimed for this connection. Exclusions are reset on every connect. Exclude registers a device does not support instead of relying on its exception responses: devices differ, and a request that times out counts as a lost connection. Excluding a property does not clear its value, so a property excluded after it was read keeps its last reading unless discovery sets it to `null` with `SetValueFromSource`. Excluding a property that another mapping uses as its scale factor, through `ScaleFactorProperty` or `IModbusScaleFactorProvider`, makes every connect fail with `ModbusConfigurationException`. Discovery may also create, replace or clear child subjects: the bindings are resolved from the subject tree after `DiscoverAsync` returns, so the registers of a new child are read and the properties of a removed child are released.

Await every context call before the next one and before `DiscoverAsync` returns: the context is not thread-safe (a read started while another is in flight throws `InvalidOperationException`), and once `DiscoverAsync` returns it throws `ObjectDisposedException` because polling then uses the connection. A rejected read throws `ModbusResponseException` with the Modbus exception code, and the connection stays usable. Its `IsPermanentRejection` is true for codes 1 to 3 (illegal function, data address or data value), meaning the device does not support the request; other codes, such as 6 (server busy), may pass on a later try. Throwing from `DiscoverAsync` fails the connect attempt, which is retried after `RetryTime`.

## Building a Device Library

A device library is a set of subject classes for one device family, with the connector underneath.

1. Model the device as subjects grouped by what a user looks for (functions, not register blocks), with plain properties for values and child subjects for components such as sensors. Keep the setters of mapped properties non-public (`internal set`): the connector is read-only, so a local write would only be reverted.
2. Derive a register attribute that presets what every register of the device shares, such as `AddressSpace` and `NotAvailableValue` (see Register Mapping).
3. Give a subject that repeats at several addresses an `IModbusBaseAddressProvider`, and use absolute addresses everywhere else.
4. Implement `IModbusDiscovery` on the root to read version and capability registers on every connect, exclude what the device does not provide rather than probing it (clearing values that an earlier connection read), and create or clear the child subjects of optional parts.
5. Own the source in the device: create it with `CreateModbusClientSource`, restart it when the configuration or a capability read during polling changes, and map `Diagnostics` to the device status.
6. Test against an in-process FluentModbus `ModbusTcpServer` whose `RequestValidator` rejects unmapped addresses like the real device.

```csharp
[InterceptorSubject]
public partial class Inverter : IModbusDiscovery
{
    public Inverter()
    {
        StringInput1 = new StringInput(baseAddress: 300);
        StringInput2 = new StringInput(baseAddress: 320);
    }

    [ModbusRegister(100, ModbusDataType.S32, AddressSpace = ModbusAddressSpace.InputRegister, NotAvailableValue = ModbusNotAvailableValue.SignedMinimum)]
    public partial decimal? Power { get; internal set; }

    public partial StringInput StringInput1 { get; internal set; }

    public partial StringInput StringInput2 { get; internal set; }

    public partial Battery? Battery { get; internal set; }

    public async Task DiscoverAsync(ModbusDiscoveryContext context, CancellationToken cancellationToken)
    {
        var batteryRegisters = await context.ReadInputRegistersAsync(200, 1, cancellationToken: cancellationToken);
        Battery = batteryRegisters[0] != 0 ? Battery ?? new Battery() : null;
    }
}

// One class for both inputs: register 0 reads 300 for the first input and 320 for the second.
[InterceptorSubject]
public partial class StringInput : IModbusBaseAddressProvider
{
    public StringInput(int baseAddress)
    {
        BaseAddress = baseAddress;
    }

    public int BaseAddress { get; }

    [ModbusRegister(0, ModbusDataType.U16, AddressSpace = ModbusAddressSpace.InputRegister, Scale = 0.1)]
    public partial decimal? Voltage { get; internal set; }

    [ModbusRegister(1, ModbusDataType.U16, AddressSpace = ModbusAddressSpace.InputRegister, Scale = 0.01)]
    public partial decimal? Current { get; internal set; }
}

[InterceptorSubject]
public partial class Battery
{
    [ModbusRegister(201, ModbusDataType.U16, AddressSpace = ModbusAddressSpace.InputRegister, Scale = 0.1)]
    public partial decimal? StateOfCharge { get; internal set; }
}
```

## Configuration

| Property | Default | Description |
|---|---|---|
| `Host` | required | Host name or IP address |
| `Port` | 502 | TCP port, 1 to 65535 |
| `UnitId` | 1 | Default unit ID |
| `PollingInterval` | 2 s | Interval at which poll cycles start |
| `RequestTimeout` | 5 s | Timeout of the TCP connect and of each request; a request timeout counts as a lost connection |
| `RetryTime` | 10 s | Delay before reconnecting after a lost connection or a failed connect attempt |
| `BufferTime` | 8 ms | Change queue buffer time |
| `MaximumRegisterGap` | 0 | Unmapped registers or bits a request may span to merge neighbours, 0 to 124 |

The time spans must be positive (`BufferTime` may be zero) and at most 1 hour.

## Batching and Polling

Mappings are grouped by unit ID and space, sorted by address and merged into requests of at most 125 registers or 2000 bits. With the default gap of 0 only contiguous mappings are merged, because many devices reject reads that touch unmapped addresses. Each cycle reads all requests first and then applies only values whose raw registers changed, so an unchanged cycle converts nothing and raises no change events. All values of a cycle share one timestamp, since Modbus carries none.

The initial load reads every mapping once before the source reports `Synchronized`. A mapping the device rejects, or whose request fails transiently during that load, keeps its previous value until it is read; `Synchronized` does not mean every mapping holds a current device value.

## Local Writes

Mapped properties are owned by the source, so local changes reach it but are not sent to the device. The source logs a warning once per property and connection and applies the device value again the next time the mapping is read, even when it did not change, so the model converges back to the device state. A mapping that is not read (marked unavailable, its request failing, or its scale factor unknown) keeps the local value until it is read again. A local write, including one made in a transaction, is reported to the change queue and to transactions as written, although nothing is sent.

## Resilience

- A request the device rejects as unsupported (exception code 1 illegal function, 2 illegal data address or 3 illegal data value) is re-read one mapping at a time in the same cycle when it spans several mappings. Its mappings keep being read one at a time until the next connect, so a rejected gap register or a device that rejects reads across block boundaries costs one failed request per connect, not one per cycle. A mapping that fails alone with one of these codes, in such a re-read or in a request of its own, is logged once, reported in `Diagnostics.Polling.UnavailablePropertyCount` and skipped until the next connect. A mapping that fails alone with any other code stays isolated but is not marked unavailable.
- A request answered with any other exception code (for example 4 server failure, 6 server busy or 10 and 11 gateway errors) is transient: it is skipped for that cycle, its mappings keep their last value and the read plan stays unchanged. It is logged once, counted in `Diagnostics.Polling.TotalFailedRequests` and logged again when it succeeds.
- A value that fails to convert (for example a scale factor exponent outside the `decimal` range) is logged and the property keeps its value.
- An I/O error, a timeout or a malformed response closes the connection. The source reports `Synchronizing`, reconnects every `RetryTime`, runs discovery again, reloads all values and reports `Synchronized`.
- Some devices do not answer reads of unmapped registers at all. The request then times out and the connection is treated as lost, so keep such reads out of the plan (gap 0, `ExcludeProperty`).

## Diagnostics

`ModbusSubjectClientSource.Diagnostics` is a `ModbusClientDiagnostics`, which extends the shared model described in [Connector Diagnostics](connectors.md#connector-diagnostics). `IsOperational` is `null` until the first connect has opened the connection, run discovery and resolved the mappings, and `true` from then on. It drops to `false` when the connection is lost, until a reconnect has done the same again. In both cases it is set before the values are loaded, which `State` reports by reaching `Synchronized`. The claimed property count is measured, throughput is not.

`Polling` adds:

| Member | Meaning |
|---|---|
| `TotalPolls` | Completed poll cycles, including the initial load of every connect |
| `TotalFailedRequests` | Planned read requests answered with a Modbus exception response; one-by-one re-reads and discovery reads are not counted |
| `BatchCount` | Read requests per poll cycle |
| `UnavailablePropertyCount` | Mappings the device rejected, not read until the next connect |
| `LastPollDuration` | Duration of the last poll cycle or initial load, `null` before the first one |
| `LastPollTime` | Time of the last poll cycle that read a value, `null` before any did. A cycle that read no value, for example because every request failed or no mapping is claimed, leaves it unchanged |

## Thread Safety

One connection per source, used by one request at a time: discovery, the initial load and the poll loop run strictly one after another. Values are applied through the source's property writer, like every other connector.

## Lifecycle

Properties of subjects attached after the source connected are picked up on the next reconnect. Detached subjects release the ownership of their properties automatically, but stay in the read plan until the next connect. A property already owned by another source is logged and not read, and neither are the properties that use it as their scale factor.

## Limitations

- No writes (function codes 5, 6, 15 and 16) yet; the connector only issues function codes 1 to 4.
- No Modbus RTU and no Modbus server.
- No [structural changes](connectors.md#structural-changes) during a session: the read plan follows the subject graph as it was at the last connect. A device library changes its structure in discovery and restarts the source when the device's configuration changes.

## References

- [Modbus Application Protocol Specification V1.1b3](https://www.modbus.org/file/secure/modbusprotocolspecification.pdf)
- [Modbus Messaging on TCP/IP Implementation Guide V1.0b](https://www.modbus.org/file/secure/messagingimplementationguide.pdf)
- [FluentModbus](https://github.com/Apollo3zehn/FluentModbus)
