# Modbus connector and Luxtronik SHI design

Date: 2026-09-28
Status: Part A implemented (sections 3 and 4 describe the implementation), Part B design
Replaces: `2026-05-04-modbus-design.md` (the SunSpec part moved to [2026-09-28-sunspec-design.md](2026-09-28-sunspec-design.md))

## 1. Goals and scope

Two deliverables in one plan, connector first:

- **Part A: `Namotion.Interceptor.Modbus`**, a generic Modbus TCP client connector with a complete read side. Models are plain `[InterceptorSubject]` classes with `[ModbusRegister]` attributes; the connector attaches to them, polls, and applies values. Models contain no Modbus code.
- **Part B: `Namotion.Devices.Luxtronik`** (+ `.HomeBlaze` UI, + `.Tests`), a strongly typed, read-only model of the Luxtronik 2.1 Smart Home Interface (SHI, Modbus TCP port 502) and the first consumer of the connector. Target device: Alpha Innotec SWCV 92H3 with Luxtronik 2.1 firmware 3.92.3.

### In scope

- Reads of holding registers (FC3), input registers (FC4), coils (FC1) and discrete inputs (FC2)
- Data types U16, S16, U32, S32, F32, String (multi-register ASCII), Boolean (coils and discrete inputs)
- All four word orders for multi-register values
- Static scale (`Scale`) and dynamic scale factors (`ScaleFactorProperty`, SunSpec style `value * 10^sf`)
- Conversion to any numeric CLR type, `decimal`, nullable types, enums (including flags enums), `bool` and `string`
- Unit ID per subject (`[ModbusUnitId]`, `IModbusUnitIdProvider`) with configuration fallback
- Per-subject base addresses (`IModbusBaseAddressProvider`)
- Batching with a configurable maximum gap and PDU limits, plus split-on-failure fallback
- Discovery (`IModbusDiscovery`) with raw reads and `ExcludeProperty`
- Not-available values (device-specific raw patterns such as 0x7FFF) mapped to `null`
- `Access` declared on the attribute (enforced by the write stage)
- Reconnect, diagnostics, DI and imperative wire-up following the OPC UA and MQTT connectors
- Luxtronik SHI model with firmware gating, HomeBlaze device and UI
- HomeBlaze abstractions on the Luxtronik model: `ITemperatureSensor` child subjects for measured temperatures, `IPowerSensor`, and a new `IThermalPowerSensor` in `HomeBlaze.Abstractions`
- New `StateUnit` members `Kelvin`, `Minute` and `Hour` in `HomeBlaze.Abstractions`, with display formatting
- Documentation: `docs/connectors-modbus.md` and the HomeBlaze Luxtronik device doc, both with a References section linking the original sources

### Out of scope

- Writes (FC5, FC6, FC15, FC16), write validation (minimum/maximum) and the stale-control reconnect policy. This is the next stage.
- A Modbus server connector and Connector Tester support. Planned together with writes.
- Modbus RTU (serial).
- Bit fields inside registers (flags enums cover the Luxtronik status register).
- Luxtronik data that SHI does not expose (flow rate, heat pump type) and the proprietary CFI protocol (port 8889).
- Registers that neither the official AIT manual nor python-luxtronik names (for example input 10414).
- Rebuilding the read plan when subjects are attached after discovery ran (picked up on the next reconnect).

## 2. Architecture

```
Namotion.Devices.Luxtronik.HomeBlaze   Blazor widget and edit components    net10.0
            |
Namotion.Devices.Luxtronik             plain models + HomeBlaze device       net10.0
            |
Namotion.Interceptor.Modbus            generic connector                     net9.0
            |
FluentModbus (internal only)  ->  Modbus TCP
```

- `Namotion.Interceptor.Modbus` is the only module that talks Modbus. FluentModbus is an implementation detail: no FluentModbus type appears in the public API, so the wire library can be replaced without breaking device libraries. FluentModbus was chosen over NModbus for `CancellationToken` support, a reused response buffer and a built-in `ModbusTcpServer` for tests.
- `Namotion.Devices.Luxtronik` is the only module that knows Luxtronik. It depends on the connector and `HomeBlaze.Abstractions`.
- `Namotion.Devices.Luxtronik.HomeBlaze` owns UI only.

Project locations:

- `src/Namotion.Interceptor.Modbus/`, `src/Namotion.Interceptor.Modbus.Tests/` (in the `/Connectors/` and `/Tests/` solution folders)
- `src/HomeBlaze/Namotion.Devices.Luxtronik/`, `src/HomeBlaze/Namotion.Devices.Luxtronik.HomeBlaze/`, `src/HomeBlaze/Namotion.Devices.Luxtronik.Tests/`

## 3. Part A: connector public surface

Namespaces: attributes in `Namotion.Interceptor.Modbus.Attributes`, everything else in `Namotion.Interceptor.Modbus`, DI extensions in `Microsoft.Extensions.DependencyInjection`.

```csharp
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public class ModbusRegisterAttribute(int address, ModbusDataType dataType) : Attribute   // not sealed: device libraries derive presets
{
    public int Address { get; } = address;                  // relative to the subject's BaseAddress
    public ModbusDataType DataType { get; } = dataType;
    public ModbusAddressSpace Space { get; init; } = ModbusAddressSpace.HoldingRegister;
    public ModbusWordOrder WordOrder { get; init; } = ModbusWordOrder.HighWordFirst;
    public double Scale { get; init; } = 1.0;                // static scale
    public string? ScaleFactorProperty { get; init; }        // dynamic scale: value * 10^sf
    public int Length { get; init; }                         // String only: register count
    public ModbusAccess Access { get; init; } = ModbusAccess.ReadWrite;
    public ModbusNotAvailableValue NotAvailableValue { get; init; } = ModbusNotAvailableValue.None;
}

public enum ModbusDataType          { Boolean, U16, S16, U32, S32, F32, String }
public enum ModbusAddressSpace      { HoldingRegister, InputRegister, Coil, DiscreteInput }
public enum ModbusWordOrder         { HighWordFirst, LowWordFirst, HighWordFirstByteSwapped, LowWordFirstByteSwapped }
public enum ModbusAccess            { ReadWrite, ReadOnly }
public enum ModbusNotAvailableValue { None, SignedMaximum, SignedMinimum, UnsignedMaximum }
// raw bit pattern meaning "not available", mapped to null:
//   SignedMaximum   0x7FFF / 0x7FFFFFFF  (Luxtronik)
//   SignedMinimum   0x8000 / 0x80000000  (SunSpec int16/int32)
//   UnsignedMaximum 0xFFFF / 0xFFFFFFFF  (SunSpec uint16/uint32)

[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class ModbusUnitIdAttribute(byte unitId) : Attribute { public byte UnitId { get; } = unitId; }

public interface IModbusUnitIdProvider      { byte UnitId { get; } }
public interface IModbusBaseAddressProvider { int BaseAddress { get; } }

// Implemented by the root subject; runs on every (re)connect before bindings are resolved.
public interface IModbusDiscovery
{
    Task DiscoverAsync(ModbusDiscoveryContext context, CancellationToken cancellationToken);
}

public sealed class ModbusDiscoveryContext   // invalid once DiscoverAsync returns
{
    public ISubjectSource Source { get; }
    public Task<ushort[]> ReadHoldingRegistersAsync(int address, int count, byte? unitId = null, CancellationToken cancellationToken = default);
    public Task<ushort[]> ReadInputRegistersAsync(int address, int count, byte? unitId = null, CancellationToken cancellationToken = default);
    public Task<bool[]> ReadCoilsAsync(int address, int count, byte? unitId = null, CancellationToken cancellationToken = default);
    public Task<bool[]> ReadDiscreteInputsAsync(int address, int count, byte? unitId = null, CancellationToken cancellationToken = default);
    public void ExcludeProperty(PropertyReference property);
}

public sealed class ModbusClientConfiguration
{
    public required string Host { get; init; }
    public int Port { get; init; } = 502;
    public byte UnitId { get; init; } = 1;
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RetryTime { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan BufferTime { get; init; } = TimeSpan.FromMilliseconds(8);
    public int MaximumRegisterGap { get; init; } = 0;
    public void Validate();   // ArgumentException: empty Host, Port outside 1..65535, non-positive time span
                              // (BufferTime may be 0), time span above int.MaxValue ms, gap outside 0..124
}

public sealed class ModbusConfigurationException(string message) : Exception;   // invalid mapping, message names the property path

public sealed class ModbusResponseException : Exception   // Modbus exception response (e.g. 2, illegal data address); connection stays usable
{
    internal ModbusResponseException(int exceptionCode, string message, Exception innerException);
    public int ExceptionCode { get; }
}

public sealed class ModbusSubjectClientSource : SubjectSourceBase, IFaultInjectable, IAsyncDisposable
{
    internal ModbusSubjectClientSource(...);
    public override ModbusClientDiagnostics Diagnostics { get; }
}

public sealed class ModbusClientDiagnostics : SourceDiagnostics
{
    public ModbusPollingDiagnostics Polling { get; }
}

public sealed class ModbusPollingDiagnostics
{
    public long TotalPolls { get; }
    public long FailedBatches { get; }             // requests answered with a Modbus exception response
    public int BatchCount { get; }                 // read requests per poll cycle
    public int UnavailableProperties { get; }
    public TimeSpan? LastPollDuration { get; }
    public DateTimeOffset? LastPollTime { get; }
}

// namespace Microsoft.Extensions.DependencyInjection
public static class ModbusSubjectExtensions
{
    public static ModbusSubjectClientSource CreateModbusClientSource(
        this IInterceptorSubject subject, ModbusClientConfiguration configuration, ILogger logger);

    public static IServiceCollection AddModbusSubjectClientSource<TSubject>(
        this IServiceCollection services, string host, int port = 502) where TSubject : IInterceptorSubject;

    public static IServiceCollection AddModbusSubjectClientSource(
        this IServiceCollection services,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, ModbusClientConfiguration> configurationProvider);

    public static IServiceCollection AddKeyedModbusSubjectClientSource(
        this IServiceCollection services, string name,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, ModbusClientConfiguration> configurationProvider);
}
```

Notes:

- There is no `IModbusSubjectClientSource` interface. The source is a public sealed class with an internal constructor, created only through `CreateModbusClientSource` and the DI extensions. A HomeBlaze device needs to attach it as a hosted service, dispose it and read its diagnostics, which the class covers.
- DI follows `OpcUaSubjectExtensions`: every overload registers keyed configuration, subject and source singletons under an internal Guid key plus `AddSingleton<IHostedService>` resolving the keyed source; the unkeyed overloads add an unkeyed alias (at most one, else `InvalidOperationException`) and `AddKeyedModbusSubjectClientSource` a keyed alias under the given name (unique, else `InvalidOperationException`). Arguments are null-checked before the duplicate guard. The configuration provider is invoked once, and the configuration is validated and lifecycle tracking required (`WithLifecycle()`, else `InvalidOperationException`) when the source is resolved.
- `SourceDiagnostics` already provides operational state, last error, throughput, retry queue and inbound buffer. The claimed property count is only populated when the source calls `Metrics.RegisterClaimedProperties(() => ownership.Count)`, which it does in its constructor. `ModbusClientDiagnostics` adds only the polling counters, kept in an internal metrics class using `Interlocked` and registered via `Metrics.RegisterResettable`.

## 4. Part A: connector behavior

### 4.1 Address and unit ID resolution

- Absolute address = `BaseAddress` of the subject declaring the property (0 if it does not implement `IModbusBaseAddressProvider`) + `ModbusRegisterAttribute.Address`. Base addresses are not inherited from parents.
- Unit ID = first match of: the declaring subject's `IModbusUnitIdProvider`, its `[ModbusUnitId]`, the same two checks on the nearest ancestor, then `ModbusClientConfiguration.UnitId`. A subject reachable through several paths is resolved once, through the first path walked.
- Base addresses and unit IDs are read on every connect when the read plan is built, so a change takes effect on the next reconnect.
- Register count per data type: Boolean 1 bit, U16/S16 1, U32/S32/F32 2, String `Length`.

### 4.2 Validation

Resolution throws `ModbusConfigurationException` naming the property path when:

- a property carries more than one `ModbusRegisterAttribute` (a derived preset next to the base attribute counts)
- an enum value of the attribute (`DataType`, `Space`, `WordOrder`, `NotAvailableValue`) is not defined
- `Scale` and `ScaleFactorProperty` are both set, `Scale` is zero or not finite (or outside the `decimal` range for a `decimal` target), or `ScaleFactorProperty` does not name an S16 register property on the same subject that is resolved (not excluded). S16 only, because an exponent is signed and a U16 read would turn -1 into 65535
- `Length` is set on a non-string type, or outside 1..125 on String
- `Boolean` is used in a register space, or a non-Boolean type in `Coil`/`DiscreteInput`
- a scaled value targets a CLR type other than `float`, `double` or `decimal`, F32 targets any other type, or an integral or enum CLR type cannot hold every value of the data type (for example U16 into `short`, or String into `int`)
- the attribute address is negative, or the absolute address range is outside 0..65535
- `NotAvailableValue` is set on a non-nullable CLR type, or on `Boolean`, `F32` or `String`

A root subject whose context has no registry fails the connect attempt with `InvalidOperationException`.

### 4.3 Value conversion

At resolution time each property gets a converter delegate chosen by a switch on (data type, CLR type), so the poll loop does no reflection. Register attributes are read from `RegisteredSubjectProperty.ReflectionAttributes` (precomputed metadata), not via `GetCustomAttribute`; only the class-level `[ModbusUnitId]` is read with `GetCustomAttribute`, once per subject and connect. This keeps the connector AOT friendly.

- Numeric targets: `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`, and their nullable forms. Integral targets must hold every value of the data type (4.2).
- F32: converts to `float`, `double` or `decimal`. On a `decimal?` target, NaN, infinities and magnitudes beyond the `decimal` range become `null`; on a non-nullable `decimal` the conversion fails (see below).
- A conversion that throws (for example a dynamic exponent outside ±28 for a `decimal` target) is logged as a warning and the property keeps its value; the raw words are still remembered, so it is retried only when they change.
- Scaling: `decimal` targets scale in decimal arithmetic (`Scale` converted once via `(decimal)double`), so `234 * 0.1` is exactly `23.4`. `float`/`double` targets scale in double.
- Dynamic scale factors: `value = raw * 10^sf` where `sf` is the current raw value of the S16 scale-factor property. If the scale factor was never successfully read, or its raw value matches its own `NotAvailableValue`, it is unknown and the scaled property is not updated in that cycle; otherwise the last successfully read scale factor is used. A changed scale factor reapplies its dependents even when their raw words did not change.
- Enums: the raw integer is converted with `Enum.ToObject` (AOT safe). Flags enums pass through unchanged. Undefined enum values are passed through, not rejected.
- `bool`: non-zero is `true`. Works for coils, discrete inputs and unscaled integer registers.
- `string`: ASCII, trailing `0x00` and `0x20` trimmed on the byte span before decoding (one allocation).
- Not-available values: when `NotAvailableValue` is set and the raw register words match its bit pattern for the data type's width, the property is set to `null` instead of converted. Requires a nullable CLR type (validated in 4.2). This is checked before scaling.

### 4.4 Discovery and read plan

On every connect (first connect and each reconnect):

1. Connect a new `TcpClient`, bounded by `RequestTimeout`, and wrap it in a `ModbusTcpClient`.
2. If the root subject implements `IModbusDiscovery`, call `DiscoverAsync` with a fresh `ModbusDiscoveryContext`. Discovery can read raw registers of all four spaces (1 to 125 registers or 1 to 2000 bits per call, default unit ID is `configuration.UnitId`) and call `ExcludeProperty`. The context is not thread-safe. After `DiscoverAsync` returns, the context is invalidated and its reads and `ExcludeProperty` throw `ObjectDisposedException`.
3. Walk the root subtree through the registry (each `RegisteredSubject.Properties` and their children, each subject once), resolve every `[ModbusRegister]` property that is not excluded, and claim it with `SourceOwnershipManager` (requires `WithLifecycle()`). A property owned by another source is logged as an error and not read. Previously claimed properties that are now excluded or detached are released. A stop during the open cancels before claiming, so a disposed source never claims again.
4. Build the read plan (4.5) and cache it until the next connect.

Any failure disposes the new connection and fails the connect attempt.

Exclusions are reset on every connect, so discovery decides again after each reconnect.

### 4.5 Batching

- Group resolved properties by (unit ID, address space), sort by address.
- Merge neighbours while the gap between them is at most `MaximumRegisterGap` (default 0, strictly contiguous, because many devices reject reads that cover unmapped registers) and the request stays within the PDU limit (125 registers for FC3/FC4, 2000 bits for FC1/FC2).
- Each cycle reads all batches before converting any value, so a dynamic scale factor from any batch of the same cycle is current when its dependents are converted.
- After a multi-property batch is rejected with exception code 1, 2 or 3 (4.9), its properties are isolated until the next connect: the plan is rebuilt and never merges them again. Otherwise a batch whose properties all read fine alone (a rejected gap register, or a device rejecting reads across block boundaries) would cost a failed request plus one request per property every cycle.

### 4.6 Poll loop

- Runs every `PollingInterval` on a single task started from `StartListeningAsync` via `BackgroundTaskLifetime`. The base calls `LoadInitialStateAsync` after `StartListeningAsync` returns, so the loop first awaits a gate that `LoadInitialStateAsync` opens once its full read has finished. The client and the raw cache are therefore never used by the initial load and the loop at the same time.
- One timestamp per cycle, passed as both changed and received timestamp (Modbus has no source timestamp).
- The last raw register words are cached per property. A value is converted and applied only when its raw words changed, so an unchanged cycle converts nothing and raises no change events. A changed value is boxed once, because `SetValueFromSource` takes `object?`.
- Values are applied through `SubjectPropertyWriter.Write` with a static lambda and value-tuple state, calling `PropertyReference.SetValueFromSource`. The initial load's apply action calls `SetValueFromSource` directly, catching and logging per property so one rejected value cannot fail the whole load. The interceptor's equality check (`EqualityComparer<T>.Default`) is correct for `decimal?` (23.4m equals 23.40m), enums and `bool`.
- The per-request timeout uses one reusable `CancellationTokenSource` per connection (`CancelAfter` before, `TryReset` after each request) with the caller's token registered per request, instead of `Task.WaitAsync` allocating a timer per request. A timeout surfaces as `TimeoutException`, a cancellation by the caller as `OperationCanceledException`; FluentModbus closes the stream in both cases, so both end the connection. The TCP connect is bounded by the same `RequestTimeout` (`Task.WaitAsync`, once per connect).
- In this stage only discovery (before the loop starts), the initial load and the poll loop use the client, strictly one after another, so no lock is needed. The write stage adds a lock around every client transaction.
- The connector issues only read function codes (FC1 to FC4) in this stage. It contains no call to a FluentModbus write method, and an integration test asserts that the server only ever receives FC1 to FC4.

### 4.7 Lifecycle

`ModbusSubjectClientSource` inherits `SubjectSourceBase`:

- Constructor: calls `configuration.Validate()`, creates the `SourceOwnershipManager` (throws without `WithLifecycle()`), registers claimed-property and polling metrics.
- `StartListeningAsync`: connect, run discovery, build the plan (4.4), call `Metrics.MarkOperational()` (the base never does), start the poll loop. Returns the poll loop lifetime, whose cleanup closes the connection and calls `Metrics.MarkNotOperational()`. A failure here is retried by the base after `RetryTime`.
- `LoadInitialStateAsync`: one full batched read, returned as an apply action, so `Synchronized` is only reported after real values exist. Opens the poll loop gate (4.6).
- Connection loss while polling (I/O error, `RequestTimeout` or a malformed response): the source runs its own reconnect loop following the MQTT pattern: `Metrics.MarkNotOperational()`, start buffering, wait `RetryTime`, reconnect (4.4), `Metrics.MarkOperational()`, `LoadInitialStateAndResumeAsync` (the same order as the initial connect). Only the poll cycles run inside `RunAttemptAsync`; `ReconnectAsync` runs outside it (as in the MQTT source), so a Kill injected while the source reconnects is a no-op.
- `IFaultInjectable.InjectFaultAsync`: Kill uses `ForceKillCurrentAttemptAsync`, Disconnect closes the client so the reconnect loop takes over.
- The reconnect loop retries every `RetryTime` until opening the session and the reload both succeed, reporting each failure through `Metrics.ReportError` and an error log. A failure caused by a stop is not reported.
- `Dispose`/`DisposeAsync` are idempotent. `Dispose` calls the base first, which cancels the poll loop so a reconnect in flight stops, then closes the connection and releases the ownership. `DisposeAsync` awaits `StopAsync` first, then does the same.

### 4.8 Local writes in this stage

Resolved properties are claimed, so local changes are routed to `WriteChangesAsync`. Until the write stage exists, it logs a warning (once per property until the next connect), sets a per-property "reapply" flag (written with `Volatile.Write`, because `WriteChangesAsync` runs on the change queue thread while the poll loop reads the flag) and returns `WriteResult.Success`. Nothing is sent to the device. The next poll sees the flag, applies the device value even though the raw words did not change, and clears the flag, so the model converges back to the device state. Returning `Failure` would park the changes in the retry queue forever.

### 4.9 Error handling

| Failure | Behavior |
|---|---|
| TCP connect failure or connect not completing within `RequestTimeout` | Attempt fails, retried after `RetryTime`. |
| Value conversion throws | Warning logged, property keeps its value (4.3), other properties continue. |
| I/O error or timeout during a poll | Connection treated as lost, reconnect loop (4.7). |
| Malformed response (FluentModbus framing error: invalid protocol identifier, function code or length, reported as exception code 255) | Not a device rejection: connection treated as lost, reconnect loop (4.7). |
| Modbus exception response 1 (illegal function), 2 (illegal data address) or 3 (illegal data value) for a batch covering more than one property | The device does not support the request. Re-read that batch's properties individually in the same cycle, and keep them in batches of their own until the next connect (4.5). A property that still fails with one of these codes is marked unavailable until the next connect: logged once, counted in `Polling.UnavailableProperties`, value not updated. A property that fails alone with any other code is only skipped for that cycle and logged once until it succeeds again. |
| Modbus exception response with any other code (4 server failure, 5 acknowledge, 6 server busy, 10 or 11 gateway errors, and any other) for a batch covering more than one property | Transient: the batch is skipped for this cycle, so its properties keep their last value and nothing is converted or applied for them. The plan is unchanged. Counted in `Polling.FailedBatches`, warning logged once until the batch succeeds again (same tracking as below). The classification lives in one place, `ModbusResponseException.IsPermanentRejection`. |
| Modbus exception response (any code) for a single-property batch | Skipped for this cycle, never marked unavailable. Warning logged once until the batch succeeds again (tracked by unit ID, space, start address and count, so rebuilding the plan does not log it again), counted in `Polling.FailedBatches`, other batches continue. |
| No response to a request (some devices stay silent on unmapped reads instead of answering with an exception) | Treated as a timeout, so as connection loss. Device libraries avoid it by never planning unmapped reads (gap 0, exclusion); the Luxtronik hardware test records the controller's actual behavior. |
| `DiscoverAsync` throws | Connect attempt fails, retried after `RetryTime`. |
| Configuration error | `ModbusConfigurationException` from the connect attempt, logged as error, retried (a HomeBlaze device reports it through `IMonitoredService`). |

## 5. Part B: Luxtronik SHI library

### 5.1 Sources of truth

In order of authority:

1. AIT, "Betriebsanleitung Smart Home Interface Modbus TCP" (83026900aDE), the official register table. Primary source for addresses, types, scales, enum values, not-available values and timeouts.
2. AIT, "Betriebsanleitung Luxtronik 2.1 Teil 2" (83055400pDE), SHI activation and status (pp. 47-48), Smart Grid (pp. 36-37) and error 816 (p. 62).
3. `Bouni/python-luxtronik` at commit `02afea84bd5bf3ee87445de6f2a42b8029983169` (`definitions/inputs.py`, `definitions/holdings.py`, `datatypes.py`, `constants.py`, `shi/`). Source for the firmware `since` versions, which the official manual does not state.
4. raibisch LuxModbusSHI, for cross-checks only: its table has known errors (10100/10101 swapped, energy word order, level and buffer type values).

The model classes carry a comment referencing the official manual and the pinned python-luxtronik commit. The library has no runtime dependency on external schemas.

Protocol facts that shape the design:

- Addresses are raw (no `3xxxx`/`4xxxx` prefix, no +1). Input, holding and discrete input addresses all start at 10000, in separate address spaces.
- Unit ID 1 (default, configurable on the controller). Byte order and word order are both big endian, so 32-bit values are `HighWordFirst`.
- Reading a non-existent register fails the whole request. Hence `MaximumRegisterGap = 0`, firmware gating and the split-on-failure fallback (4.9).
- Since 3.92.0, a data point that exists but is not configured returns 0x7FFF (16-bit) or 0x7FFFFFFF (32-bit, per python-luxtronik `constants.py`; the official manual only mentions 32767 and the hardware dump confirms). All Luxtronik registers therefore use `NotAvailableValue = SignedMaximum`.
- Discrete inputs 10000 to 10011 report which operating modes and circuits are configured.
- SHI values written by a master are volatile (RAM only) and reset after 15 minutes without a request. The controller shows SHI as "Standby" after 10 minutes without traffic. Reading never writes flash.
- The hot water temperature (10120) reports the substitute value 75.0 °C on a sensor fault; documented, not filtered.

### 5.2 Subject rules

These apply to every Luxtronik class (per `docs/subject-guidelines.md` and the existing device libraries):

- Child subjects are `public partial X Name { get; internal set; }`, initialized in the constructor, so they are attached, registered, walked by the connector and tracked for derived properties.
- Register properties are `public partial T? Name { get; internal set; }`, initialized to `null` in the constructor. `internal set` keeps them read-only in the HomeBlaze UI.
- Every register property carries `[State]` with `Unit` (`DegreeCelsius`, `Kelvin`, `Watt`, `WattHour`, `Minute`, `Hour`), `IsCumulative = true` on energy and runtime totals, `IsDiscrete = true` on enums and bools, and a `Position` ordering the group. Without `[State]` the UI and history ignore a property.
- Constant, constructor-set metadata (`BaseAddress`, `Title` on sensors, gating information) are plain get-only properties; they never change, so they need no interception.
- Two preset attributes keep the model compact: `LuxtronikInputRegisterAttribute` and `LuxtronikHoldingRegisterAttribute` derive from an abstract `LuxtronikRegisterAttribute : ModbusRegisterAttribute`, preset `Space` and `NotAvailableValue = SignedMaximum` (the holding preset also sets `Access = ReadOnly`; `Access` is ignored for input spaces), and add `MinimumFirmware` (string such as `"3.92.0"`, parsed once and cached on the attribute) and `Feature` (a `LuxtronikFeature` whose `None = -1` sentinel means no requirement, because attribute arguments cannot be nullable enums; 5.6). Discrete inputs use a plain `ModbusRegisterAttribute`.

### 5.3 Model tree

All scaled values are `decimal?` in HomeBlaze units (°C, K, W, Wh, minutes, hours). Addresses are absolute; each class declares offsets from its own `BaseAddress`. "3.92" marks properties with `MinimumFirmware = "3.92.0"`, and a feature name marks the `LuxtronikFeature` it requires.

```
LuxtronikHeatPump   [Category("Devices")] [Description(...)] BackgroundService subject, IModbusDiscovery,
│                   IPowerSensor, IThermalPowerSensor, IConnectionState, ISoftwareState, IConfigurable,
│                   IMonitoredService, ITitleProvider, IIconProvider, ILastUpdatedProvider
│  [Configuration] Name, HostAddress, Port = 502, PollingInterval = 5 s (raised to at least 2 s)
│  [Derived] Title => Name ?? "Luxtronik Heat Pump"
│  [State] SoftwareVersion (set by discovery), [Derived] AvailableSoftwareUpdate => null
│  [State] IsConnected, Status, StatusMessage, LastUpdated (mirrored from diagnostics, 5.8)
│  [Derived] Power => Energy.ElectricalPower, EnergyConsumed => Energy.TotalElectricalEnergy
│  [Derived] ThermalPower => Energy.HeatingPower, ThermalEnergyProduced => Energy.TotalThermalEnergy
│
├─ Features          LuxtronikFeatures, discrete input 10000 (Boolean)
│    0 Heating, 1 HotWater, 2 Cooling, 3 Pool, 4 Solar, 5 RoomControlUnit,
│    6 MixingCircuit1Heating, 7 MixingCircuit1Cooling, 8 MixingCircuit2Heating, 9 MixingCircuit2Cooling,
│    10 MixingCircuit3Heating, 11 MixingCircuit3Cooling
├─ OperatingStatus   LuxtronikOperatingStatus, input 10000 (not named Status, which IMonitoredService uses)
│    0 HeatPumpStatus (flags), 2 OperationMode, 3 HeatingStatus, 4 HotWaterStatus, 6 CoolingStatus (Cooling),
│    7 PoolHeatingStatus (Pool), 201 ErrorCode, 202 BufferType, 203 MinimumOffTime, 204 MinimumRunTime (Minute),
│    207 CoolingReleased (Cooling)
│    [Derived] IsCompressorRunning, IsAuxiliaryHeaterRunning
├─ Temperatures      LuxtronikTemperatures, input 10100, Scale 0.1
│    measured, as LuxtronikTemperatureSensor children:
│      100 Return, 102 ExternalReturn, 105 Flow, 106 Room, 108 Outside, 120 HotWater;
│      3.92: 109 OutsideAverage, 110 HeatSourceInlet, 111 HeatSourceOutlet
│    setpoints and limits, plain properties:
│      101 ReturnTarget, 103 ReturnLimit, 104 ReturnMinimumTarget, 107 HeatingLimit,
│      121 HotWaterTarget, 122 HotWaterMinimum, 123 HotWaterMaximum, 124 HotWaterLimit;
│      3.92: 112 MaximumFlow, 113 CalculatedFlow
├─ Energy            LuxtronikEnergy, input 10300
│    300 HeatingPower (S16), 301 ElectricalPower, 302 MinimumPredictedElectricalPower   (Scale 100: kW/10 to W)
│    S32 HighWordFirst, Scale 100 (kWh/10 to Wh), IsCumulative:
│      310 TotalElectricalEnergy, 312 HeatingElectricalEnergy, 314 HotWaterElectricalEnergy,
│      316 CoolingElectricalEnergy (Cooling), 318 PoolElectricalEnergy (Pool)
│      3.92: 320 TotalThermalEnergy, 322 HeatingThermalEnergy, 324 HotWaterThermalEnergy,
│            326 CoolingThermalEnergy (Cooling), 328 PoolThermalEnergy (Pool)
├─ Outputs           LuxtronikOutputs, input 10350, 3.92, bool
│    350 BrineCirculationPump, 351 MixingCircuit1Pump, 352 MixingCircuit2Pump, 353 MixingCircuit3Pump,
│    354 HeatingCirculationPump, 355 HotWaterLoadingPump, 356 CirculationPump
├─ SmartGrid         LuxtronikSmartGrid, input 10360, 3.92: 360 Evu1, 361 Evu2 (bool), [Derived] State
├─ Runtime           LuxtronikRuntime, input 10404, 3.92, U32 hours, IsCumulative
│    404 HeatPump, 406 Heating, 408 HotWater, 410 Cooling (Cooling), 412 Pool (Pool), 416 Solar (Solar)
├─ ExtraHotWater     LuxtronikExtraHotWater, input 10500, 3.92
│    500 Setpoint (°C, Scale 0.1), 501 Duration (Minute), 502 RemainingDuration (Minute)
├─ Heating           LuxtronikControl, holding 10000
├─ HotWater          LuxtronikControl, holding 10005
├─ MixingCircuit1..3 LuxtronikMixingCircuit(index), features MixingCircuitNHeating / MixingCircuitNCooling
│    ├─ Temperature  LuxtronikTemperatureSensor, input 10140 / 10150 / 10160 (Heating or Cooling)
│    ├─ Setpoints    LuxtronikMixingCircuitSetpoints, input 10141 / 10151 / 10161 (Heating or Cooling): Target, Minimum, Maximum
│    ├─ Heating      LuxtronikControl, holding 10010 / 10020 / 10030 (Heating)
│    └─ Cooling      LuxtronikCoolingControl, holding 10015 / 10025 / 10035 (Cooling)
├─ PowerLimit        LuxtronikPowerLimit, holding 10040: Mode, Limit (Scale 100: kW/10 to W)
├─ Locks             LuxtronikLocks, holding 10050 (bool): 52 Cooling (Cooling), 53 Pool (Pool);
│                    3.92: 50 Heating, 51 HotWater
├─ RoomControl       LuxtronikRoomControl, holding 10060, MinimumFirmware 3.92.1, RoomControlUnit:
│                    TemperatureSetpoint (Scale 0.1)
├─ OverallHeating    LuxtronikOverallHeating, holding 10065, 3.92: Mode (LuxtronikOverallHeatingMode), Offset (K), Level
└─ HotWaterRequests  LuxtronikHotWaterRequests, holding 10070, 3.92 (bool): Circulation, ExtraHotWater

LuxtronikControl         +0 Mode, +1 Setpoint (°C), +2 Offset (K, S16), 3.92: +3 Level
LuxtronikCoolingControl  +0 Mode, +1 Setpoint (°C), +2 Offset (K, S16)
```

- All holding register properties are `Access = ReadOnly` in this stage; the write stage relaxes this per property together with range validation.
- `Features` is polled like every other group, so a reconfigured controller shows up in the UI; exclusion by feature is decided on connect (5.6).
- `SmartGrid.State` maps the two EVU signals to `LuxtronikSmartGridState` per the official manual: EVU1=1/EVU2=0 Locked, 0/0 Reduced, 0/1 Normal, 1/1 Increased.
- Input 10414 and anything the official manual and python-luxtronik do not name are not modeled.

### 5.4 HomeBlaze abstractions

`ITemperatureSensor` has a single `Temperature` property, so each measured temperature is its own child subject, as in `EcowittTemperatureSensor` and `ShellyTemperatureSensor`. Dashboards, history and the MCP type discovery see the temperatures without Luxtronik-specific code.

```csharp
[InterceptorSubject]
public partial class LuxtronikTemperatureSensor : ITemperatureSensor, ITitleProvider, IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    private readonly Version? _minimumFirmwareVersion;
    private readonly LuxtronikFeature _feature;

    public LuxtronikTemperatureSensor(int address, string title, string? minimumFirmware = null, LuxtronikFeature feature = LuxtronikFeature.None)
    {
        BaseAddress = address;
        Title = title;
        _minimumFirmwareVersion = minimumFirmware is null ? null : Version.Parse(minimumFirmware);
        _feature = feature;
        Temperature = null;
    }

    public int BaseAddress { get; }
    public string? Title { get; }

    [LuxtronikInputRegister(0, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? Temperature { get; internal set; }

    Version? ILuxtronikGatedSubject.MinimumFirmwareVersion => _minimumFirmwareVersion;
    LuxtronikFeature ILuxtronikGatedSubject.Feature => _feature;
}
```

- Each instance's `BaseAddress` is its own register address. Batching works on addresses, so the sensors still merge into contiguous reads.
- S16 is used for all temperature registers: the U16 ones (100, 101, 102, 105) decode identically below 3276.7 °C.
- A parameterized constructor means the generator emits no parameterless constructor. That is fine for children the parent creates (as `EcowittTemperatureSensor(int channel)` does); HomeBlaze never deserializes them because only `[Configuration]` properties are persisted.
- `ILuxtronikGatedSubject` (internal, implemented explicitly: `MinimumFirmwareVersion`, `Feature`, and optionally `AlternativeFeature`, which also satisfies the feature requirement) lets a subject carry gating per instance where an attribute on a shared property cannot differ. `LuxtronikMixingCircuit` passes its feature flags to its children the same way: the official manual provides the circuit temperature and setpoints while heating or cooling of that circuit is active, so they pass with either flag, and the heating and cooling controls each require their own.

New abstraction in `HomeBlaze.Abstractions/Sensors/IThermalPowerSensor.cs`, mirroring `IPowerSensor`:

```csharp
[SubjectAbstraction]
[Description("Reports thermal power output in watts and total thermal energy produced in watt-hours.")]
public interface IThermalPowerSensor
{
    [State(Unit = StateUnit.Watt, Position = 350)]
    decimal? ThermalPower { get; }

    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 351)]
    decimal? ThermalEnergyProduced { get; }
}
```

Intended for any heat producer (heat pumps, solar thermal, heat meters). On firmware older than 3.92, `ThermalEnergyProduced` is `null` because the thermal energy registers are excluded.

New `StateUnit` members `Kelvin`, `Minute` and `Hour`, with display formatting in `StateUnitExtensions` and tests. Values stay numeric (`decimal?`) so history can record them.

The Luxtronik error code has no abstraction; it stays a Luxtronik property and is reflected in `IMonitoredService.StatusMessage` when non-zero.

### 5.5 Enums

Values from the official manual; names follow python-luxtronik where the manual is silent.

```csharp
public enum LuxtronikOperationMode : ushort { Heating = 0, HotWater = 1, PoolOrSolar = 2, UtilityLockout = 3, Defrost = 4, NoRequest = 5, HeatingExternalSource = 6, Cooling = 7 }
// 6: "not assigned" in the official manual, "heating external source" in python-luxtronik
public enum LuxtronikModeStatus         : ushort { Disabled = 0, NoRequest = 1, Requested = 2, Running = 3 }
public enum LuxtronikControlMode        : ushort { NoInfluence = 0, Setpoint = 1, Offset = 2, Level = 3 }
public enum LuxtronikOverallHeatingMode : ushort { Individual = 0, Offset = 2, Level = 3 }
public enum LuxtronikLevelMode          : ushort { NoInfluence = 0, RaisedWithTimeProgram = 1, RaisedIgnoringTimeProgram = 2, Lowered = 3 }
public enum LuxtronikPowerLimitMode     : ushort { NoLimit = 0, SoftLimit = 1, HardLimit = 2 }
public enum LuxtronikBufferType         : ushort { SeriesBuffer = 0, SeparationBuffer = 1, MultifunctionBuffer = 2 }
public enum LuxtronikSmartGridState     { Locked, Reduced, Normal, Increased }
public enum LuxtronikFeature            { None = -1, Heating = 0, HotWater = 1, Cooling = 2, Pool = 3, Solar = 4, RoomControlUnit = 5,
                                          MixingCircuit1Heating = 6, MixingCircuit1Cooling = 7, MixingCircuit2Heating = 8,
                                          MixingCircuit2Cooling = 9, MixingCircuit3Heating = 10, MixingCircuit3Cooling = 11 }   // discrete input offset

[Flags]
public enum LuxtronikHeatPumpStatus : ushort { None = 0, Compressor1 = 1, Compressor2 = 2, AuxiliaryHeater1 = 4, AuxiliaryHeater2 = 8, AuxiliaryHeater3 = 16 }
```

### 5.6 Discovery: firmware and feature gating

`LuxtronikHeatPump.DiscoverAsync`:

1. Read input 10400 to 10402. On failure, throw (the connect attempt is retried). Set `SoftwareVersion` via `SetValueFromSource(context.Source, ...)`.
2. Read discrete inputs 10000 to 10011. If this read is permanently rejected (exception codes 1 to 3; the official manual does not version it), every feature gate passes and not-available values mark unconfigured functions instead. A transient rejection fails the connect attempt, which is retried.
3. Walk the device subtree once. For every register property, gating comes from its preset attribute (`MinimumFirmware`, `Feature`), read from `RegisteredSubjectProperty.ReflectionAttributes`, combined with the declaring subject's `ILuxtronikGatedSubject`. Call `context.ExcludeProperty` when the firmware is older than required or the required feature is not configured.

The model tree is always fully constructed. Discovery runs on every connect. Excluded properties stay `null` and unclaimed (a property first excluded on a later reconnect keeps its last value until restart), so the UI can show "not supported" (unclaimed) separately from "not available right now" (claimed, `null` from a not-available value). The firmware `since` versions come from python-luxtronik; the official manual does not version its registers.

### 5.7 Safety

Read-only use cannot change the heat pump's behavior:

- The connector only issues FC1 to FC4 in this stage (4.6), asserted by an integration test.
- Reads change no controller state and write no flash. SHI values live in RAM only.
- Every holding register defaults to "no influence" (modes 0, power limit 0, locks 0, requests 0), and setpoints only take effect once their mode is written. Enabling SHI and only reading leaves the heat pump's operation unchanged. The controller's "Empfangene Daten" menu keeps showing "---" and no SHI symbol appears, which is visible proof that nothing was written.
- About 25 small requests per poll cycle, fewer with feature gating. Community tools poll at similar or higher rates without reported controller problems. The device default is 5 s to be conservative.

Preconditions documented for the user (HomeBlaze device doc):

- Enabling SHI is a service-menu setting (SERVICE > Systemsteuerung > Konnektivität > Smart-Home-Interface). AIT reserves controller settings for authorized personnel; change only this setting, apart from Smart Grid.
- The manual (Teil 2, p. 47) asks to deactivate Smart Grid (SG-Ready) while SHI is used, because both can influence each other. Note the current setting first; if the installation relies on SG-Ready (PV or an energy manager on the SG contacts), decide which of the two to use before enabling SHI.
- SHI has no read-only mode (only on or off) and Modbus TCP has no authentication, so any host reaching port 502 can write. Keep port 502 LAN-only and firewalled to trusted hosts; never forward it on the router, even though the manual mentions it. For extra safety, put a read-only Modbus proxy in front (evcc modbusproxy with `readonly: true` or `readonly: deny`).
- Several clients may connect, but only one may write: two clients writing the same data point raise error 816, and SHI is disabled while it persists. Read-only HomeBlaze is not a writer, but written values reset 15 minutes after the master's last request and reads count as requests, so continuous polling can keep another client's last written values active.
- First run: one client at a time, and check that "Empfangene Daten" keeps showing "---".
- Switch SHI off again if it is not needed after testing.

### 5.8 HomeBlaze device

Follows `HomeBlaze.OpcUa/OpcUaClient.cs` and `Namotion.Devices.Wallbox/WallboxCharger.cs`:

- `ExecuteAsync` builds a `ModbusClientConfiguration` from `HostAddress`, `Port` and `PollingInterval`, creates the source with `this.CreateModbusClientSource(configuration, logger)` and attaches it with `AttachHostedServiceAsync`. On stop or configuration change it detaches and disposes the source, then recreates it.
- Source diagnostics are not tracked properties, so the same loop refreshes the device's partial `[State]` properties every `PollingInterval`: `IsConnected = Diagnostics.IsOperational == true`, `Status` Starting until the source reports an error, Error once the connection fails and Running while connected (a non-zero `OperatingStatus.ErrorCode` appears in `StatusMessage` only), `LastUpdated` from `Diagnostics.Polling.LastPollTime` (as `OpcUaClient` does in its diagnostics loop).
- Deliverables from the `create-homeblaze-library` command: `LuxtronikServiceCollectionExtensions` using `AddHostedSubject`, the device doc and sample configuration (5.10), project references in `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`, and `TypeProvider.AddAssembly` registration for both assemblies in `Program.cs`.

### 5.9 UI (`Namotion.Devices.Luxtronik.HomeBlaze`)

Following the Ecowitt and Wallbox UI projects: `LuxtronikHeatPumpWidget` (operation mode, outside, flow and hot water temperatures, electrical and heating power, error code) and `LuxtronikHeatPumpEditComponent` (name, host address, port, polling interval). No setup component, as for other simple IP-based devices.

### 5.10 Documentation

**`docs/connectors-modbus.md`**, modelled on `docs/connectors-mqtt.md`: intro (client only, read only in this stage), Key Features, Client Setup (DI and `CreateModbusClientSource`, a subject example), Register Mapping (address spaces, data types, word order, scaling, not-available values, base address and unit ID providers), Discovery, Configuration table, Batching and Polling, Resilience (reconnect, split-on-failure), Diagnostics, Thread Safety, Lifecycle, Limitations, and a final **References** section (not "Sources", which `connectors.md` uses for the source concept):

- Modbus Application Protocol Specification V1.1b3: https://modbus.org/docs/Modbus_Application_Protocol_V1_1b3.pdf
- Modbus Messaging on TCP/IP Implementation Guide V1.0b: https://modbus.org/docs/Modbus_Messaging_Implementation_Guide_V1_0b.pdf
- FluentModbus: https://github.com/Apollo3zehn/FluentModbus

Also link the new page from `docs/connectors.md` (connector list) and `README.md` (connector paragraph and table).

**`src/HomeBlaze/HomeBlaze/Data/Docs/devices/Luxtronik.md`**, modelled on `Wallbox.md` and `Shelly.md`: frontmatter `title`/`icon`, intro, Supported Devices, Safety and Prerequisites (5.7), Configuration table, State Properties grouped per child subject (property, unit, description), Interfaces, JSON configuration example (`"$type": "Namotion.Devices.Luxtronik.LuxtronikHeatPump"`), Troubleshooting (SHI not enabled, port blocked, error 816, 0x7FFF values, "Standby" status), Modbus Register Map, and a final **References** section:

- AIT, Betriebsanleitung Smart Home Interface Modbus TCP (83026900aDE): https://files.ait-group.net/FILES/Alpha-InnoTec/Betriebsanleitungen/01%20Waermepumpen/05%20Regler/Zubehoer/83026900aDE_SHI.pdf
- AIT, Betriebsanleitung Luxtronik 2.1 Teil 2 (83055400pDE): https://files.ait-group.net/FILES/Alpha-InnoTec/Betriebsanleitungen/01%20Waermepumpen/05%20Regler/LUX/83055400pDE_Lux_21_Teil_2.pdf
- python-luxtronik SHI README: https://github.com/Bouni/python-luxtronik/blob/02afea84bd5bf3ee87445de6f2a42b8029983169/luxtronik/shi/README.md
- python-luxtronik input definitions: https://github.com/Bouni/python-luxtronik/blob/02afea84bd5bf3ee87445de6f2a42b8029983169/luxtronik/definitions/inputs.py
- python-luxtronik holding definitions: https://github.com/Bouni/python-luxtronik/blob/02afea84bd5bf3ee87445de6f2a42b8029983169/luxtronik/definitions/holdings.py
- python-luxtronik constants (not-available values): https://github.com/Bouni/python-luxtronik/blob/02afea84bd5bf3ee87445de6f2a42b8029983169/luxtronik/constants.py
- python-luxtronik PR #213, update from the official documentation: https://github.com/Bouni/python-luxtronik/pull/213
- raibisch LuxModbusSHI how-to (register table has known errors): https://github.com/raibisch/mylibs/blob/main/LuxModbusSHI/LuxtronikSHI.md
- evcc Luxtronik support, PR #21516: https://github.com/evcc-io/evcc/pull/21516
- haustechnikdialog forum thread on the Luxtronik 2.1 SHI: https://www.haustechnikdialog.de/Forum/t/284442/Eigene-Regelung-PV-Luxtronik-2-1-Smart-Home-Interface-SHI

Sample configuration in `src/HomeBlaze/HomeBlaze/Data/Devices/Luxtronik.json`.

## 6. Testing and verification

### 6.1 Connector unit tests (`Namotion.Interceptor.Modbus.Tests`)

- Codecs: every data type times every word order, decode round trips, string trimming.
- Conversion: all numeric targets, `decimal?` exactness, enums, flags enums, `bool`, static and dynamic scaling, not-available values for each width and pattern.
- Resolution: base addresses, unit ID precedence, derived preset attributes, every validation rule in 4.2.
- Batch planner: gap handling, PDU limits for registers and bits, scale-factor ordering, grouping by unit ID and space.
- Public API snapshot (`VerifyChecksTests.PublicApi`).

### 6.2 Connector integration tests (`[Trait("Category", "Integration")]`)

In-process FluentModbus `ModbusTcpServer` on a free port, registers written directly into server memory:

- Batched reads across all four address spaces and several unit IDs.
- Only read function codes: the server's request validator records every function code, and the test asserts FC1 to FC4 only, including after local writes to claimed properties.
- Split-on-failure: the request validator rejects chosen addresses; neighbours still update, the rejected property is marked unavailable.
- Discovery: raw reads of all four spaces, `ExcludeProperty`, context invalid after return.
- Reconnect: stop and restart the server, discovery runs again, polling resumes, state goes through `Synchronized` again (non-parallel test collection because of port reuse).
- Local write: nothing sent, value restored by the next poll.
- Diagnostics counters, including the claimed property count.

No hardcoded waits: `AsyncTestHelpers.WaitUntilAsync` and `SourceStateRecorder`.

### 6.3 Luxtronik tests (`Namotion.Devices.Luxtronik.Tests`)

`LuxtronikTestServer` wraps `ModbusTcpServer` pre-filled with the SHI map (firmware 3.92.3, all features configured by default) or a recorded register dump:

- Full read of the model, enums, scaling to W and Wh, signed temperatures, energy decoding with `HighWordFirst`.
- Firmware 3.90 vs 3.92: 3.92 properties excluded and unclaimed on 3.90, and a 3.90 server rejecting 3.92 addresses causes no failures.
- Feature gating: with cooling and pool not configured, their properties are excluded; with the discrete input read rejected, gating is skipped and 0x7FFF values map to `null`.
- Abstractions: temperature sensor children report `ITemperatureSensor.Temperature`, the device reports `IPowerSensor` and `IThermalPowerSensor` in W and Wh, derived properties update when the child values change.
- Device status: `IsConnected`, `Status` and `LastUpdated` follow the source state.

### 6.4 Hardware verification

A `[LuxtronikHardwareFact]` attribute (derived from `FactAttribute`) sets `Skip` unless `LUXTRONIK_HOST` is set, so the test is reported as skipped rather than passing in CI. It also carries `[Trait("Category", "Integration")]` so default unit test runs exclude it. It only reads:

- Firmware, discrete inputs 10000 to 10011, and every mapped input and holding register, each block read separately, failures tolerated and recorded.
- One deliberately unmapped input register (10001) to record whether the controller answers unmapped reads with a Modbus exception or not at all. If it does not answer, split-on-failure re-reads would surface as timeouts and reconnects; the result decides whether that path needs a device-specific setting.
- The raw values of features that are not configured on the target unit, to confirm the 16-bit and 32-bit not-available patterns.

The raw values are written to a JSON dump. Run once against the target 3.92.3 unit, compare with the controller display (return actual and target, electrical and thermal energy, heating power, runtime hours), fix any mapping, and check the dump in as a fixture for `LuxtronikTestServer`.

### 6.5 Long-running verification

The Connector Tester is not run for this stage (agreed during planning): the connector is client-only and read-only, and the Connector Tester pairs a server with a client. It is planned together with the Modbus server connector and writes.

## 7. Designed for SunSpec

SunSpec is the next device library ([2026-09-28-sunspec-design.md](2026-09-28-sunspec-design.md)). This connector already provides what it needs for reading: strings (Common model), dynamic scale factors, not-available values (`SignedMinimum`, `UnsignedMaximum`), `IModbusUnitIdProvider` for several units per connection, `IModbusBaseAddressProvider` for model instances at runtime addresses, and `IModbusDiscovery` for the chain walk. The write stage adds what SunSpec controls need.

## 8. Future work

- Write stage: FC5/FC6/FC15/FC16, reverse scaling from the cached scale factors, a client lock shared with the poll loop, `Access` enforcement, minimum/maximum validation (the official manual lists ranges per holding register), a reconnect policy for volatile controls (SHI values reset after 15 minutes and must not be replayed blindly), and the manual's guidance on conflicting controls (for example room setpoint vs heating offset).
- Modbus server connector (`Namotion.Interceptor.Modbus` `Server/`) and Connector Tester integration.
- Rebuilding the read plan on attach/detach instead of on reconnect.
- Probing unknown firmware instead of version tables.
- Modbus RTU.
- Luxtronik data beyond SHI (flow rate, heat pump type) through the web interface or CFI.
