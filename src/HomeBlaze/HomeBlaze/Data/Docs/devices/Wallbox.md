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
| `MaximumChargingPower` | Watt | Maximum charging power |
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
- `IPowerSensor` - Power and energy consumption
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

The integration uses two Wallbox cloud API hosts: `user-api.wall-box.com` for authentication and `api.wall-box.com` for data/control. The API has rate limiting -- polling intervals below 60 seconds are not recommended.

| Endpoint | Host | Purpose |
|----------|------|---------|
| `GET /users/signin` | user-api | Authentication (Basic → JWT) |
| `GET /users/refresh-token` | user-api | Token refresh (Bearer) |
| `GET /v3/chargers/groups` | api | List chargers for account |
| `GET /chargers/status/{id}` | api | Charger status and configuration |
| `PUT /v2/charger/{id}` | api | Lock/unlock, set current |
| `POST /chargers/config/{id}` | api | Set energy price, ICP max current |
| `POST /v3/chargers/{id}/remote-action` | api | Pause, resume, reboot, firmware update |
| `PUT /v4/chargers/{id}/eco-smart` | api | Solar charging mode |
| `GET /v4/groups/{id}/charger-charging-sessions` | api | Historical sessions |
