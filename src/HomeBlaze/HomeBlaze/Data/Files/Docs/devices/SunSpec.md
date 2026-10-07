---
title: SunSpec Devices
icon: SolarPower
---

# SunSpec Devices

Reads inverters, meters and batteries that implement the [SunSpec](https://sunspec.org) Modbus information models, over Modbus TCP. This integration is read only: it only sends read requests.

## How It Works

A SunSpec device describes itself with a chain of models after the "SunS" marker at register 40000 (or 50000 or 0). On every connect HomeBlaze walks this chain for each configured unit ID and shows what it finds:

- **Units**: one entry per unit ID. A meter or battery can sit behind the same connection under another unit ID.
- **Logical devices**: each Common model (model 1) starts a new device, so an inverter and the meters connected to it appear as separate devices with their own manufacturer, model and serial number.
- **Models**: every SunSpec model of the official catalog has a typed class. Inverters (101 to 103, 111 to 113), the DER AC measurement (701) and meters (201 to 204, 211 to 214) report power and energy; storage (713) and batteries (802) report the battery level. Common models such as nameplate (120), settings (121), status (122), batteries (802 to 805) and weather sensors (302, 307) have named classes, all others are `SunSpecModel{Id}`.

Values the device does not implement are shown as empty, and so are registers beyond the length a model reports and the values scaled by them. Percentages are fractions from 0 to 1 (shown as percent), and durations (`Secs`, `mSecs` and similar units) are time spans.

## Configuration

| Setting | Default | Description |
|---|---|---|
| Host Address | required | IP address or host name |
| Port | 502 | Modbus TCP port; SolarEdge uses 1502 |
| Unit IDs | 1 | Comma-separated Modbus unit IDs, 1 to 247 |
| Polling Interval | 10 s | 1 s to 1 h |
| Model Definitions Folder | data directory `SunSpec/Models` | SunSpec JSON files for models without a built-in class |
| Log register dump | off | Logs all registers of the chain when it is discovered |

A configuration change restarts the connection.

Only configure unit IDs that exist. A unit ID the device does not answer at all (a timeout rather than an error response, common when connecting to a device directly instead of through a gateway) fails the connection for all units.

## Status

| Status | When | Status message |
|---|---|---|
| Stopped | No host address configured | "No host address configured" |
| Starting | Connecting, no error yet | "Connecting..." |
| Running | Connected and polling | Empty, or the missing units, such as "Unit 2 not found" or "No SunSpec unit found" |
| Error | The connection failed (stays set while reconnecting), or the configuration is invalid | The last error, or the configuration error |

While a configured unit is missing, the device discovers its units again: first after one minute (or one polling interval, when longer), then twice as long after each attempt that still misses a unit, up to once an hour. The interval starts over when a unit is found or lost, or the configuration changes. Each attempt briefly reconnects; the status stays Running meanwhile. A unit that a gateway cannot reach (Modbus exceptions 10 and 11) is skipped; the other units are still read. When a polled model ID no longer matches the discovered chain, for example after a firmware update, the device disconnects, waits one polling interval and discovers the chain again.

## Power Directions

Inverters report their AC output as a measured circuit: production is export, so the measured power is negative while the inverter produces. Meters pass the device's power through unchanged; check the sign against a known load before relying on it.

## Additional Models

Models that are newer than the built-in catalog, or vendor models published in the SunSpec JSON format, can be added as files in the model definitions folder. They are read when the device connects. A file for a built-in model, a second file for the same model, and an invalid file (for example with null values, or reserved or duplicate point names) are logged and skipped. A model without any definition is shown as an unknown model with its ID, address and length.

## Register Dump

Enable "Log register dump" to log the raw registers of every model in the chain. The dump is logged on the first discovery after a configuration change and whenever the chain changes. The dump can be replayed in tests, which is the easiest way to report a device that is not read correctly.

## SolarEdge

- Modbus TCP must be enabled by the installer in SetApp (Site Communication, Modbus TCP). The port is 1502.
- After enabling it, connect within about 2 minutes the first time.
- SolarEdge accepts only one Modbus TCP client at a time. A refused connection usually means another client (another home automation system, an energy manager) is connected. HomeBlaze retries every 10 seconds.
- Meters connected to the inverter appear as their own logical devices.
- Battery data outside the SunSpec models (the SolarEdge battery registers at 0xE100) is not read yet.

## Known Limitations

- Some timestamps, which SunSpec stores as seconds since 2000, are shown as durations.
- The nine harmonics text points of model 64411 (150 registers each) cost two requests each per poll, plus a second read of both requests in the same poll when they change, and a text that changes faster than one pair of reads is not applied.

## Network

Modbus TCP has no authentication. Keep the port inside your network and never forward it on the router.

## Implementation Details

### Model Classes and the Generator

The model classes in `Namotion.Devices.SunSpec/Models/Generated` are generated from the official SunSpec JSON definitions, which are embedded unmodified in the library (`Definitions/Json`, pinned to a commit of [sunspec/models](https://github.com/sunspec/models)). The console tool `Namotion.Devices.SunSpec.Generator` writes one `[InterceptorSubject]` class per model, with a `[ModbusRegister]` and a `[State]` attribute per point:

- Point names are used verbatim as property names (`W`, `W_SF`, `TotWhImp`), group classes and enums use the class name as prefix (`SunSpecMpptModuleGroup`, `SunSpecInverterSt`).
- Scaled measurements are `decimal?`, durations `TimeSpan?`, text `string?`, enumerations and bit fields generated enums, all other integers their raw type. Percentages get `Scale = 0.01` on top of their scale factor, so they arrive as fractions.
- `overrides.json` gives friendly names to the models a home installation is likely to meet and merges families with the same layout into one class (`SunSpecInverter` for 101 to 103, `SunSpecAcMeter` for 201 to 204). All other models are `SunSpecModel{Id}`.
- Capabilities such as `IPowerMeter` or `IBatteryState` are hand-written partial classes next to the generated ones (`Models/SunSpecInverter.cs`).

To update the definitions, copy the files of a newer commit, update the commit in `Definitions/Json/README.md` and run `dotnet run --project src/HomeBlaze/Namotion.Devices.SunSpec.Generator -- src/HomeBlaze/Namotion.Devices.SunSpec/Models/Generated`. A test fails when the checked-in classes differ from the generator output, and the generator fails when a family's layouts differ or a generated name collides.

### Scale Factors

Scaling stays in the Modbus connector, so a value and its scale factor always come from the same poll and never tear. A point scaled by a scale factor of its own block uses `ScaleFactorProperty`; a repeating group whose scale factors live in its parent (the module groups of model 160) implements `IModbusScaleFactorProvider` and returns a reference to the parent's property. The connector resolves these links once per connect; see [Modbus connector: register mapping](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/docs/connectors-modbus.md) for the rules.

### Discovery

`SunSpecDevice` owns the Modbus source and implements `IModbusDiscovery`, so discovery runs on every connect before the registers are bound:

1. For each unit ID the chain reader probes 40000, 50000 and 0 for the "SunS" marker and walks the model headers (ID and length) until the end marker 0xFFFF, in reads of at most 125 registers. A chain longer than 500 models or past the address space is rejected for that unit.
2. Each Common model starts a logical device. Each model becomes a generated class, a dynamic model (user definition) or an unknown model; an existing subject is kept when its address, ID and length are unchanged, so the UI and history keep their references across reconnects.
3. Repeating groups are resolved from the counts the device reports and created as child subjects.
4. Registers beyond the length a device reports for a model, and the values scaled by them, are excluded from polling.
5. Each discovered model ID is written to the model's `ModelIdRegister`, which is polled with the model (register gap 2). The status loop compares it with the discovered ID and restarts the connection when the device's chain changed.

### Dynamic and Unknown Models

A user definition is parsed and validated when the source starts (point and group names must be unique and must not hide built-in members); parsed definitions are cached by file content, so an unchanged file keeps its subjects across restarts. A dynamic model adds one registry property per point at runtime, with the same register and state attributes the generator writes, so it is polled and displayed like a generated model. An unknown model only maps its ID register.

### Tests

The tests run against `SunSpecTestServer`, a FluentModbus server that lays out chains from the definitions and rejects reads outside the chain like a real device, and against device dumps from [pysunspec2](https://github.com/sunspec/pysunspec2). A register dump logged by this device can be replayed with `SunSpecTestChain.FromDump`.

## References

- [SunSpec Alliance](https://sunspec.org) (information model specifications)
- [sunspec/models](https://github.com/sunspec/models) (JSON model definitions)
- [sunspec/pysunspec2](https://github.com/sunspec/pysunspec2) (reference implementation and test data)
- [SolarEdge SunSpec technical note](https://knowledge-center.solaredge.com/sites/kc/files/sunspec-implementation-technical-note.pdf)
