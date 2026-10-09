---
title: Luxtronik Heat Pump
icon: HeatPump
---

# Luxtronik Heat Pump

Reads Luxtronik 2.1 heat pumps (Alpha Innotec, Novelan and other ait-deutschland brands) through the Smart Home Interface (SHI), which is Modbus TCP on port 502. This integration is strictly read only: it only ever sends read function codes (2 to 4) and never writes a value to the controller.

## Supported Devices

- Luxtronik 2.1 controllers with the Smart Home Interface, firmware 3.90.1 or later (the minimum the SHI manual names)
- Values added in firmware 3.92 (thermal energy, operating hours, pump outputs, Smart Grid signals, the outside average, heat source and maximum and calculated flow temperatures, extra hot water state, levels, heating and hot water locks, overall smart home control, circulation and extra hot water requests) are read only when the controller runs 3.92 or later; the room temperature setpoint needs 3.92.1

## Safety and Prerequisites

Reading does not change how the heat pump runs. Reads change no controller state, and every SHI control starts at "no influence" and only takes effect once a client writes it, which this integration never does. The risks come from enabling the interface itself, so read this section before switching it on.

### Enabling the Smart Home Interface

The SHI is switched on in the service menu: SERVICE > Systemsteuerung > Konnektivität > Smart-Home-Interface. AIT reserves controller settings for authorised service personnel, so if you do this yourself, change only this one setting, apart from Smart Grid as described below.

The Luxtronik manual (Teil 2, p. 47) asks to deactivate Smart Grid (SG-Ready) while the SHI is used, because both functions can influence each other. Note the current Smart Grid setting before you start. If the installation relies on SG-Ready (for example a PV system or an energy manager on the SG contacts), decide which of the two to use before enabling the SHI.

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
| `PollingInterval` | TimeSpan | 30 seconds | Time between reads; values below 10 seconds are raised to 10 seconds, values above 1 hour lowered to 1 hour |

A configuration change restarts the connection.

## Status

| `Status` | When | `StatusMessage` |
|----------|------|-----------------|
| Stopped | No host address configured | "No host address configured" |
| Starting | Connecting, no error yet | "Connecting..." |
| Running | Connected and polling | "Heat pump error N" when the controller reports error N, otherwise empty |
| Error | The connection failed (stays set while reconnecting), or the configuration is invalid | The last error, or the configuration error |

A controller error does not change `Status`: the connection is healthy, so it stays Running and the error number is shown in `StatusMessage`, which the widget displays. The status is refreshed every second, independent of the polling interval.

## State Properties

Temperatures are °C, temperature offsets K, power W, energy Wh, durations minutes and operating hours h. Properties are read only; a local change is replaced by the controller value on the next poll.

The model is grouped by function. Measured temperatures are child sensors (`ITemperatureSensor`) and pump outputs are child switches (`ISwitchState`, read only), each titled after what it measures or drives, such as "Return temperature" or "Hot water loading pump (BUP)"; targets, limits, states, locks, energy and operating hours are plain values.

`Heating` and `HotWater` always exist. `Cooling`, `Pool`, `Solar`, `RoomControl` and `MixingCircuit1` to `MixingCircuit3` exist while their function is active on the controller (discrete inputs 10000 to 10011, shown under `Functions`); a mixing circuit exists while its heating or its cooling is active. The flags follow the operating modes for heating ("Aus" clears it), hot water ("Aus" clears it), cooling and pool (set only in "Automatisch") and the mixing circuits; solar and the room control unit reflect the configuration. The flags are read on every connect and polled with the other values, and when a flag other than heating or hot water changes, the device reconnects and discovers its values again, so a function appears or disappears within a few polling intervals. Heating and hot water always exist and gate no value, so their flags never cause a reconnect.

On every connect the device also reads the firmware version and skips values the firmware does not provide. Values the controller reports as not available (0x7FFF or 0x7FFFFFFF) are shown empty.

| Group | Properties |
|-------|-----------|
| Device | `IsConnected`, `Status`, `StatusMessage`, `LastUpdated`, `SoftwareVersion` (firmware, such as "3.92.3"), `Power`, `TotalConsumedEnergy`, `ThermalPower`, `TotalProducedThermalEnergy` |
| `OperatingStatus` | `HeatPumpStatus` (running compressors and auxiliary heaters), `IsCompressorRunning`, `IsAuxiliaryHeaterRunning`, `OperatingState` (heating, hot water, defrost, ...), `ErrorNumber`, `BufferType`, `MinimumOffTime` (cycling lock), `MinimumRunTime`, `TotalOperatingHours`, `BrinePump` (VBO) |
| `Temperatures` | Sensors of the heat pump itself: `Flow`, `Return`, `Outside`, `OutsideAverage` (24 hours), `HeatSourceInlet`, `HeatSourceOutlet`; `MaximumFlowTemperature` |
| `Energy` | `ThermalPower`, `ElectricalPower`, `MinimumPredictedElectricalPower`, `TotalElectricalEnergy`, `TotalThermalEnergy` |
| `SmartGrid` | `IsEvu1Active`, `IsEvu2Active`, `State` (Locked, Reduced, Normal, Increased) |
| `PowerConsumptionLimit` | `Mode` (none, soft, hard), `Limit` |
| `Functions` | `IsHeatingEnabled`, `IsHotWaterEnabled`, `IsCoolingEnabled`, `IsPoolEnabled` and `IsMixingCircuit1HeatingEnabled` to `IsMixingCircuit3CoolingEnabled` (the operating mode is switched on), `IsSolarConfigured`, `IsRoomControlUnitConfigured` (see above) |
| `Heating` | `Status` (Off when the operating mode is switched off, NoRequest, Requested, Running), `ReturnTarget`, `MinimumReturnTarget`, `ReturnLimit` (maximum return target), `LimitTemperature` (above it heating demand counts as optional), `CalculatedFlowTemperature`, `IsLocked`, `TotalOperatingHours`, `TotalElectricalEnergy`, `TotalThermalEnergy`; `ExternalReturn` sensor (separation or multifunction tank), `CirculationPump` (HUP), `SmartHomeControl`, `OverallSmartHomeControl` |
| `HotWater` | `Status`, `Target`, `MinimumTarget`, `MaximumTarget`, `LimitTemperature` (below it a soft power limit is ignored), `IsLocked`, `IsCirculationRequested`, `TotalOperatingHours`, `TotalElectricalEnergy`, `TotalThermalEnergy`; `Temperature` sensor, `LoadingPump` (BUP), `CirculationPump` (ZIP), `SmartHomeControl`, `ExtraHotWater` (`IsRequested`, `Target`, `Duration`, `RemainingDuration`) |
| `Cooling` | `Status`, `IsReleased`, `IsLocked`, `TotalOperatingHours` (active cooling), `TotalElectricalEnergy`, `TotalThermalEnergy` |
| `Pool` | `Status`, `IsLocked`, `TotalOperatingHours`, `TotalElectricalEnergy`, `TotalThermalEnergy` |
| `Solar` | `TotalOperatingHours` |
| `RoomControl` | `Temperature` sensor, `TemperatureSetpoint` |
| `MixingCircuit1` to `MixingCircuit3` | `Target`; `MinimumTarget` and `MaximumTarget` (read while the circuit heats); `Temperature` flow sensor, `Pump` (FP1 to FP3), `HeatingSmartHomeControl` (read while the circuit heats), `CoolingSmartHomeControl` (read while the circuit cools) |

The `SmartHomeControl` blocks show the setpoint configuration a smart home system sends over the SHI (the controller lists it under "Empfangene Daten"): `Mode` (no influence, setpoint, offset, level), `Setpoint`, `Offset` and `Level`. The cooling blocks have no `Level`, and `OverallSmartHomeControl` has `Mode` (individual, offset, level), `Offset` and `Level`. With no writing client they read "no influence" ("individual" for the overall block), and locks and requests read off.

Power and energy values are marked as estimated. The controller calculates them from its own data rather than measuring them (its energy monitor "ist keine geeichte Messeinrichtung"), and the SHI reports them in steps of 100 W and 100 Wh. Use an external meter for exact consumption.

`SmartGrid.IsEvu1Active` and `IsEvu2Active` are the raw states of the EVU inputs, as the controller lists them under its inputs ("EVU: Ein" reads `true`). With Smart Grid switched off, EVU1 is the utility lock contact: it is on while the heat pump is released and off during a lock time ("Aus = Sperrzeit"). `SmartGrid.State` follows the SHI manual's Smart Grid table and is only meaningful while Smart Grid is switched on in the controller; the SHI does not report that setting.

## Interfaces

- `IPowerSensor`: electrical power and consumed energy
- `IThermalPowerSensor`: thermal power and produced thermal energy (thermal energy needs firmware 3.92)
- `ITemperatureSensor`: each measured temperature is its own child sensor
- `ISwitchState`: each pump output is its own child switch (read only)
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
  "pollingInterval": "00:00:30"
}
```

## Troubleshooting

- **Connection refused or timeouts:** the SHI is not enabled on the controller, or a firewall blocks port 502.
- **Values stay empty:** the value needs firmware 3.92 (3.92.1 for the room temperature setpoint). A mixing circuit's heating values stay empty while it only cools, and its cooling values while it only heats; they are cleared when that flag clears. If the controller rejects the read of its active functions, the device creates every function and relies on the not-available values instead. A temporary rejection of that read fails the connect, which is retried.
- **Controller error 816:** more than one client writes the same SHI data point, and the SHI stays disabled while the error persists. HomeBlaze does not write; check the other clients.
- **SHI "Standby" on the controller:** no requests for 10 minutes; check that HomeBlaze is running and connected.
- **Hot water or a mixing circuit temperature shows exactly 75.0 °C, or the external return 5.0 °C:** the controller reports these substitute values when the sensor is faulty. They are passed through unfiltered; check the sensor.
- **`SmartGrid.State` stays empty:** the Smart Grid inputs need firmware 3.92, and EVU2 (input 10361) is only provided by air heat pumps and propane brine heat pumps; on other models `IsEvu2Active` stays empty and only `IsEvu1Active` has a value.
- **A function disappeared:** its operating mode was switched off, or, for solar and the room control unit, it is no longer configured (see State Properties); it reappears when switched on again.

## Modbus Register Map

Addresses are raw Modbus addresses (no +1). Input registers are read with function code 4, holding registers with 3, discrete inputs with 2. Input, holding and discrete input addresses all start at 10000 in separate address spaces. 32-bit values are high word first.

| Group | Address space | Addresses |
|-------|---------------|-----------|
| Firmware | Input | 10400 to 10402 (major, minor, patch) |
| `Functions` | Discrete input | 10000 to 10011 |
| `OperatingStatus` | Input | 10000, 10002, 10201 to 10204, 10404 (3.92), 10350 (3.92) |
| `Temperatures` | Input | 10100, 10105, 10108, 10109 to 10112 (3.92) |
| `Energy` | Input | 10300 to 10302, 10310, 10320 (3.92) |
| `SmartGrid` | Input | 10360, 10361 (3.92) |
| `PowerConsumptionLimit` | Holding | 10040, 10041 |
| `Heating` | Input | 10003, 10101 to 10104, 10107, 10113 (3.92), 10312, 10322 (3.92), 10354 (3.92), 10406 (3.92) |
| `Heating` | Holding | 10000 to 10003 (level 3.92), 10050 (3.92), 10065 to 10067 (3.92) |
| `HotWater` | Input | 10004, 10120 to 10124, 10314, 10324 (3.92), 10355, 10356 (3.92), 10408 (3.92), 10500 to 10502 (3.92) |
| `HotWater` | Holding | 10005 to 10008 (level 3.92), 10051 (3.92), 10070, 10071 (3.92) |
| `Cooling` | Input | 10006, 10207, 10316, 10326 (3.92), 10410 (3.92) |
| `Cooling` | Holding | 10052 |
| `Pool` | Input | 10007, 10318, 10328 (3.92), 10412 (3.92) |
| `Pool` | Holding | 10053 |
| `Solar` | Input | 10416 (3.92) |
| `RoomControl` | Input / Holding | 10106 / 10060 (3.92.1) |
| `MixingCircuit1` to `3` | Input | 10140 to 10143, 10150 to 10153, 10160 to 10163; pumps 10351 to 10353 (3.92) |
| `MixingCircuit1` to `3` | Holding | heating 10010 to 10013, 10020 to 10023, 10030 to 10033 (level 3.92); cooling 10015 to 10017, 10025 to 10027, 10035 to 10037 |

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
