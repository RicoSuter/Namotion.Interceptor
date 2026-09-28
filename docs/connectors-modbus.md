# Modbus

The `Namotion.Interceptor.Modbus` package polls Modbus TCP devices into C# objects. Properties are mapped to registers with attributes, and the connector reads them on a fixed interval. This version is a read-only client: local changes are not sent to the device and are replaced by the device value on the next poll.

## Key Features

- Attribute-based mapping of holding registers, input registers, coils and discrete inputs
- U16, S16, U32, S32, F32, String and Boolean values, all four 32-bit word orders
- Static scaling and dynamic scale factors (value = raw * 10^exponent)
- Conversion to integer and floating point types, `decimal`, `bool`, `string`, enums, flags enums and their nullable forms
- "Not available" raw patterns mapped to `null`
- Per-subject base addresses and unit IDs for reusable model classes
- Contiguous read batching with a configurable gap, split until the next connect when the device rejects a request
- Discovery hook for firmware gating and runtime discovery
- Automatic reconnection and diagnostics

## Client Setup

```csharp
[InterceptorSubject]
public partial class HeatMeter
{
    [ModbusRegister(0, ModbusDataType.S16, Space = ModbusAddressSpace.InputRegister, Scale = 0.1)]
    public partial decimal? FlowTemperature { get; set; }

    [ModbusRegister(10, ModbusDataType.U32, Space = ModbusAddressSpace.InputRegister)]
    public partial long? Energy { get; set; }
}

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithLifecycle();

builder.Services.AddSingleton(new HeatMeter(context));
builder.Services.AddModbusSubjectClientSource<HeatMeter>("192.168.1.50");
```

The context needs `WithRegistry()`, because the connector walks the subject tree when it connects, and `WithLifecycle()`, because the source claims the properties it reads. Without lifecycle tracking, resolving or creating the source throws `InvalidOperationException`; without the registry, every connect attempt fails with `InvalidOperationException`. The configuration is validated when the source is resolved or created and throws `ArgumentException` for a value out of range.

Register a source with its own configuration through the `AddModbusSubjectClientSource(subjectSelector, configurationProvider)` overload, and several sources with `AddKeyedModbusSubjectClientSource`, which makes each one resolvable as a keyed `ModbusSubjectClientSource`. Only one unnamed source can be registered.

To create a source for a subject at runtime, for example in a HomeBlaze device, use `subject.CreateModbusClientSource(configuration, logger)`, start it as a hosted service (for example with `AttachHostedServiceAsync`) and dispose it when done.

## Register Mapping

`[ModbusRegister(address, dataType)]` maps one property:

| Setting | Default | Meaning |
|---|---|---|
| `Space` | `HoldingRegister` | `HoldingRegister`, `InputRegister`, `Coil` or `DiscreteInput`. The bit spaces require `Boolean`, and `Boolean` requires a bit space |
| `WordOrder` | `HighWordFirst` | Register and byte order of 32-bit values |
| `Scale` | `1.0` | Static factor, requires a `float`, `double` or `decimal` property |
| `ScaleFactorProperty` | none | Name of an S16 register property on the same subject holding a power-of-ten exponent. Mutually exclusive with `Scale`, and the named property must not be excluded |
| `Length` | 0 | Register count of `String` values, 1 to 125 |
| `NotAvailableValue` | `None` | Raw pattern mapped to `null`: `SignedMaximum` (0x7FFF or 0x7FFFFFFF), `SignedMinimum` (0x8000 or 0x80000000) or `UnsignedMaximum` (0xFFFF or 0xFFFFFFFF) |
| `Access` | `ReadWrite` | Declares writability for a later write stage, not enforced yet |

Addresses are raw protocol addresses, without the `3xxxx`/`4xxxx` documentation prefixes and without the +1 offset some tools use.

Values convert as follows:

- Integer data types convert to any integer type that holds every value of the data type (U16 into `int` but not `short`), to `float`, `double` and `decimal` (unscaled or scaled), to `bool` (non-zero is `true`) and to enums, including flags enums. Undefined enum values pass through.
- Scaled values require a `float`, `double` or `decimal` property. `decimal` properties scale in decimal arithmetic, so a raw 234 with `Scale = 0.1` is exactly `23.4`.
- F32 converts to `float`, `double` or `decimal`. A NaN, an infinity or a value beyond the `decimal` range becomes `null` on a `decimal?` property.
- String reads two ASCII characters per register and trims trailing NUL and space characters.
- `NotAvailableValue` requires a nullable property and an integer data type, and is checked before scaling.
- With `ScaleFactorProperty`, a mapped value is not applied until its scale factor was read once, and is applied again whenever the scale factor changes. A scale factor reading as its own `NotAvailableValue` is unknown, so its dependents are not updated until it is available again.

A subject implementing `IModbusBaseAddressProvider` makes its addresses relative to `BaseAddress`, so one class can describe a repeated block. Base addresses are not inherited by child subjects. `IModbusUnitIdProvider` or `[ModbusUnitId]` sets the unit ID for a subject and its children, the interface taking precedence; otherwise `ModbusClientConfiguration.UnitId` applies. Both are read on every connect, when the connector builds its read plan.

Device libraries can derive from `ModbusRegisterAttribute` to preset values such as `Space` and `NotAvailableValue`. A property carries at most one register attribute, derived ones included.

Invalid mappings (for example `Scale` on an `int` property, or `Length` on a non-string) throw `ModbusConfigurationException` naming the property path when the source connects. The connect attempt fails and is retried after `RetryTime`.

## Discovery

A root subject implementing `IModbusDiscovery` has `DiscoverAsync` called on every connect and reconnect, before the register bindings are resolved. The `ModbusDiscoveryContext` offers raw reads of all four spaces (`ReadHoldingRegistersAsync(address, count, unitId, cancellationToken)` and its siblings, from the configured unit ID unless one is given), `Source` for applying values with `SetValueFromSource`, and `ExcludeProperty` to leave a mapped property unread and unclaimed for this connection. Exclusions are reset on every connect. Excluding a property that another mapping names as its `ScaleFactorProperty` makes every connect fail with `ModbusConfigurationException`.

Await every context call before the next one and before `DiscoverAsync` returns: the context is not thread-safe, and once `DiscoverAsync` returns it throws `ObjectDisposedException` because polling then uses the connection. A rejected read throws `ModbusResponseException` with the Modbus exception code, and the connection stays usable. Throwing from `DiscoverAsync` fails the connect attempt, which is retried after `RetryTime`.

## Configuration

| Property | Default | Description |
|---|---|---|
| `Host` | required | Host name or IP address |
| `Port` | 502 | TCP port, 1 to 65535 |
| `UnitId` | 1 | Default unit ID |
| `PollingInterval` | 2 s | Time between poll cycles |
| `RequestTimeout` | 5 s | Timeout of the TCP connect and of each request; a request timeout counts as a lost connection |
| `RetryTime` | 10 s | Delay before reconnecting after a lost connection or a failed connect attempt |
| `BufferTime` | 8 ms | Change queue buffer time |
| `MaximumRegisterGap` | 0 | Unmapped registers or bits a request may span to merge neighbours, 0 to 124 |

The time spans must be positive (`BufferTime` may be zero) and at most `int.MaxValue` milliseconds.

## Batching and Polling

Mappings are grouped by unit ID and space, sorted by address and merged into requests of at most 125 registers or 2000 bits. With the default gap of 0 only contiguous mappings are merged, because many devices reject reads that touch unmapped addresses. Each cycle reads all requests first and then applies only values whose raw registers changed, so an unchanged cycle converts nothing and raises no change events. All values of a cycle share one timestamp, since Modbus carries none.

The initial load reads every mapping once before the source reports `Synchronized`, so the model holds device values from then on.

## Local Writes

Mapped properties are owned by the source, so local changes reach it but are not sent to the device. The source logs a warning once per property and connection and applies the device value again on the next poll, even when it did not change, so the model converges back to the device state.

## Resilience

- A request the device rejects as unsupported (exception code 1 illegal function, 2 illegal data address or 3 illegal data value) is re-read one mapping at a time in the same cycle. Its mappings keep being read one at a time until the next connect, so a rejected gap register or a device that rejects reads across block boundaries costs one failed request per connect, not one per cycle. Mappings that still fail alone with one of these codes are logged once, reported in `Diagnostics.Polling.UnavailableProperties` and skipped until the next connect.
- A request answered with any other exception code (for example 4 server failure, 6 server busy or 10 and 11 gateway errors) is transient: it is skipped for that cycle, its mappings keep their last value and the read plan stays unchanged. It is logged once, counted in `Diagnostics.Polling.FailedBatches` and logged again when it succeeds.
- A mapping that already has a request of its own and is rejected, with any code, is logged once, read again every cycle and logged again when it succeeds.
- A value that fails to convert (for example a scale factor exponent outside the `decimal` range) is logged and the property keeps its value.
- An I/O error, a timeout or a malformed response closes the connection. The source reports `Synchronizing`, reconnects every `RetryTime`, runs discovery again, reloads all values and reports `Synchronized`.
- Some devices do not answer reads of unmapped registers at all. The request then times out and the connection is treated as lost, so keep such reads out of the plan (gap 0, `ExcludeProperty`).

## Diagnostics

`ModbusSubjectClientSource.Diagnostics` is a `ModbusClientDiagnostics`, which extends the shared model described in [Connector Diagnostics](connectors.md#connector-diagnostics). `IsOperational` is set once the connection is open and discovery ran, and drops when the connection is lost until the reconnect and the reload have both succeeded. The claimed property count is measured, throughput is not.

`Polling` adds:

| Member | Meaning |
|---|---|
| `TotalPolls` | Completed poll cycles |
| `FailedBatches` | Planned read requests answered with a Modbus exception response; one-by-one re-reads and discovery reads are not counted |
| `BatchCount` | Read requests per poll cycle |
| `UnavailableProperties` | Mappings the device rejected, not read until the next connect |
| `LastPollDuration` | Duration of the last poll cycle, `null` before the first one |
| `LastPollTime` | When the last poll cycle completed, `null` before the first one |

## Thread Safety

One connection per source, used by one request at a time: discovery, the initial load and the poll loop run strictly one after another. Values are applied through the source's property writer, like every other connector.

## Lifecycle

Properties of subjects attached after the source connected are picked up on the next reconnect. Detached subjects release the ownership of their properties automatically, but stay in the read plan until the next connect. A property already owned by another source is logged and not read.

## Limitations

- No writes (function codes 5, 6, 15 and 16) yet; the connector only issues function codes 1 to 4.
- No Modbus RTU and no Modbus server.

## References

- [Modbus Application Protocol Specification V1.1b3](https://modbus.org/docs/Modbus_Application_Protocol_V1_1b3.pdf)
- [Modbus Messaging on TCP/IP Implementation Guide V1.0b](https://modbus.org/docs/Modbus_Messaging_Implementation_Guide_V1_0b.pdf)
- [FluentModbus](https://github.com/Apollo3zehn/FluentModbus)
