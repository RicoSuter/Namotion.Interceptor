---
title: Tesla
icon: DirectionsCar
---

# Tesla

The Tesla integration reads all Tesla vehicles of an account through the unofficial Tesla Owner API: charging, battery, climate, location, doors and windows, tires, media, software updates and the vehicle specification. It is read only: the only call that changes anything is the `Wake Up` operation. It never wakes a vehicle on its own and is built so that polling does not keep a vehicle awake, because both drain the battery. It needs no Tesla developer application and no subscription, only a refresh token of your Tesla account. See [Known Limitations](#known-limitations) for what it cannot do.

## Setup

1. Create a refresh token of your Tesla account with a token tool, on any device: [tesla_auth](https://github.com/adriankumpf/tesla_auth/releases) (Windows, macOS, Linux) or the app Auth app for Tesla (iOS). Use a token of its own: Tesla replaces a refresh token with every refresh, so sharing one with TeslaMate or another tool makes the tools log each other out.
2. In HomeBlaze, create a **Tesla account** (group **Vehicles**).
3. Paste the refresh token and select **Connect**. HomeBlaze refreshes the token, which makes Tesla issue a new one, and lists the vehicles of the account with their VIN and state (online or asleep). The list shows no model, because Tesla's vehicle list does not return one.
4. Select **Save**. The new token is the only valid one from now on and is stored with the account. Cancelling the dialog after **Connect**, or closing it while it connects, discards the new token and invalidates the one you pasted, so create a new token with your token tool and start again.

The account has to live in a storage file, such as `Devices/Tesla.json`, and not in the root configuration. HomeBlaze writes the rotated refresh token back through the storage file that holds the account; an account in the root configuration keeps rotated tokens in memory only, logs a warning, and needs a new token after every restart.

### Advanced: sign in with Tesla

When no token tool is at hand, open **Advanced: sign in with Tesla** in the setup. It shows a Tesla sign in address with PKCE. Open it in a browser and sign in. Tesla then redirects to an address that starts with `tesla://auth/callback`, which a browser cannot open. In a desktop browser, open the developer tools before you sign in, enable **Preserve log** in the network tab, and copy the address of the failed request from there. Paste it into the field below the sign in address and select **Connect with the pasted address**. The address holds a single-use code and is cleared after use; if the sign in fails, open the new sign in address and sign in again.

### Re-authentication

Refresh tokens last about three months and rotate on every refresh, so a running account keeps its token valid. When Tesla rejects the token (`login_required` or `invalid_grant`, for example after a password change or when another tool used the token), `IsAuthenticated` becomes `false`, the status is `Error` with the message "Re-authentication required", and polling stops until the configuration changes. Open the edit dialog of the account, expand **Replace token**, connect with a new token and save.

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | string | "" | Display name. Empty shows the email of the account. |
| `Provider` | enum | `OwnerApi` | Where the data comes from. Only `OwnerApi` exists in this version. |
| `RefreshToken` | string | "" | Refresh token of the account (stored as a secret). Tesla rotates it with every refresh and HomeBlaze stores the new one. |
| `StatePollingInterval` | TimeSpan | 1 minute | How often the vehicle list with state and cached data is read. |
| `ActivePollingInterval` | TimeSpan | 1 minute | How often the data of an awake vehicle is read while it charges, drives or has a user present. |
| `IdlePollingInterval` | TimeSpan | 5 minutes | How often the data of an awake vehicle is read while it is idle. |
| `IdleTimeout` | TimeSpan | 15 minutes | How long an awake vehicle that could sleep is read before reading pauses. |
| `SleepWindow` | TimeSpan | 21 minutes | How long reading pauses so that an idle vehicle can fall asleep. |
| `RetryInterval` | TimeSpan | 1 minute | Wait after a failed poll. |

Every interval is at least 10 seconds. The account restarts its loop when the configuration is applied; the access token is kept, so a restart does not refresh again.

```json
{
  "$type": "Namotion.Devices.Tesla.TeslaAccount",
  "name": "Tesla",
  "provider": "OwnerApi",
  "refreshToken": "",
  "statePollingInterval": "00:01:00",
  "activePollingInterval": "00:01:00",
  "idlePollingInterval": "00:05:00",
  "idleTimeout": "00:15:00",
  "sleepWindow": "00:21:00",
  "retryInterval": "00:01:00"
}
```

The sample file `Devices/Tesla.json` has no token and shows the status "Missing refresh token" until you connect.

## Status

| `Status` | When | `StatusMessage` |
|----------|------|-----------------|
| Stopped | No refresh token configured | "Missing refresh token" |
| Starting | First poll | |
| Running | Polling | Empty, or the failure of the last vehicle that could not be read or applied; the other vehicles are not affected |
| Error | A poll failed, Tesla limits the requests, or re-authentication is required | The cause, or "Re-authentication required" |

## Subjects

```
TeslaAccount                                   tokens and polling
└─ Vehicles[VIN]: TeslaVehicle                 identity, shared interfaces, Wake Up
   ├─ Charge: TeslaChargeState
   ├─ Climate: TeslaClimateState
   │  └─ SeatHeaters: TeslaSeatHeaters
   ├─ Drive: TeslaDriveState
   │  └─ Navigation: TeslaNavigation
   ├─ Body: TeslaVehicleBody
   │  └─ FrontLeftTire, FrontRightTire, RearLeftTire, RearRightTire: TeslaTire
   ├─ Media: TeslaMedia
   ├─ SoftwareUpdate: TeslaSoftwareUpdate
   ├─ SpeedLimit: TeslaSpeedLimitMode
   ├─ Specification: TeslaVehicleSpecification
   └─ DisplaySettings: TeslaDisplaySettings
```

Vehicles are discovered, not configured, and keyed by VIN. A vehicle that appears in the account is added, one that disappears is removed, and a known vehicle is updated in place, so paths and history stay stable.

All readings are `null` until the vehicle reported them. Percentages are fractions from 0 to 1, distances are in km, speeds in km/h, tire pressures in bar, temperatures in °C, power in W, energy in Wh, currents in A and times of day or durations are `TimeSpan`. Values that Tesla reports as placeholders, such as a charger voltage of 0 to 2 V when not charging, `<invalid>`, a blank software version or an empty vehicle name, are `null`. Unknown enum values are `Unknown` and logged once.

| Subject | Interfaces |
|---------|------------|
| `TeslaAccount` | `IConfigurable`, `IMonitoredService`, `IAuthenticatedSubject`, `ITitleProvider`, `IIconProvider` |
| `TeslaVehicle` | `IElectricVehicle` (`IVehicleState`, `IVehicleChargingState` with `IChargingState`, `IBatteryState`), `IVehicleClimateState`, `IDoorLockState`, `IDeviceInfo`, `ISoftwareState`, `IConnectionState`, `ILastUpdatedProvider`, `ITitleProvider`, `IIconProvider` |
| `TeslaMedia` | `IMediaTrackState`, `IVolumeState` |

### TeslaAccount

| Property | Description |
|----------|-------------|
| `Status`, `StatusMessage` | See [Status](#status) |
| `IsAuthenticated` | Whether Tesla accepted the refresh token |
| `AuthenticatedUser` | Email of the account |
| `AuthenticatedAt` | Time of the last successful refresh |
| `Vehicles` | The vehicles, keyed by VIN |

### TeslaVehicle

The properties of the shared interfaces read the child subjects, so the vehicle can be used like any other electric vehicle, for example on a page or by an automation, without knowing the Tesla children.

| Property | Unit | Description |
|----------|------|-------------|
| `Vin`, `SerialNumber` | | Vehicle identification number |
| `DisplayName` | | Name in the Tesla account, from the vehicle list |
| `VehicleName` | | Name that is set in the vehicle |
| `VehicleState` | | `Online`, `Asleep`, `Offline` or `Unknown` |
| `IsAwake` | | `true` when online, `false` when asleep or offline, `null` when unknown; discrete, so history records every wake and sleep |
| `IsConnected` | | `true` when online or asleep, `false` when offline: an asleep vehicle is still reachable through Tesla's servers |
| `CommandSigning` | | Whether the vehicle requires signed commands (`Off`, `Allowed`, `Required`) |
| `IsInService` | | Whether the vehicle is in service at Tesla |
| `LastUpdated` | | Time of the newest data, from the data itself and not from the read time of the poll |
| `BatteryLevel` | Percent | State of charge |
| `Range` | km | Estimated range |
| `TotalDrivenDistance` | km | Odometer |
| `Location` | | Coordinate of the vehicle |
| `IsPluggedIn`, `IsCharging` | | Charging cable and charging state |
| `ChargingPower` | W | Current charging power |
| `MaximumChargingPower` | W | Power at the highest current that the charger offers and the vehicle can request; `null` when the vehicle does not charge. The phases come from the measured power, because Tesla reports 2 phases while charging on 3-phase AC. |
| `ChargeLimit` | Percent | Battery level at which charging stops |
| `RemainingChargingTime` | TimeSpan | Time until the charge limit is reached |
| `IsClimateOn`, `InsideTemperature`, `OutsideTemperature` | °C | Climate state |
| `LockState` | | Door lock state |
| `Manufacturer`, `Model`, `ProductCode` | | `Tesla`, the model name from the specification (Model 3, Model Y, Model S, Model X, Cybertruck) and the trim badging. `Model` and `ProductCode` are `null` until the first read of the vehicle data, because the cached data does not carry the specification. |
| `SoftwareVersion`, `AvailableSoftwareUpdate` | | Installed version and the version of an available update |

The title is the name set in the vehicle, else the display name of the account, else the model with the last six characters of the VIN.

### Charge

| Property | Unit | Description |
|----------|------|-------------|
| `ChargingState` | | `Disconnected`, `NoPower`, `Starting`, `Charging`, `Complete`, `Stopped`, `Calibrating` |
| `IsPluggedIn`, `IsCharging` | | Derived from `ChargingState` |
| `BatteryLevel`, `UsableBatteryLevel` | Percent | State of charge and the usable part of it |
| `ChargeLimit`, `MinimumChargeLimit`, `MaximumChargeLimit`, `StandardChargeLimit` | Percent | Charge limit with its bounds and the standard limit |
| `Range`, `IdealRange`, `EstimatedRange` | km | Rated, ideal and estimated range |
| `ChargingPower` | W | Current charging power |
| `ChargingCurrent`, `RequestedChargingCurrent`, `MaximumRequestedChargingCurrent`, `PilotCurrent`, `ChargingCurrentSetting` | A | Actual, requested, maximum requested and charger offered current, and the current setting of the vehicle |
| `ChargerVoltage` | V | Charger voltage, `null` when not charging |
| `ChargerPhases` | | Number of phases as Tesla reports it |
| `AddedEnergy` | Wh | Energy added in the session |
| `AddedRange`, `AddedIdealRange` | km | Range added in the session |
| `ChargeRate` | km/h | Range added per hour |
| `RemainingChargingTime` | TimeSpan | Time to the charge limit |
| `IsFastChargerPresent`, `FastChargerBrand`, `FastChargerType` | | Fast charger information |
| `ChargeCable` | | Cable type |
| `IsChargePortDoorOpen`, `ChargePortLatch`, `ChargePortColor`, `IsChargePortColdWeatherMode` | | Charge port |
| `IsBatteryHeaterOn` | | Battery heater |
| `ScheduledChargingMode`, `IsScheduledChargingPending`, `ScheduledChargingStartTime`, `ScheduledDepartureTime` | | Scheduled charging and departure, times as time of day |
| `IsOffPeakChargingEnabled`, `OffPeakChargingDays`, `OffPeakChargingEndTime` | | Off-peak charging |
| `IsPreconditioningEnabled`, `PreconditioningDays` | | Preconditioning before departure |
| `IsTripCharging`, `IsSuperchargerSessionFromTripPlanner` | | Trip charging |
| `IsChargeEnabled`, `IsChargeEnabledByUser`, `IsNotEnoughPowerToHeat`, `MaximumRangeChargeCounter` | | Further flags of the charge state |
| `EstimatedBatteryCapacity` | Wh | Derived estimate of the battery capacity from a charging session, `null` for short sessions |

### Climate

| Property | Unit | Description |
|----------|------|-------------|
| `IsClimateOn`, `IsAutoConditioningOn`, `IsPreconditioning` | | Climate state |
| `InsideTemperature`, `OutsideTemperature` | °C | Temperatures |
| `DriverTemperatureSetting`, `PassengerTemperatureSetting`, `MinimumTemperatureSetting`, `MaximumTemperatureSetting` | °C | Set temperatures and their bounds |
| `FanStatus`, `LeftTemperatureDirection`, `RightTemperatureDirection`, `DefrostMode` | | Fan, airflow direction and defrost mode |
| `HvacAutoRequest`, `ClimateKeeperMode` | | Auto request and climate keeper (`Off`, `On`, `Dog`, `Camp`) |
| `IsFrontDefrosterOn`, `IsRearDefrosterOn`, `IsSideMirrorHeaterOn`, `IsWiperBladeHeaterOn` | | Heaters and defrosters |
| `IsBatteryHeaterOn`, `IsBatteryHeaterWithoutPower`, `IsRemoteHeaterControlEnabled` | | Battery heater |
| `CabinOverheatProtection`, `CabinOverheatProtectionTemperature`, `IsCabinOverheatProtectionAllowed`, `IsFanOnlyCabinOverheatProtectionSupported`, `IsCabinOverheatProtectionCooling` | | Cabin overheat protection and its activation temperature (`Low`, `Medium`, `High`) |
| `SeatHeaters` | | The `TeslaSeatHeaters` child |

`TeslaSeatHeaters` has the levels of the front left, front right, rear left, rear center and rear right seats (`FrontLeftLevel` and so on), the automatic seat climate of the front seats, and the steering wheel heater (`IsSteeringWheelHeaterOn`, `SteeringWheelHeatLevel`, `IsAutoSteeringWheelHeatOn`). Sides are physical, not relative to the driver.

### Drive

| Property | Unit | Description |
|----------|------|-------------|
| `Location`, `NativeLocation`, `NativeLocationType`, `IsNativeLocationSupported` | | Location, and the location in the coordinate system of `NativeLocationType` (`Wgs` or `Gcj`), which differs from `Location` only in some regions |
| `Heading` | degrees | Heading |
| `GpsTime` | | Time of the last location |
| `Speed` | km/h | Speed, which Tesla omits while the vehicle is parked |
| `Power` | W | Drive power, negative while the vehicle regenerates |
| `ShiftState`, `IsDriving` | | `Park`, `Reverse`, `Neutral`, `Drive`, and whether the vehicle is in drive, reverse or neutral |
| `IsHomelinkNearby` | | Whether a Homelink device is nearby |
| `Navigation` | | The `TeslaNavigation` child |

`TeslaNavigation` has `Destination`, `DestinationLocation`, `TimeToArrival`, `DistanceToArrival` (km), `EnergyAtArrival` (percent) and `TrafficDelay` of the active route.

### Body

| Property | Description |
|----------|-------------|
| `LockState` | Door lock state |
| `IsDriverFrontDoorOpen`, `IsDriverRearDoorOpen`, `IsPassengerFrontDoorOpen`, `IsPassengerRearDoorOpen` | Doors, named after Tesla's sides as the vehicle reports them |
| `IsFrunkOpen`, `IsTrunkOpen` | Frunk and trunk |
| `IsDriverFrontWindowOpen`, `IsPassengerFrontWindowOpen`, `IsDriverRearWindowOpen`, `IsPassengerRearWindowOpen` | Windows |
| `IsAnyDoorOpen`, `IsAnyWindowOpen` | Derived: any of the four is open |
| `IsUserPresent` | Someone is in the vehicle |
| `IsSentryModeOn`, `IsSentryModeAvailable` | Sentry mode |
| `IsValetModeOn`, `IsValetPinNeeded` | Valet mode |
| `IsRemoteStartActive`, `IsRemoteStartEnabled`, `IsRemoteStartSupported` | Remote start |
| `IsInServiceMode`, `IsInServiceModePlus` | Service mode |
| `DashcamState`, `IsDashcamClipSaveAvailable`, `IsWebcamAvailable` | Dashcam and webcam |
| `CenterDisplayState`, `AutoparkState`, `SantaMode`, `SelfTestProgress`, `IsSelfTestRequested` | Further vehicle state |
| `FrontLeftTire`, `FrontRightTire`, `RearLeftTire`, `RearRightTire` | `TeslaTire` children |

`TeslaTire` has `Pressure` and `RecommendedPressure` (bar), `HasSoftWarning`, `HasHardWarning` and `LastSeen`.

### Other children

| Subject | Properties |
|---------|------------|
| `TeslaMedia` | `PlaybackStatus`, `Source`, `CurrentTrackTitle`, `CurrentTrackArtist`, `CurrentTrackAlbum`, `Station`, `CurrentTrackDuration`, `CurrentTrackPosition`, `AudioVolume`, `AudioVolumeMaximum`, `AudioVolumeIncrement`, `IsRemoteControlEnabled`, and `Volume` as the fraction of the maximum |
| `TeslaSoftwareUpdate` | `Status`, `Version`, `DownloadProgress`, `InstallProgress` (percent), `ExpectedDuration` |
| `TeslaSpeedLimitMode` | `IsActive`, `CurrentLimit`, `MinimumLimit`, `MaximumLimit` (km/h), `IsPinCodeSet` |
| `TeslaVehicleSpecification` | `CarType`, `CarSpecialType`, `TrimBadging`, colors, trims, wheel type, roof, spoiler, seats, drive unit, charge port type, lamps, package and version numbers, `UtcOffset`, and the feature flags `HasSunRoof`, `HasSeatCooling`, `HasAirSuspension`, `IsRightHandDrive`, `IsEuVehicle`, `CanActuateTrunks` and others, from `vehicle_config` |
| `TeslaDisplaySettings` | The units of the vehicle display: `ChargeRateUnits`, `DistanceUnits`, `RangeDisplay`, `TemperatureUnits`, `TirePressureUnits`, `ShowsRangeUnits`, `Is24HourTime`. They describe the display of the vehicle; HomeBlaze values are always in the units above. |

## Operations

| Operation | Subject | Description |
|-----------|---------|-------------|
| `Wake Up` | `TeslaVehicle` | Wakes the vehicle, waits up to 2 minutes until it is online and reads its data. Enabled only while the vehicle is asleep or offline. |

`Wake Up` is the only call that wakes a vehicle. While it waits, polling of the account pauses. Waking costs battery, and the vehicle falls asleep by itself again after the sleep window (see below).

## Polling and Sleep

The account makes three kinds of calls, and only one can wake a vehicle:

| Call | Wakes the vehicle | When |
|------|-------------------|------|
| `GET /api/1/products` | No | Every `StatePollingInterval`: discovery, `VehicleState`, `CommandSigning`, `IsInService` and the cached data of every vehicle |
| `GET /api/1/vehicles/{id}/vehicle_data` | Not an asleep vehicle (Tesla answers 408), but an awake one stays awake while it is read | Only while the vehicle is online, see below |
| `POST /api/1/vehicles/{id}/wake_up` | Yes | Only from the `Wake Up` operation |

`vehicle_data` is read only for a vehicle that `products` reports as online. An online vehicle is read every `ActivePollingInterval` while it charges, drives or has a user present, else every `IdlePollingInterval`. A vehicle that nothing keeps awake (not charging, not driving, nobody present, climate and sentry mode off) is read for `IdleTimeout`, then reading pauses for `SleepWindow` so that the vehicle can fall asleep, as TeslaMate does. After the window the vehicle is read again when it is still online, and sooner when the cached data shows activity. An asleep or offline vehicle is never read and keeps its last values.

The cached data (`cached_data`) is a snapshot that Tesla's servers keep for each vehicle and return in the vehicle list, decoded from Tesla's protobuf `VehicleData` message. It is applied on every poll of the list, also while the vehicle is awake, so values appear within a minute of the first poll even for a vehicle that has been asleep since HomeBlaze started, as far as Tesla's snapshot holds them. Both sources, the cached data and the JSON of `vehicle_data`, fill the same properties with the same conversion, and every section carries Tesla's timestamp: a snapshot older than the data already read never overwrites it.

A failure of one vehicle does not stop the others. Tesla's request limit (429) pauses polling for the `Retry-After` time, 5 minutes if it is missing, and `403 account disabled: EXCEEDED_LIMIT` pauses for 15 minutes. A server error or a network error waits `RetryInterval`. A 401 refreshes the access token once and repeats the call. The log of the account records every wake with its reason at information level and every `vehicle_data` read at debug level, so that a vehicle that does not sleep can be traced.

## Known Limitations

- **Read only.** The integration cannot start or stop charging, set the charge limit, open the charge port, lock or unlock, or control the climate. Since 2024, Tesla vehicles except the pre-2021 Model S and X accept only commands signed with a key that the vehicle trusts, and reject unsigned commands. `CommandSigning` shows what the vehicle requires. A key can only be enrolled with a Fleet API developer application, or over Bluetooth with a tap of the key card, and the Owner API does not forward signed commands. Only `Wake Up` is available.
- **Phase 2: Teslemetry or Tessie.** The design prepares a second provider, Teslemetry or Tessie, which are paid services that send the signed commands for you. Provider is configuration already (`Provider`), and both services serve the same paths and payloads as Tesla's Fleet API. It is not implemented yet.
- **Phase 3: own command signing.** Signing commands in HomeBlaze itself would need a key enrolled over Bluetooth and commands sent over Bluetooth from a device near the vehicle. It is only planned if it turns out to be feasible.
- **The Owner API is unofficial.** It changed twice in 2026 (the login redirect and the vehicle list endpoint), and can break without notice. Tesla can also change what it demands of the clients, as it did with HTTP/2 and TLS 1.3 in June 2026. Updates of this integration may be needed then.
- **The China region is not supported.** A refresh token does not tell its region before the first refresh, and no installation needs it. Only the global region (`auth.tesla.com`, `owner-api.teslamotors.com`) is used.
- **Cached data is not verified.** While a vehicle sleeps, its values come from the snapshot of `cached_data`. Whether Tesla keeps that snapshot filled and current while the vehicle sleeps is not verified; a value may be old, which `LastUpdated` and the section timestamps show. When the snapshot is empty, values appear after the vehicle wakes.
- **The location is state like any other.** It is readable by automations, pages, history and AI agents with access to the vehicle. Restrict access accordingly.
- **One refresh token per application.** Tesla replaces the refresh token on every refresh. Using the same token in HomeBlaze and TeslaMate, or in two HomeBlaze instances, makes the tools log each other out.
- **Connecting rotates the token.** Connecting validates the pasted refresh token by refreshing it, which makes Tesla issue a new token and invalidate the pasted one. Cancelling the dialog after **Connect**, or closing it while it connects, discards the new token, so you have to create a new one with your token tool.
- **The account has to live in a storage file.** Rotated tokens are written back through the storage file that holds the account. In the root configuration they stay in memory only and are lost with a restart. If saving a rotated token fails, HomeBlaze keeps it in memory and retries saving with the next poll.
- **The vehicle list shows no model.** The model appears on the vehicle once its vehicle data was read.

## API Reference

The integration uses two Tesla hosts. The Owner API is unofficial and not documented by Tesla, so the paths below may change without notice. Since June 2026, both hosts require HTTP/2 and TLS 1.3, which the integration uses, and the token request is form-encoded.

| Endpoint | Host | Purpose |
|----------|------|---------|
| `POST /oauth2/v3/token` (`grant_type=refresh_token`) | `auth.tesla.com` | Refreshes the access token and rotates the refresh token (client `ownerapi`, scope `openid email offline_access`) |
| `POST /oauth2/v3/token` (`grant_type=authorization_code`) | `auth.tesla.com` | Exchanges the code of the advanced sign in (PKCE) |
| `GET /oauth2/v3/authorize` | `auth.tesla.com` | Sign in address of the advanced sign in, redirects to `tesla://auth/callback` |
| `GET /api/1/products` | `owner-api.teslamotors.com` | Vehicle list with state, `command_signing`, `in_service` and `cached_data`. Never wakes a vehicle. |
| `GET /api/1/vehicles/{id}/vehicle_data` | `owner-api.teslamotors.com` | Full data of an online vehicle (`charge_state`, `climate_state`, `closures_state`, `drive_state`, `gui_settings`, `location_data`, `vehicle_config`, `vehicle_state`). 408 when the vehicle is asleep. |
| `POST /api/1/vehicles/{id}/wake_up` | `owner-api.teslamotors.com` | Wakes the vehicle, only from the `Wake Up` operation |

The access token lives in memory and is refreshed when it has less than an hour or half of its lifetime left, and after a 401. Tesla's vehicle protobuf schemas (`teslamotors/vehicle-command`, Apache 2.0) are vendored in the library to decode `cached_data`.
