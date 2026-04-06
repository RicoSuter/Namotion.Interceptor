---
title: Wallbox EV Charger
icon: EvStation
---

# Wallbox EV Charger

The Wallbox integration supports Pulsar MAX, Pulsar Plus, Commander 2, Quasar, and other Wallbox EV chargers via the Wallbox cloud API.

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | string | "" | Display name |
| `Email` | string | "" | Wallbox cloud account email |
| `Password` | string | "" | Account password (stored encrypted) |
| `SerialNumber` | string | "" | Charger serial number |
| `PollingInterval` | TimeSpan | 90 seconds | API polling interval (min 60s recommended) |
| `RetryInterval` | TimeSpan | 60 seconds | Delay after connection error |

## Operations

| Operation | Description |
|-----------|-------------|
| `Pause Charging` | Pause the current charging session |
| `Resume Charging` | Resume a paused charging session |
| `Lock` | Lock the charger |
| `Unlock` | Unlock the charger |
| `Set Max Charging Current` | Set maximum charging current (6-32A) |
| `Set Energy Price` | Set energy price per kWh |
| `Set ICP Max Current` | Set grid maximum current |
| `Set Eco-Smart Mode` | Set solar charging mode (Disabled, Eco, Full Solar) |
| `Reboot` | Reboot the charger (requires confirmation) |
| `Update Firmware` | Trigger firmware update (requires confirmation) |
| `Toggle/Lock/Unlock Charge Port` | Control the vehicle charge port lock |

## State Properties

| Property | Unit | Description |
|----------|------|-------------|
| `IsConnected` | - | Cloud API connection status |
| `ChargerStatus` | - | Detailed charger status (Ready, Charging, Paused, etc.) |
| `IsPluggedIn` | - | Whether a vehicle is plugged in |
| `IsCharging` | - | Whether actively charging |
| `ChargingPower` | Watt | Current charging power |
| `ChargingSpeed` | Ampere | Actual charging current |
| `MaxChargingCurrent` | Ampere | Configured maximum current |
| `ChargeLevel` | Percent | Vehicle battery level (when car reports SoC) |
| `IsLocked` | - | Charger lock state |
| `AddedEnergy` | WattHour | Energy added in current session |
| `AddedRange` | km | Range added in current session |
| `AddedGreenEnergy` | WattHour | Solar energy in current session |
| `ChargingTime` | TimeSpan | Current session duration |
| `SessionCost` | Currency | Current session cost |
| `TotalEnergyConsumed` | WattHour | Cumulative total energy |
| `EnergyPrice` | Currency/kWh | Configured energy price |
| `EcoSmartEnabled` | - | Whether Eco-Smart is enabled |
| `EcoSmartMode` | - | Current Eco-Smart mode |
| `SoftwareVersion` | - | Charger firmware version |
| `AvailableSoftwareUpdate` | - | Available firmware update |
| `Model` | - | Charger model name |
| `ProductCode` | - | Part number / SKU |

## Interfaces

- `IVehicleCharger` - EV charger state and control (pause/resume)
- `IConnectionState` - Cloud connectivity
- `ISoftwareState` - Firmware version tracking
- `IDeviceInfo` - Hardware identity (manufacturer, model, serial, product code)
- `IMonitoredService` - Service lifecycle status

## JSON Configuration Example

```json
{
  "$type": "Namotion.Devices.Wallbox.WallboxCharger",
  "name": "Garage Charger",
  "email": "user@example.com",
  "password": "secret",
  "serialNumber": "910037",
  "pollingInterval": "00:01:30",
  "retryInterval": "00:01:00"
}
```

## Cloud API

The integration uses the Wallbox cloud API (`api.wall-box.com`) with JWT authentication. The API has rate limiting -- polling intervals below 60 seconds are not recommended.

| Endpoint | Purpose |
|----------|---------|
| `GET /users/signin` | Authentication (JWT) |
| `GET /chargers/status/{id}` | Charger status and configuration |
| `PUT /v2/charger/{id}` | Lock/unlock, set current |
| `POST /v3/chargers/{id}/remote-action` | Pause, resume, reboot |
| `PUT /v4/chargers/{id}/eco-smart` | Solar charging mode |
| `GET /v4/groups/{id}/charger-charging-sessions` | Historical sessions |
