---
title: Tesla Integration
navTitle: Tesla
status: Implemented
---

# Tesla Integration

This plan is implemented. The deviations that came up during implementation are folded into the sections below; the user documentation is [Tesla](../devices/Tesla.md). It migrates the Tesla vehicle integration of HomeBlaze v1 (`HomeBlaze.Tesla`) to a v2 device library and introduces vehicle abstractions that a Hyundai Bluelink integration will share. The v1 Tesla Wall Connector is not migrated: no installation has one, and it cannot be tested.

## Goals and Phases

The integration must not wake the car unnecessarily and must not keep it awake, because both drain its battery. It must work without a Tesla developer (Fleet API) application, because a normal user cannot register one per installation.

| Phase | Scope |
|-------|-------|
| 1 (this plan) | Tesla Owner API: read everything the car reports, wake the car on request. No other commands. |
| 2 | Teslemetry or Tessie as a second provider, which adds commands (charge limit, start and stop charging, charge schedule, charge port) |
| 3 | Own command signing, only if feasible: a key enrolled over Bluetooth, commands sent over Bluetooth |

Phase 1 prepares phase 2: the provider is configuration, the client takes a base URL and a token source, and Teslemetry and Tessie serve the same paths and payloads as the Fleet API.

## Research Summary

State of November 2026, with sources in the pull request:

- **Owner API reads work.** Tesla removed the `auth.tesla.com/void/callback` redirect of the client `ownerapi` in April 2026, which broke v1. The only registered redirect left is `tesla://auth/callback`. Since June 2026 the auth and API calls need HTTP/2 and TLS 1.3, and token requests are form-encoded. `GET /api/1/vehicles` now answers "Endpoint is only available on fleetapi"; vehicles are listed with `GET /api/1/products`. TeslaMate (actively maintained, Owner API by default) follows the same path.
- **Owner API commands do not work.** Since 2024, cars except pre-2021 Model S and X reject unsigned commands. `/api/1/products` reports `command_signing: "required"`. Signing needs a key that the car trusts. Without a developer application, a key can only be enrolled over Bluetooth with a tap of the key card, and `signed_command` is only available on the Fleet API. The unofficial route over the app's Hermes WebSocket is undocumented and fragile.
- **Fleet API** needs a developer application, a public key hosted on an HTTPS domain, partner registration and a virtual key, is billed per request to the application owner, and gives a $10 monthly credit. Home Assistant and evcc removed their shared applications in 2025.
- **Login:** a normal browser cannot complete the `tesla://` redirect. A refresh token from `tesla_auth` (desktop) or "Auth app for Tesla" (iOS) is the reliable path, which TeslaMate uses as well. Refresh tokens rotate on every refresh and last about three months; access tokens last about eight hours.
- **Cached data:** every vehicle entry of `/api/1/products` has `cached_data`, a base64 protobuf of Tesla's `CarServer.VehicleData` message (schema in `teslamotors/vehicle-command`, `pkg/protocol/protobuf/vehicle.proto`, Apache 2.0). A captured response held battery level, usable battery level, charge limit, lock state and tire pressures matching the JSON response. `vehicle_data` returns the same message in its `vehicle_data` field. Whether `cached_data` stays filled while the car sleeps is unverified (see [Verification](#verification)).

## Abstractions

### Changes to existing interfaces

`IEnergyChargerState` becomes `IChargingState`, and `ChargingPower` moves into it from `IVehicleChargerState`. Both ends of a charging cable implement it: a wall charger and a vehicle.

```
IChargingState { IsPluggedIn, IsCharging, ChargingPower, MaximumChargingPower }
  IEnergyCharger : IChargingState            marker for charger devices
  IVehicleChargerState : IChargingState      + ChargeLevel
  IVehicleChargingState : IChargingState     new, see below
```

No property is renamed or removed, so paths, history and configuration files stay valid. Only code that names `IEnergyChargerState` breaks, which the pull request lists with its migration step (replace `IEnergyChargerState` with `IChargingState`). The only implementer in the repository is `WallboxCharger`.

`IsPluggedIn` and `IsCharging` are discrete, and all four members have the category "Charging" with the positions 101, 102 (flags) and 330, 331 (`ChargingPower`, `MaximumChargingPower`), so that a charger and a vehicle group alike.

Two units are added to `StateUnit`: `KilometerPerHour` (speed, speed limit) and `Bar` (tire pressure).

### New interfaces

In `HomeBlaze.Abstractions/Devices/Vehicles/`, with positions in the documented ranges and categories. `IVehicle` has the category "Vehicle" and the positions 224, 225 (`TotalDrivenDistance`, `Location`), `IVehicleChargingState` the category "Charging" and 341 to 343, and `IVehicleClimateState` the category "Climate" and the positions 153 (`IsClimateOn`), 201 and 202:

| Interface | Members |
|-----------|---------|
| `IVehicle` | `TotalDrivenDistance` (km, cumulative, the odometer), `Location` (`GeoCoordinate?`), and the navigation properties `Climate` (`IVehicleClimateState?`) and `Body` (`IDoorLockState?`), `null` when the vehicle does not report them |
| `IElectricVehicle : IVehicle` | `Charge` (`IVehicleChargingState`), never `null` |
| `IVehicleChargingState : IChargingState, IBatteryState` | `ChargeLimit` (percent), `RemainingChargingTime` (`TimeSpan?`), `Range` (km) |
| `IVehicleClimateState` | `IsClimateOn`, `InsideTemperature`, `OutsideTemperature` (°C) |

Each value has one path. A capability interface goes on the subject that owns the data, and the vehicle reaches it through a typed navigation property: `TeslaChargeState` implements `IVehicleChargingState`, `TeslaClimateState` implements `IVehicleClimateState` and `TeslaVehicleBody` implements `IDoorLockState`. `TeslaVehicle` keeps its children concretely typed and implements `Charge`, `Climate` and `Body` of the interfaces explicitly under the same names, so its metadata holds only the tracked child.

A charging controller for vehicles is not part of this plan. The Bluelink integration adds it as its first implementer, and decides whether it generalizes `IVehicleChargerController`. Phase 2 of Tesla implements it.

### Reused interfaces

`IBatteryState` (state of charge), `IDoorLockState`, `IDeviceInfo`, `ISoftwareState`, `IConnectionState`, `IAuthenticatedSubject` (its first implementer), `IMonitoredService`, `ILastUpdatedProvider`, `ITitleProvider`, `IIconProvider`, `IMediaTrackState`, `IVolumeState`, and the `GeoCoordinate` record (its first use).

## Projects

- `src/HomeBlaze/Namotion.Devices.Tesla`: subjects, client, DTOs, protobuf decoding, `TeslaServiceCollectionExtensions` with `AddHostedSubject`. References `SonarAnalyzer.CSharp` like the other device libraries.
- `src/HomeBlaze/Namotion.Devices.Tesla.HomeBlaze`: Blazor components.
- `src/HomeBlaze/Namotion.Devices.Tesla.Tests`: unit tests and the public API snapshot.
- Registration in `src/Namotion.Interceptor.slnx`, `HomeBlaze.csproj` and `Program.cs` (both assemblies), a sample `Data/Files/Devices/Tesla.json` without a token, and `Docs/devices/Tesla.md`.

The four protobuf files that `vehicle.proto` needs (`vehicle.proto`, `common.proto`, `vcsec.proto`, `managed_charging.proto`) are vendored under `Protos/` with their license header, and compiled with `Google.Protobuf` and `Grpc.Tools` (build only, `GrpcServices="None"`, `Access="Internal"`). The generated code needs no reflection, which keeps the library AOT compatible.

## Subjects

```
TeslaAccount                                   hub: provider, tokens, polling
└─ Vehicles[VIN]: TeslaVehicle                 identity, odometer, location, Wake Up
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

### TeslaAccount

`BackgroundService`, `IConfigurable`, `IMonitoredService`, `IAuthenticatedSubject`, `ITitleProvider`, `IIconProvider`. Category "Vehicles" (a new group).

- Configuration: `Name`, `Provider` (enum `TeslaProvider` with `OwnerApi` only), `RefreshToken` (secret), `StatePollingInterval` (1 min), `ActivePollingInterval` (1 min), `IdlePollingInterval` (5 min), `IdleTimeout` (15 min), `SleepWindow` (21 min), `RetryInterval` (1 min).
- State: `Status`, `StatusMessage`, `IsAuthenticated`, `IsReauthenticationRequired`, `AuthenticatedUser`, `AuthenticatedAt`, `Vehicles` (`Dictionary<string, TeslaVehicle>` keyed by VIN).
- Vehicles are discovered, not configured. Existing instances are updated in place; a new or removed vehicle replaces the dictionary.

### TeslaVehicle and its children

`TeslaVehicle` implements `IElectricVehicle`, `IDeviceInfo`, `ISoftwareState`, `IConnectionState`, `ILastUpdatedProvider`, `ITitleProvider` and `IIconProvider`. It stores `TotalDrivenDistance` and `Location` (set by the drive state from the location section) and navigates to `Charge`, `Climate` and `Body` for the other vehicle interfaces. It holds identity and status itself: `Vin`, `DisplayName`, `VehicleName`, `VehicleState` (enum `Online`, `Asleep`, `Offline`, `Unknown`), the derived `IsAwake` (true when online, false when asleep or offline, `null` when unknown; discrete, so history shows every wake and sleep), `IsConnected` (true when online or asleep, false when offline: an asleep car is still reachable through Tesla's servers), `CommandSigning` (enum from `/products`), `IsInService`, and the operation `Wake Up`, which wakes the car, waits until it is online (at most 2 minutes, polling of the account pauses meanwhile) and fetches its data. `Wake Up` is enabled only while the car is asleep or offline. `Model` is not stored: it is derived from the car type of `TeslaVehicleSpecification` (`vehicle_config`), so it is `null` until the first `vehicle_data` read, because the cached snapshot carries no specification. `ProductCode` is the trim badging and `SerialNumber` the VIN. `MaximumChargingPower` of `TeslaChargeState` is derived from the charger voltage, the lower of the pilot current and the maximum requested current, and the phases. The phases come from the measured power, voltage and current when charging, because Tesla is reported to send 2 phases while charging on 3-phase AC, and from the reported phases otherwise; it is `null` while the car does not charge.

Every field of `vehicle_data` and `cached_data` that describes the car is exposed in the child that it belongs to. Dropped are only API internals (tokens, `id_s`, user ID, backseat token, `feature_bitmask`, API version, the per-section timestamps, which feed `LastUpdated`) and deprecated fields (`option_codes`). The fixture decides the final property list; the groups are:

| Child | Content |
|-------|---------|
| `TeslaChargeState` | charging state (enum), battery level, usable battery level, charge limit with minimum, maximum and standard, charging power, currents (actual, requested, maximum, pilot), voltage, phases, added energy and range, remaining time, fast charger present, brand and type, cable, charge port door, latch and color, battery heater, scheduled charging mode and start, scheduled departure, off-peak and preconditioning settings, estimated battery capacity (v1, derived) |
| `TeslaClimateState` | climate on, auto conditioning, preconditioning, inside, outside, driver and passenger temperature, minimum and maximum settable temperature, fan, defrosters, cabin overheat protection and its activation temperature, climate keeper mode, battery heater, mirror and wiper heaters |
| `TeslaSeatHeaters` | levels of the front left, front right, rear left, rear center and rear right seats (physical sides, `FrontLeftLevel` and so on), auto seat climate of the front left and right seats, and the steering wheel heater (on, level, auto), which therefore is not in `TeslaClimateState` |
| `TeslaDriveState` | native location, heading, speed, shift state (enum), derived `IsDriving`, drive power, GPS time |
| `TeslaNavigation` | active route destination, minutes and distance to arrival, energy at arrival, traffic delay, destination location |
| `TeslaVehicleBody` | lock state, each door, window, frunk and trunk open, derived "any open" flags, sentry mode, valet mode, user present, remote start, dashcam state, homelink nearby, center display state |
| `TeslaTire` | pressure (bar), recommended pressure, soft and hard warning, last seen |
| `TeslaMedia` | playback status, source, title, artist, album, station, duration, elapsed, volume and maximum (`IMediaTrackState`, `IVolumeState`) |
| `TeslaSoftwareUpdate` | status, version, download and install progress, expected duration |
| `TeslaSpeedLimitMode` | active, current, minimum and maximum limit (km/h), PIN set |
| `TeslaVehicleSpecification` | car type, special type, trim badging, colors, wheel type, seat and roof options, drive unit, charge port type, feature flags (`vehicle_config`) |
| `TeslaDisplaySettings` | the units and 24 h setting of the car's display |

Modeling rules: readings are nullable and `null` until observed; setters are `internal`; every partial property is initialized in the constructor; enums for string states with an `Unknown` value, and an unknown value is logged once; percentages as fractions from 0 to 1; distances in km (the API reports miles), energy in Wh, power in W; `DateTimeOffset` for Unix times; `TimeSpan` for minutes after midnight. Placeholder values become `null`: a charger voltage of 0 to 2 V when not charging, `"<invalid>"`, a blank software version, an empty vehicle name. `TeslaMedia` implements `IMediaTrackState` and `IVolumeState`, not the vehicle. The title is the vehicle name, else the display name from `/products`, else the model with the last six VIN characters.

## Data Sources

DTOs are internal and mapped explicitly to the subjects; no subject property has `[JsonPropertyName]`.

1. `GET /api/1/products` (never wakes the car): discovery, VIN, display name, `state`, `command_signing`, `in_service`, and `cached_data`, which is decoded into `CarServer.VehicleData` and mapped.
2. `GET /api/1/vehicles/{id}/vehicle_data?endpoints=charge_state;climate_state;closures_state;drive_state;gui_settings;location_data;vehicle_config;vehicle_state;vehicle_data_combo` while the car is online: the full JSON. A 408 means that the car is asleep.
3. `POST /api/1/vehicles/{id}/wake_up`: only from the `Wake Up` operation.

Both sources fill the same subjects through one mapping per field, and both are applied on every poll, also while the car is awake, so the two must produce identical values for the same state, otherwise history would flap between them: every protobuf float goes through the same decimal conversion, miles are rounded to the hundredths that the snapshot reports, coordinates are reduced to float precision, enums and oneofs become exactly the strings and enum members that the JSON gives, and a protobuf enum that maps to unknown is skipped instead of overwriting a value. Where the two sources genuinely disagree, the JSON decides and the protobuf field is not mapped. A protobuf field that is absent leaves the property unchanged, because every field of the schema is optional. Each section has a timestamp clock: data that is older than the data already applied to the section is ignored, data with an equal timestamp applies, so that an older cache never overwrites a newer reading.

## Polling

`TeslaAccount.ExecuteAsync` runs one loop:

- Every `StatePollingInterval`, it reads `/products`, updates discovery, `VehicleState` and the cached data of every vehicle.
- For each online vehicle, it reads `vehicle_data` every `ActivePollingInterval` while charging, driving or with a user present, else every `IdlePollingInterval`.
- After `IdleTimeout` without activity (not charging, parked, no user present, climate and sentry off), it stops reading `vehicle_data` for `SleepWindow` so that the car can fall asleep, as TeslaMate does. It resumes when the car is still online afterwards or becomes active.
- An asleep or offline vehicle is never read with `vehicle_data` and keeps its last values.
- Nothing but the `Wake Up` operation calls `wake_up`. The account log records every wake at information level and every `vehicle_data` read at debug level, so that a car that does not sleep can be traced. Every interval is at least 10 seconds.
- A failure of one vehicle, in reading or in applying its data, does not stop the others. A 429 waits for `Retry-After` (default 5 min), `403 account disabled: EXCEEDED_LIMIT` waits 15 minutes, a 5xx or network error waits `RetryInterval`. Status and status message follow the Wallbox pattern.
- A configuration change (`ApplyConfigurationAsync`) restarts the loop.
- Time comes from an injected `TimeProvider`.

## Authentication

- The access token lives in memory. It is refreshed when less than one hour or half of its lifetime is left and after a 401 (once; a second 401 requires re-authentication).
- Refresh: `POST https://auth.tesla.com/oauth2/v3/token`, form-encoded, `grant_type=refresh_token`, `client_id=ownerapi`, `scope=openid email offline_access`. The China region is not supported: a refresh token does not tell its region before the first refresh, and no installation needs it.
- The rotated refresh token is written to the configuration through `TryGetFirstParent<IConfigurationWriter>()` before the new access token is used, as `FritzBox` persists its pinned key. The writer is only found through `TryGetFirstParent<IConfigurationWriter>()`, so the account has to live in a storage file; an account in the root configuration keeps rotated tokens in memory only and logs a warning. When writing fails, the token stays pending in the token manager, the access token is not handed out, and the next call retries only the write, without another refresh. A token that the user replaced in the configuration while a rotation ran wins over the rotated one.
- A rejected refresh (`login_required`, `invalid_grant`) sets `IsAuthenticated` false, `IsReauthenticationRequired` true and the status message "Re-authentication required", and the loop waits for a configuration change.
- `AuthenticatedUser` is the email claim of the ID token, `AuthenticatedAt` the time of the last successful refresh.
- The client owns one long-lived `HttpClient` on a `SocketsHttpHandler` with TLS 1.3 and HTTP/2 (`RequestVersionExact`), disposed with the account.
- The advanced login builds `https://auth.tesla.com/oauth2/v3/authorize` with PKCE (S256), `redirect_uri=tesla://auth/callback` and a random state, and exchanges the code from a pasted `tesla://auth/callback?code=…` URL or query string (`grant_type=authorization_code`, `code_verifier`).

## UI

In `Namotion.Devices.Tesla.HomeBlaze`, following the Wallbox and Hue components:

- `TeslaAccountSetupComponent`: refresh token field with instructions and links to `tesla_auth` and Auth app for Tesla, and a warning not to share the token with TeslaMate; a Connect button that refreshes the token and lists the vehicles from `/products` (name or VIN, VIN and state; no model, because `/products` does not return one). Connecting rotates the token at Tesla, so canceling the dialog or closing it while it connects discards the new token and the user has to create another one. The links are shown as text to copy, because a link that opens a new window does not work in a kiosk browser; a collapsed "Sign in with Tesla" section with the login address and a field for the pasted callback URL, which is cleared after use.
- `TeslaAccountEditComponent`: name and polling intervals, and a "Replace token" section with the setup flow. Uses `OnInitialized`.
- `TeslaAccountWidget`: status, number of vehicles, a warning when re-authentication is required.
- `TeslaVehicleWidget`: model and name, battery bar with the charge limit marked, range, charging indicator with power and remaining time, lock state, inside and outside temperature, online or asleep with the time of the last update.

`TeslaVehicle` has no edit component, because it has no configuration.

## Documentation

`Docs/devices/Tesla.md` describes setup, configuration, subjects and properties, polling and sleep behavior, and has a chapter "Known Limitations":

- No commands besides wake up in phase 1, and why: the car accepts only commands signed with an enrolled key, enrolling over the internet needs a Fleet API developer application, and the Owner API does not forward signed commands.
- The roadmap: Teslemetry or Tessie (paid, commands included), then own signing over Bluetooth with a HomeBlaze device near the car.
- The Owner API is unofficial and changed twice in 2026; it can break without notice.
- Data of a car that sleeps since HomeBlaze started is only available when `cached_data` is filled; otherwise it appears once the car wakes.
- The location is state like any other, readable by automations and AI agents.
- One refresh token per application: sharing it with TeslaMate makes both log out each other.
- The China region is not supported.
- Connecting rotates the pasted refresh token, so canceling the dialog afterwards discards the new token, and the account has to live in a storage file so that rotated tokens persist.

## Tests

- Mapping tests against a redacted fixture of the captured `/products`, `/vehicles/{id}` and `vehicle_data` responses. VIN, IDs, coordinates and the address inside the protobuf blobs are replaced; the blobs are re-encoded from the redacted message.
- Placeholder handling (`"<invalid>"`, charger voltage 1, blank version, empty names), unit conversion, enum mapping with unknown values.
- Cached data versus JSON precedence by timestamp.
- Polling decisions with a fake `TimeProvider`: active and idle intervals, idle timeout and sleep window, no `vehicle_data` for asleep cars, back-off after 429 and errors.
- Token handling with a fake HTTP handler: refresh before expiry and after 401, rotated token persisted before use, re-authentication state after `login_required`.
- Discovery: vehicles added, updated in place and removed.
- Public API snapshot of `Namotion.Devices.Tesla`, and the updated snapshot of `HomeBlaze.Abstractions` for the interface changes.

## Verification

- `dotnet build src/Namotion.Interceptor.slnx` without warnings and `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"` pass.
- Live check against the maintainer's car: setup with a refresh token, vehicles discovered, data while awake, the car falls asleep under polling, and whether `cached_data` delivers values while it sleeps. A capture with the car asleep and one while charging extend the fixture.
- The v1 feature checklist below is complete.

## V1 Feature Checklist

| v1 feature | v2 |
|------------|----|
| Login with the Tesla account | Refresh token paste, or sign in with a pasted callback URL |
| Token refresh | Automatic, rotated token persisted |
| Vehicle selection | All vehicles of the account discovered |
| Charge, climate, drive and vehicle state (all `vehicle_data` fields) | Children of `TeslaVehicle`, typed and normalized |
| Odometer in meters | `TotalDrivenDistance` in km |
| Locations as `GeoCoordinate` | `Location`, native location, navigation destination |
| Estimated battery capacity | `TeslaChargeState`, derived |
| `IsConnected`, `LastUpdated` | `IConnectionState`, `ILastUpdatedProvider` |
| Wake up | `Wake Up` operation |
| Set charge limit, charging amps, scheduled charging, open charge port | Not possible with the Owner API since 2024; phase 2 |
| Tesla Wall Connector | Not migrated (no installation, not testable) |
