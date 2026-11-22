---
title: Hyundai Bluelink
icon: DirectionsCar
---

# Hyundai Bluelink

The Bluelink integration reads the Hyundai electric vehicles of a Bluelink account in Europe through the unofficial Bluelink API: battery, range, charging, charge limits, climate, doors, windows, lock, tires, odometer, 12 V battery and parking location. It can start and stop charging, set the charge limits and start and stop the climate. It is careful with the vehicle and the API: polling reads only the status that Hyundai's servers stored and never reaches the vehicle, and only the `Refresh` operation and the commands do. See [Known Limitations](#known-limitations) for what it cannot do.

## Setup

1. In HomeBlaze, create a **Hyundai Bluelink account** (group **Vehicles**).
2. Enter the email and password of your Bluelink account. Enter the PIN of the Bluelink app only when a vehicle needs it: charging and climate commands of newer vehicles (protocol `Ccs2`) need it, older vehicles such as the first Kona Electric do not.
3. Select **Connect**. HomeBlaze registers itself as a device of the account and signs in. A **Connected** card then replaces the form and shows the email and the vehicles with their name, the last six characters of the VIN, the model and the protocol. Vehicles that are not electric are marked as not supported, and a `Ccs2` vehicle is marked when its commands need a PIN that was not entered. **Use different credentials** discards the connection and brings the form back.
4. Create the account, which stores the credentials and the token set of the connection.

Connecting never reaches a vehicle. Leading and trailing spaces of the email and the PIN are removed. The PIN is checked only when one is entered and the account has an electric `Ccs2` vehicle, because five wrong PINs lock PIN entry for a while; a rejected PIN fails the connect. When Hyundai refuses the device registration, HomeBlaze signs in with a random device identity instead, as the reference library does; the account keeps it until Hyundai answers that the device is not registered (4002), and then registers a new device with the next poll.

The account has to live in a storage file, such as `Devices/Bluelink.json`, and not in the root configuration. Hyundai replaces the tokens with every refresh, and HomeBlaze writes the new ones back through the storage file that holds the account before it uses them; an account in the root configuration keeps them in memory only, logs a warning and signs in again after every restart. When saving fails, the account keeps the tokens in memory and retries saving with the next call or poll.

### Re-authentication

Every token refresh replaces the tokens, so a running account keeps them valid. When Hyundai rejects a refresh (an answer from 400 to 499 other than 408, 429 and a rejected app fingerprint), the account signs in once with the stored password and continues. A refresh that fails for another reason, such as a server error, a timeout, 408 or 429, is an ordinary failure that is retried after `RetryInterval`.

When the sign in is rejected as well (wrong email or password, a 4xx answer, or new terms to accept), or no password is stored, `IsAuthenticated` becomes `false`, `IsReauthenticationRequired` becomes `true`, the status is `Error` with a message that starts with "Re-authentication required: " and names the cause, and polling waits until the tokens or the password change. The account never retries a rejected password by itself. Open the edit dialog, expand **Replace credentials**, connect and save.

When Hyundai asks to accept new terms, the message says so: accept them once in the Bluelink app, then connect again.

A poll, `Refresh` or command that runs while the credentials are replaced either completes with the previous credentials or, when those would need a sign in or are rejected, fails with the status `Error` and the message "The credentials were replaced; the next poll uses them."; the next poll uses the new credentials.

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | string | "" | Display name. Empty shows "Hyundai: " with the email, or "Hyundai" without one. |
| `Email` | string | "" | Email of the Bluelink account. |
| `Password` | string | "" | Password (stored as a secret). Used for one automatic sign in when Hyundai rejects a refresh; empty disables it. |
| `Pin` | string | "" | PIN of the Bluelink app (stored as a secret). Only charging and climate commands of `Ccs2` vehicles use it. |
| `Tokens` | string | "" | Token set (stored as a secret), replaced with every refresh. |
| `DeviceId` | string | "" | Device ID registered at setup, or by the first poll when none is stored. |
| `StatusPollingInterval` | TimeSpan | 30 minutes | How often the stored status of every vehicle is read; at least 15 minutes. |
| `DailyRequestLimit` | int | 150 | Vehicle API calls per local day; see [Call budget](#call-budget). |
| `MinimumAuxiliaryBatteryLevel` | decimal | 0.65 | 12 V battery level (0 to 1) below which `Refresh` is refused. |
| `ClimateDuration` | TimeSpan | 10 minutes | How long a started climate runs, in whole minutes from 1 to 30. With another value, starting the climate fails before any call. |
| `RetryInterval` | TimeSpan | 5 minutes | Wait after a failed poll; at least one minute. |

The edit dialog enforces the minimums of `StatusPollingInterval` and `RetryInterval`, and the account uses the minimum when the file holds a shorter interval. In the dialog, `DailyRequestLimit` ranges from 20 to 200 and `ClimateDuration` is a number of minutes from 1 to 30. The account picks up a changed configuration at once when it is applied.

```json
{
  "$type": "Namotion.Devices.Hyundai.BluelinkAccount",
  "name": "Hyundai",
  "email": "",
  "password": "",
  "pin": "",
  "tokens": "",
  "deviceId": "",
  "statusPollingInterval": "00:30:00",
  "dailyRequestLimit": 150,
  "minimumAuxiliaryBatteryLevel": 0.65,
  "climateDuration": "00:10:00",
  "retryInterval": "00:05:00"
}
```

The sample file `Devices/Bluelink.json` has no credentials and shows the status "Missing credentials" until you connect.

## Status

| `Status` | When | `StatusMessage` |
|----------|------|-----------------|
| Stopped | No email or no tokens | "Missing credentials" |
| Starting | First poll | |
| Running | Polling | Empty, or the failure of the last vehicle that could not be read; the other vehicles are not affected. "Polling pauses until midnight to keep the remaining requests for commands." while polling saves the last calls of the day. |
| Error | Re-authentication required, replaced credentials, Hyundai's request limit, the daily request limit, or a failed poll | The cause, for example "Re-authentication required: Hyundai rejected the email or password.", "Hyundai's request limit was reached; polling resumes at 14:32.", "Daily request limit reached." |

After an error, the next poll follows after `RetryInterval`, unless re-authentication is required or a pause applies.

## Subjects

```
BluelinkAccount                          tokens, polling, call budget
└─ Vehicles[VIN]: BluelinkVehicle        identity, odometer, location, 12 V battery, Refresh
   ├─ Charge: BluelinkChargeState        battery, range, charging, charge limits, charging commands
   ├─ Climate: BluelinkClimateState      climate state and commands
   └─ Body: BluelinkVehicleBody          lock, doors, trunk, hood, windows
      └─ FrontLeftTire, FrontRightTire, RearLeftTire, RearRightTire: BluelinkTire
```

Vehicles are discovered, not configured, and keyed by VIN. Only electric vehicles are added. The vehicle list is read at start, after the tokens were replaced, and then once a day: a vehicle that appears is added, one that disappears is removed, and a known vehicle is updated in place, so paths and history stay stable.

All readings are `null` until the vehicle reported them. Percentages are fractions from 0 to 1, distances are in km, temperatures in °C, power in W, tire pressures in bar and durations are `TimeSpan`. Doors and windows are named relative to the driver. Hyundai reports the rear ones as left and right, and the left ones count as the driver side, which assumes a left-hand drive vehicle. Tires keep the physical sides.

| Subject | Interfaces |
|---------|------------|
| `BluelinkAccount` | `IConfigurable`, `IMonitoredService`, `IAuthenticatedSubject`, `ITitleProvider`, `IIconProvider` |
| `BluelinkVehicle` | `IElectricVehicle` (with `IVehicle`), `IDeviceInfo`, `ILastUpdatedProvider`, `ITitleProvider`, `IIconProvider` |
| `BluelinkChargeState` | `IVehicleChargingState` (with `IChargingState`, `IBatteryState`), `IVehicleChargingController`, `ITitleProvider`, `IIconProvider` |
| `BluelinkClimateState` | `IVehicleClimateState`, `IVehicleClimateController`, `ITitleProvider`, `IIconProvider` |
| `BluelinkVehicleBody` | `IDoorLockState`, `ITitleProvider`, `IIconProvider` |

### BluelinkAccount

| Property | Description |
|----------|-------------|
| `Status`, `StatusMessage` | See [Status](#status) |
| `IsAuthenticated` | Whether Hyundai accepted the tokens |
| `IsReauthenticationRequired` | Whether Hyundai rejected the tokens and the stored password; polling waits until the tokens or the password change |
| `AuthenticatedUser` | Email of the account |
| `AuthenticatedAt` | Time of the last successful refresh or sign in |
| `RequestsToday` | Vehicle API calls of the current local day |
| `Vehicles` | The electric vehicles, keyed by VIN |

### BluelinkVehicle

Every value has one path. The vehicle holds its identity, the odometer, the location and the 12 V battery; battery, range and charging are in `Charge`, the climate in `Climate` and the lock in `Body`, for example `Vehicles[VIN]/Charge/BatteryLevel`. Through `IElectricVehicle`, code reaches them as the typed `Charge`, `Climate` and `Body` of the vehicle.

| Property | Unit | Description |
|----------|------|-------------|
| `Vin`, `SerialNumber` | | Vehicle identification number |
| `Nickname` | | Name in the Bluelink app |
| `Model` | | Model from the vehicle list, such as KONA |
| `Manufacturer` | | `Hyundai` |
| `Protocol` | | `Legacy` (older vehicles, commands without PIN) or `Ccs2` (newer vehicles, charging and climate commands with PIN) |
| `TotalDrivenDistance` | km | Odometer |
| `Location` | | Stored parking location; kept when a read returns none |
| `AuxiliaryBatteryLevel` | Percent | 12 V battery; `null` when the vehicle reports its sensor as unreliable |
| `LastUpdated` | | Time of the stored status; the read time when the status has none |
| `IsBusy` | | `true` while a `Refresh` or command of this vehicle runs |

The title is the nickname, else the model (or "Hyundai" without one), a colon and the last six characters of the VIN, such as "KONA: 123456". The icon is green while the vehicle charges.

### Charge

| Property | Unit | Description |
|----------|------|-------------|
| `BatteryLevel` | Percent | State of charge |
| `IsPluggedIn` | | Whether the cable is plugged in; `null` when the vehicle does not tell |
| `IsCharging` | | Whether the vehicle charges: `false` when the charging state is 0 or the connector is not fastened, else from the remaining charging time; `null` when unknown |
| `ChargingPower` | W | Charging power, which the API reports in kW |
| `ChargeLimit` | Percent | Charge limit for AC charging |
| `FastChargeLimit` | Percent | Charge limit for DC fast charging |
| `RemainingChargingTime` | TimeSpan | Time until the charge limit, only while charging |
| `Range` | km | Estimated range; a range that the vehicle reports in miles is converted, and an invalid reading or an unknown unit is `null` |
| `MaximumChargingPower` | W | Always `null`; the API does not report it |

### Climate

| Property | Unit | Description |
|----------|------|-------------|
| `IsClimateOn` | | Whether the climate runs, from the blower speed |
| `DriverTemperatureSetting` | °C | Set temperature; `null` while the climate is off or in another unit |
| `IsFrontDefrosterOn`, `IsSteeringWheelHeaterOn` | | Front windshield defrost and steering wheel heater |
| `OutsideTemperature` | °C | Outside temperature, when the vehicle reports it |
| `InsideTemperature` | °C | Always `null`; the API does not report it |

### Body

| Property | Description |
|----------|-------------|
| `LockState`, `IsLocked` | `Unlocked` when any door is unlocked, otherwise `null` when a door is unknown, and `Locked` when all four doors are locked |
| `IsDriverFrontDoorOpen`, `IsPassengerFrontDoorOpen`, `IsDriverRearDoorOpen`, `IsPassengerRearDoorOpen` | Doors |
| `IsTrunkOpen`, `IsHoodOpen` | Trunk and hood |
| `IsDriverFrontWindowOpen`, `IsPassengerFrontWindowOpen`, `IsDriverRearWindowOpen`, `IsPassengerRearWindowOpen` | Windows; a vented window counts as open |
| `IsAnyDoorOpen`, `IsAnyWindowOpen` | Derived: `true` when any of the four is open, `null` when none is open and one is unknown |
| `FrontLeftTire`, `FrontRightTire`, `RearLeftTire`, `RearRightTire` | `BluelinkTire` children with `IsPressureLow` and `Pressure` (bar; `null` for vehicles without direct pressure sensors and when the vehicle reports no reading, such as the value 255) |

## Operations

| Operation | Subject | Reaches the vehicle | Description |
|-----------|---------|---------------------|-------------|
| `Refresh` | `BluelinkVehicle` | Yes | Asks the vehicle to upload its status, then reads it; for a `Ccs2` vehicle after waiting 25 seconds. Refused before any call while the 12 V battery is below `MinimumAuxiliaryBatteryLevel` or unknown. Asks for confirmation. |
| `Start Charging`, `Stop Charging` | `Charge` | Yes | Starts or stops charging. A `Ccs2` vehicle needs the PIN. |
| `Set Charge Limit` | `Charge` | Yes | Sets the AC charge limit (0.5 to 1.0 in steps of 0.1) and keeps the DC limit, because the API always takes both. Fails while the DC limit is unknown or not a valid setting; use `Set Charge Limits` then. No PIN needed. |
| `Set Charge Limits` | `Charge` | Yes | Sets the AC and the DC charge limit, each from 0.5 to 1.0 in steps of 0.1. No PIN needed. |
| `Start Climate`, `Start Climate With Defrost` | `Climate` | Yes | Starts the climate with a temperature from 14.0 to 31.5 °C in steps of 0.5, for `ClimateDuration`. A `Ccs2` vehicle needs the PIN. |
| `Stop Climate` | `Climate` | Yes | Stops the climate. A `Ccs2` vehicle needs the PIN. |

A command fails before any call when the account has no credentials, is not connected, is paused after Hyundai's request limit or has spent its budget, when a `Ccs2` vehicle needs a PIN that is not stored, and when the temperature, a charge limit or `ClimateDuration` is invalid. A command waits for its result: the account checks the notification records of the vehicle every 10 seconds for at most 90 seconds, then reads the stored status and parking location once. A rejected command, a vehicle that does not respond and a result that does not arrive in time fail the operation with the reason. Hyundai rejecting the PIN fails the command without a retry, because five wrong PINs lock PIN entry for a while. Values change only from what the vehicle reports, never from what a command asked for. Canceling a command does not change the status of the account. The operations are disabled while the account is not connected or has spent its budget, and the commands that need the PIN while a `Ccs2` vehicle has none; `Refresh` also while the 12 V battery is below the minimum or unknown.

While one operation of a vehicle runs, `IsBusy` is `true` and all operations of that vehicle are disabled. A call that arrives anyway fails at once with "Another operation of this vehicle is running.", before any call. An operation of another vehicle of the same account waits until the running one and any poll in progress have finished.

A HomeBlaze automation can call `Refresh` on a schedule when fresher values are needed; the 12 V guard and the call budget apply.

## Polling and call budget

| Call | When | Reaches the vehicle |
|------|------|---------------------|
| `POST /api/v1/spa/notifications/register` | When no device ID is stored | No |
| `GET /api/v1/spa/vehicles` | At start, after the tokens were replaced, then every 24 hours | No |
| `GET /api/v1/spa/vehicles/{id}/ccs2/carstatus/latest` | Every `StatusPollingInterval`, per vehicle, both protocols | No |
| `GET /api/v1/spa/vehicles/{id}/location/park` | Every `StatusPollingInterval`, per vehicle | No |

The stored status changes only when the vehicle uploads it, for example when the ignition is switched off or charging starts or ends. Hours without change while the vehicle is parked are normal; `LastUpdated` shows the age. A status older than the applied one is skipped. A failing parking location, such as an error answer that some accounts give, a timeout or a network error, keeps the previous one and fails neither the poll nor a `Refresh` or command.

### Call budget

Hyundai blocks an account that makes too many calls in a day (about 200, not documented), for up to a day, and then the official app fails too. The account therefore counts its calls to the vehicle API in `RequestsToday`, per local day:

- Polling pauses once fewer than 10 calls are left, so that commands and `Refresh` still work, and continues after midnight.
- At `DailyRequestLimit`, polling, `Refresh` and commands stop, and both the status message and the error of a refused command read "Daily request limit reached." They continue after midnight, or at once when the limit is raised.
- When Hyundai reports its limit (5091), polling, `Refresh` and commands pause for 2 hours; meanwhile `Refresh` and commands fail before any call.
- The vehicle API calls of the setup and authentication calls are not counted; the PIN exchange of a command is. The counter starts at 0 after a restart.

One vehicle with the default interval needs about 100 calls a day. `Refresh` needs 3 calls (the request and the status and location read); a command needs 4 to 13 (the PIN exchange of a `Ccs2` vehicle, the command, up to 9 result checks, and the status and location read).

## Known Limitations

- **The API is unofficial and the login breaks.** Hyundai changed the login four times between mid 2025 and late 2026 (a reCAPTCHA, a firewall against old client IDs, a signature of the app, and a removed refresh). It will break again. A message about a rejected app fingerprint (9035, 9036, 9037) or an abusing request means the login changed and the integration needs an update.
- **Europe and Hyundai only, electric vehicles only.** Kia, Genesis, other regions and petrol, hybrid and plug-in hybrid vehicles are not supported.
- **Values can be hours old.** Polling reads the status that Hyundai's servers stored, which the vehicle updates only at certain events.
- **Refresh and commands cost 12 V battery.** They reach the vehicle. Frequent forced refreshes have drained the 12 V batteries of other users' vehicles, which is why polling never forces one. Hyundai rejects commands while the 12 V battery is low (about 40 %).
- **The legacy Refresh is not verified.** For a `Legacy` vehicle, the integration expects Hyundai to answer the status request only once the vehicle uploaded its status (the first refresh of a 2021 Kona Electric took about 25 seconds), within the timeout of 90 seconds. A vehicle that takes longer fails the `Refresh` with a timeout.
- **The daily limit is a guess.** Hyundai does not document it. The counter resets on a restart.
- **The protocol flag can be wrong.** Some vehicles report the wrong protocol; their commands then fail with 5031 or 5153.
- **No seat heating, no lock or unlock, no charging current.** Seat heating fails on some older vehicles, and unlocking from HomeBlaze is not wanted.
- **The charging power unit is not verified on every vehicle.** It is read as kW.
- **Signing in again can log out the official app.** It happens only when Hyundai rejected a refresh. When the sign in that follows fails for another reason than the credentials, such as a server error or a blocked request, the account tries the refresh and the sign in again after every `RetryInterval`, which can log out the official app repeatedly. Raise `RetryInterval` if that happens.
- **The location is state like any other.** It is readable by automations, pages, history and AI agents with access to the vehicle. Restrict access accordingly.
- **The account has to live in a storage file.** See [Setup](#setup).

## API Reference

The integration uses region 1 ("Europe") of the community library hyundai_kia_connect_api as reference. Region 9 (GSPA) is not used, because it rejects vehicles without CCS2.

| Endpoint | Host | Purpose |
|----------|------|---------|
| `GET /auth/api/v2/user/oauth2/authorize`, `GET /auth/api/v1/accounts/certs`, `POST /auth/account/signin` | `idpconnect-eu.hyundai.com` | Sign in with the RSA-encrypted password (OneApp client) |
| `POST /auth/v1/token`, `POST /auth/v2/token-refresh`, `POST /auth/v1/token-exchange?serviceType=CCS` | `cci-api-eu.hyundai.com` | Token set, its refresh (which replaces it) and the access token of the vehicle API; signed with the X-Fingerprint of the app |
| `POST /api/v1/spa/notifications/register` | `prd.eu-ccapi.hyundai.com:8080` | Device registration |
| `GET /api/v1/spa/vehicles`, `.../ccs2/carstatus/latest`, `.../location/park` | `prd.eu-ccapi.hyundai.com:8080` | Vehicle list, stored status, stored parking location |
| `GET .../status` (legacy), `GET .../ccs2/carstatus` (CCS2) | `prd.eu-ccapi.hyundai.com:8080` | `Refresh` |
| `POST /api/v1/spa/vehicles/{id}/control/charge`, `.../control/temperature` | `prd.eu-ccapi.hyundai.com:8080` | Legacy commands |
| `POST /api/v2/spa/vehicles/{id}/ccs2/control/charge`, `.../ccs2/control/temperature`, `PUT /api/v1/user/pin` | `prd.eu-ccapi.hyundai.com:8080` | CCS2 commands with the control token of the PIN |
| `POST /api/v1/spa/vehicles/{id}/charge/target` | `prd.eu-ccapi.hyundai.com:8080` | Charge limits |
| `GET /api/v1/spa/notifications/{id}/records` | `prd.eu-ccapi.hyundai.com:8080` | Results of commands |

Every call times out after 30 seconds; the status request of `Refresh` after 90 seconds. A timeout fails what made the call: in a poll, a status read fails only that vehicle and any other call fails the poll; otherwise the `Refresh` or command. A failing parking location never fails anything; the previous location is kept.

Every vehicle API call carries the `Stamp` header of the app. The access token lives in memory and is refreshed 6 minutes before it expires, and after a call answered with an expired token; each refresh replaces the token set, which is stored before the new access token is used. The control token of the PIN is kept in memory until shortly before it expires.
