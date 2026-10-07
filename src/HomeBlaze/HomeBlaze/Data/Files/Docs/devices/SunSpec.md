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
- **Models**: every SunSpec model of the official catalog has a typed class. Inverters (101 to 103, 111 to 113) and meters (201 to 204, 211 to 214) report power and energy, storage (713) reports the battery level.

Values the device does not implement are shown as empty, and so are registers beyond the length a model reports and the values scaled by them. Percentages are fractions from 0 to 1 (shown as percent), and durations (`Secs`, `mSecs` and similar units) are time spans.

## Configuration

| Setting | Default | Description |
|---|---|---|
| Host Address | required | IP address or host name |
| Port | 502 | Modbus TCP port; SolarEdge uses 1502 |
| Unit IDs | 1 | Comma-separated Modbus unit IDs, 1 to 247 |
| Polling Interval | 10 s | 1 s to 1 h |
| Model Definitions Folder | data directory `SunSpec/Models` | SunSpec JSON files for models without a built-in class |
| Log register dump | off | Logs all registers of the chain on discovery |

A configuration change restarts the connection.

## Status

| Status | When | Status message |
|---|---|---|
| Stopped | No host address configured | "No host address configured" |
| Starting | Connecting, no error yet | "Connecting..." |
| Running | Connected and polling | Empty, or the missing units, such as "Unit 2 not found" or "No SunSpec unit found" |
| Error | The connection failed (stays set while reconnecting), or the configuration is invalid | The last error, or the configuration error |

While a configured unit is missing, the device discovers its units again every polling interval, but at most once a minute. A unit that a gateway cannot reach (Modbus exceptions 10 and 11) is skipped; the other units are still read. When a polled model ID no longer matches the discovered chain, for example after a firmware update, the device disconnects, waits one polling interval and discovers the chain again.

## Power Directions

Inverters report their AC output as a measured circuit: production is export, so the measured power is negative while the inverter produces. Meters pass the device's power through unchanged; check the sign against a known load before relying on it.

## Additional Models

Models that are newer than the built-in catalog, or vendor models published in the SunSpec JSON format, can be added as files in the model definitions folder. They are read when the device connects. A file for a built-in model, a second file for the same model, and an invalid file (for example with null values, or reserved or duplicate point names) are logged and skipped. A model without any definition is shown as an unknown model with its ID, address and length.

## Register Dump

Enable "Log register dump" to log the raw registers of every model in the chain. The dump can be replayed in tests, which is the easiest way to report a device that is not read correctly.

## SolarEdge

- Modbus TCP must be enabled by the installer in SetApp (Site Communication, Modbus TCP). The port is 1502.
- After enabling it, connect within about 2 minutes the first time.
- SolarEdge accepts only one Modbus TCP client at a time. A refused connection usually means another client (another home automation system, an energy manager) is connected. HomeBlaze retries every 10 seconds.
- Meters connected to the inverter appear as their own logical devices.
- Battery data outside the SunSpec models (the SolarEdge battery registers at 0xE100) is not read yet.

## Known Limitations

- Some timestamps, which SunSpec stores as seconds since 2000, are shown as durations.

## Network

Modbus TCP has no authentication. Keep the port inside your network and never forward it on the router.
