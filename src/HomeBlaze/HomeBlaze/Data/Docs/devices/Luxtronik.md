---
title: Luxtronik Heat Pump
icon: HeatPump
---

# Luxtronik Heat Pump

Reads Luxtronik 2.1 heat pumps (Alpha Innotec, Novelan and other ait-deutschland brands) through the Smart Home Interface (SHI), which is Modbus TCP on port 502. This integration is strictly read only: it only ever sends read function codes (2 to 4) and never writes a value to the controller.

## Supported Devices

- Luxtronik 2.1 controllers with the Smart Home Interface, firmware 3.90.1 or later (the minimum the SHI manual names)
- Values added in firmware 3.92 (thermal energy, operating hours, pump outputs, Smart Grid signals, the outside average, heat source and maximum and calculated flow temperatures, extra hot water state, levels, heating and hot water locks, overall heating control, hot water requests) are read only when the controller runs 3.92 or later; the room temperature setpoint needs 3.92.1

## Safety and Prerequisites

Reading does not change how the heat pump runs. Reads change no controller state, and every SHI control starts at "no influence" and only takes effect once a client writes it, which this integration never does. The risks come from enabling the interface itself, so read this section before switching it on.

### Enabling the Smart Home Interface

The SHI is switched on in the service menu: SERVICE > Systemsteuerung > Konnektivität > Smart-Home-Interface. AIT reserves controller settings for authorised service personnel, so if you do this yourself, change only this one setting. Note the current Smart Grid (SG-Ready) setting before you start and leave it unchanged. The Luxtronik manual (Teil 2, p. 47) says that Smart Grid and the SHI can influence each other.

Switch the SHI off again when you no longer need it.

### Network

The SHI has no read-only mode (it is only on or off) and no authentication: any device that reaches port 502 can write setpoints, modes and locks. Therefore:

- Keep port 502 inside your LAN and never forward it on the router to the internet, even though the controller manual mentions opening it on the router.
- Firewall the controller so that only trusted hosts reach port 502.
- For extra safety, put a read-only Modbus proxy in front of the controller and point HomeBlaze at the proxy, for example the [evcc modbusproxy](https://docs.evcc.io/en/reference/configuration/modbusproxy/) with `readonly: true` (writes are dropped) or `readonly: deny` (writes are answered with a Modbus error).

### Other SHI clients

The controller accepts several SHI clients at once, but when two of them write the same data point it raises error 816 "Datenpunkt wurde von mehreren Quellen überschrieben". The manual warns that this can damage the device, and the SHI is disabled while the error persists. Only one system (for example evcc or an energy manager) should ever write to the SHI. HomeBlaze only reads, so it never causes this error.

Written values fall back to their defaults 15 minutes after the last request from the master. If the controller counts reads from any client as requests, continuous polling can keep another client's last written values active after that client stopped. Stop HomeBlaze (or clear the host address) if you rely on this timeout to reset another client's values.

### First run

1. Connect one client at a time. Start with HomeBlaze alone.
2. Watch "Empfangene Daten" (received data) on the controller display. It lists every value written through the SHI and must keep showing "---" while only HomeBlaze is connected. The SHI symbol on the navigation screen, which appears once a value was written through the SHI (Teil 2, p. 48), stays hidden as well.
3. Compare a few values with the display (outside, flow and return temperatures, electrical power, energy totals).

The controller shows the SHI as "Standby" after 10 minutes without requests and "Aktiv" while requests arrive.

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | string | "" | Display name |
| `HostAddress` | string | - | Controller (or proxy) IP address or host name |
| `Port` | int | 502 | Modbus TCP port |
| `PollingInterval` | TimeSpan | 5 seconds | Time between reads; values below 2 seconds are raised to 2 seconds |

A configuration change restarts the connection.

## Status

| `Status` | When | `StatusMessage` |
|----------|------|-----------------|
| Stopped | No host address configured | "No host address configured" |
| Starting | Connecting, no error yet | "Connecting..." |
| Running | Connected and polling | "Heat pump error N" when the controller reports error N, otherwise empty |
| Error | The connection failed (stays set while reconnecting), or the configuration is invalid | The last error, or the configuration error |

A controller error does not change `Status`: the connection is healthy, so it stays Running and the error number is shown in `StatusMessage` and in the widget.

## State Properties

Temperatures are °C, temperature offsets K, power W, energy Wh, durations minutes and operating hours h. Properties are read only; a local change is replaced by the controller value on the next poll.

A value is empty when the controller does not provide it. On every connect the device reads the firmware version and the configured functions (discrete inputs 10000 to 10011) and skips values the firmware or configuration does not support. Values the controller reports as not available (0x7FFF or 0x7FFFFFFF) are also shown empty.

| Group | Properties |
|-------|-----------|
| Device | `IsConnected`, `Status`, `StatusMessage`, `LastUpdated`, `SoftwareVersion` (firmware, such as "3.92.3"), `Power`, `EnergyConsumed`, `ThermalPower`, `ThermalEnergyProduced` |
| `OperatingStatus` | `HeatPumpStatus` (running compressors and auxiliary heaters), `IsCompressorRunning`, `IsAuxiliaryHeaterRunning`, `OperationMode`, `HeatingStatus`, `HotWaterStatus`, `CoolingStatus`, `PoolHeatingStatus`, `ErrorCode`, `BufferType`, `MinimumOffTime` (cycling lock), `MinimumRunTime`, `CoolingReleased` |
| `Temperatures` | Sensors: `Return`, `ExternalReturn`, `Flow`, `Room`, `Outside`, `OutsideAverage` (24 hours), `HeatSourceInlet`, `HeatSourceOutlet`, `HotWater`. Values: `ReturnTarget`, `ReturnLimit` (maximum return), `ReturnMinimumTarget`, `HeatingLimit`, `MaximumFlow`, `CalculatedFlow` (return target plus spread), `HotWaterTarget`, `HotWaterMinimum`, `HotWaterMaximum`, `HotWaterLimit` (below it the heat pump ignores a soft power limit) |
| `Energy` | `HeatingPower`, `ElectricalPower`, `MinimumPredictedElectricalPower`; electrical and thermal energy totals for all modes, heating, hot water, cooling and pool |
| `Runtime` | Operating hours: `HeatPump`, `Heating`, `HotWater`, `Cooling`, `Pool`, `Solar` |
| `Outputs` | Pump outputs: `BrineCirculationPump` (BOSUP), `MixingCircuit1Pump` to `MixingCircuit3Pump` (FP1 to FP3), `HeatingCirculationPump` (HUP), `HotWaterCirculationPump` (hot water loading pump, BUP), `CirculationPump` (hot water circulation, ZIP) |
| `SmartGrid` | `Evu1`, `Evu2`, `State` (Locked, Reduced, Normal, Increased) |
| `ExtraHotWater` | `Setpoint`, `Duration`, `RemainingDuration` |
| `Features` | Which functions are configured on the controller (heating, hot water, cooling, pool, solar, room control unit, heating and cooling per mixing circuit) |
| `Heating`, `HotWater` | Current SHI control values: `Mode`, `Setpoint`, `Offset`, `Level` |
| `MixingCircuit1` to `MixingCircuit3` | `Temperature` (flow temperature sensor), `Setpoints` (`Target`, `Minimum`, `Maximum`), `Heating` and `Cooling` SHI controls |
| `PowerLimit`, `Locks`, `RoomControl`, `OverallHeating`, `HotWaterRequests` | Current SHI control values |

The SHI control values show what the controller currently uses; with no writing client they read "no influence" (mode 0, locks and requests off).

Each measured temperature is a child sensor titled after its value, such as "Return temperature" or "Mixing circuit 1 temperature".

## Interfaces

- `IPowerSensor`: electrical power and consumed energy
- `IThermalPowerSensor`: heating power and produced thermal energy (thermal energy needs firmware 3.92)
- `ITemperatureSensor`: each measured temperature is its own child sensor
- `IConnectionState`: SHI connection state
- `ISoftwareState`: controller firmware version (`AvailableSoftwareUpdate` is always empty)
- `IMonitoredService`: service status and heat pump error number

## JSON Configuration Example

```json
{
  "$type": "Namotion.Devices.Luxtronik.LuxtronikHeatPump",
  "name": "Heat Pump",
  "hostAddress": "192.168.1.50",
  "port": 502,
  "pollingInterval": "00:00:05"
}
```

## Troubleshooting

- **Connection refused or timeouts:** the SHI is not enabled on the controller, or a firewall blocks port 502.
- **Values stay empty:** the function is not configured on the controller, or the value needs firmware 3.92. If the controller rejects the read of its configured functions, the device reads every value regardless of configuration and relies on the not-available values instead. A temporary rejection of that read fails the connect, which is retried.
- **Controller error 816:** more than one client writes the same SHI data point, and the SHI stays disabled while the error persists. HomeBlaze does not write; check the other clients.
- **SHI "Standby" on the controller:** no requests for 10 minutes; check that HomeBlaze is running and connected.
- **Hot water or a mixing circuit temperature shows exactly 75.0 °C, or the external return 5.0 °C:** the controller reports these substitute values when the sensor is faulty. They are passed through unfiltered; check the sensor.
- **A cooling-only mixing circuit shows no temperature or setpoints:** these values are read only when the circuit is configured for heating. The controller provides the temperature and target for cooling circuits too, so this is a known limitation.
- **A value keeps its last reading after a function was switched off:** functions and firmware are checked on every connect, and a value that a later reconnect skips keeps its last reading until HomeBlaze restarts.

### Register dump

The test project contains a hardware test that reads every mapped register (read function codes only, one request at a time with short pauses), probes one unmapped input register (10001) to record how the controller answers it, and writes the raw values to JSON. It is skipped unless `LUXTRONIK_HOST` is set, so set the variable only in the shell session you run it from and never persistently, otherwise every test run would contact the controller:

```powershell
$env:LUXTRONIK_HOST='192.168.x.y'; dotnet test src/HomeBlaze/Namotion.Devices.Luxtronik.Tests --filter "FullyQualifiedName~LuxtronikHardwareTests" --logger "console;verbosity=detailed"
```

The dump is written to `bin/Debug/net10.0/luxtronik-dump.json` in the test project, or to the path in `LUXTRONIK_DUMP_PATH`. `LUXTRONIK_PORT` overrides port 502.

## Modbus Register Map

Addresses are raw Modbus addresses (no +1). Input registers are read with function code 4, holding registers with 3, discrete inputs with 2. Input, holding and discrete input addresses all start at 10000 in separate address spaces. 32-bit values are high word first.

| Address | Space | Content |
|---------|-------|---------|
| 10000 | Input | Heat pump status bits (compressors, auxiliary heaters) |
| 10002 to 10007 | Input | Operation mode, heating, hot water, cooling and pool status |
| 10100 to 10113 | Input | Return, flow, room, outside and heat source temperatures and limits (0.1 °C); 10109 to 10113 from 3.92 |
| 10120 to 10124 | Input | Hot water temperature, target and limits (0.1 °C) |
| 10140 to 10163 | Input | Mixing circuit 1 to 3 flow temperature, target, minimum, maximum (0.1 °C) |
| 10201 to 10207 | Input | Error number, buffer type, minimum off and run times, cooling release |
| 10300 to 10302 | Input | Heating power, electrical power, minimum predicted power (0.1 kW) |
| 10310 to 10329 | Input | Electrical and thermal energy, 32-bit (0.1 kWh); thermal from 3.92 |
| 10350 to 10356 | Input | Pump outputs (3.92) |
| 10360 to 10361 | Input | Smart Grid signals EVU1 and EVU2 (3.92) |
| 10400 to 10402 | Input | Firmware major, minor, patch |
| 10404 to 10417 | Input | Operating hours, 32-bit (3.92) |
| 10500 to 10502 | Input | Extra hot water setpoint, duration, remaining time (3.92) |
| 10000 to 10008 | Holding | Heating and hot water control: mode, setpoint, offset, level (level from 3.92) |
| 10010 to 10037 | Holding | Mixing circuit 1 to 3 heating and cooling control |
| 10040 to 10041 | Holding | Power limit mode and value (0.1 kW) |
| 10050 to 10053 | Holding | Heating, hot water, cooling and pool locks (heating and hot water from 3.92) |
| 10060 | Holding | Room temperature setpoint (3.92.1, room control unit) |
| 10065 to 10067 | Holding | Overall heating control (3.92) |
| 10070 to 10071 | Holding | Circulation and extra hot water requests (3.92) |
| 10000 to 10011 | Discrete input | Configured functions |

The firmware gates come from python-luxtronik; the official manual does not version its registers.

## References

- [AIT, Betriebsanleitung Smart Home Interface Modbus TCP (83026900aDE)](https://files.ait-group.net/FILES/Alpha-InnoTec/Betriebsanleitungen/01%20Waermepumpen/05%20Regler/Zubehoer/83026900aDE_SHI.pdf)
- [AIT, Betriebsanleitung Luxtronik 2.1 Teil 2 (83055400pDE)](https://files.ait-group.net/FILES/Alpha-InnoTec/Betriebsanleitungen/01%20Waermepumpen/05%20Regler/LUX/83055400pDE_Lux_21_Teil_2.pdf): SHI activation and status, "Empfangene Daten", Smart Grid, error 816
- [evcc modbusproxy (read-only proxy)](https://docs.evcc.io/en/reference/configuration/modbusproxy/)
- [python-luxtronik SHI README](https://github.com/Bouni/python-luxtronik/blob/02afea84bd5bf3ee87445de6f2a42b8029983169/luxtronik/shi/README.md)
- [python-luxtronik input definitions](https://github.com/Bouni/python-luxtronik/blob/02afea84bd5bf3ee87445de6f2a42b8029983169/luxtronik/definitions/inputs.py)
- [python-luxtronik holding definitions](https://github.com/Bouni/python-luxtronik/blob/02afea84bd5bf3ee87445de6f2a42b8029983169/luxtronik/definitions/holdings.py)
- [python-luxtronik constants (not-available values)](https://github.com/Bouni/python-luxtronik/blob/02afea84bd5bf3ee87445de6f2a42b8029983169/luxtronik/constants.py)
- [python-luxtronik PR 213, update from the official documentation](https://github.com/Bouni/python-luxtronik/pull/213)
- [raibisch LuxModbusSHI how-to (its register table has known errors)](https://github.com/raibisch/mylibs/blob/main/LuxModbusSHI/LuxtronikSHI.md)
- [evcc Luxtronik support, PR 21516](https://github.com/evcc-io/evcc/pull/21516)
- [haustechnikdialog forum thread on the Luxtronik 2.1 SHI](https://www.haustechnikdialog.de/Forum/t/284442/Eigene-Regelung-PV-Luxtronik-2-1-Smart-Home-Interface-SHI)
