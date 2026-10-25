# Ecowitt Weather Station Gateway

Integrates Ecowitt weather station gateways (GW1000, GW1100, GW2000, etc.) via the local HTTP polling API. The gateway acts as a hub, automatically discovering connected sensors.

## Supported Devices

Any Ecowitt gateway that exposes the local HTTP API:
- GW1000, GW1100, GW1200
- GW2000, GW2000X
- HP2550, HP2560, HP3500

## Configuration

| Property | Description | Default |
|----------|-------------|---------|
| Name | Display name | — |
| Host Address | Gateway IP address or hostname | — |
| Polling Interval | How often to poll the gateway | 30 seconds |
| Retry Interval | Wait time after connection failure | 60 seconds |
| Hidden Sensors | Sensor keys to exclude from discovery | — |

## Readings and Sensors

The gateway reports two separate things, and the device models both:

- **Readings** are merged by the gateway per category. All outdoor sensors feed one outdoor reading, all traditional rain gauges one rain reading, and so on, without saying which sensor delivered a value. A reading subject such as `OutdoorSensor` holds these merged values.
- **Device health** is per sensor. The gateway lists every paired sensor with its ID, type, battery and signal, but without values. Each paired sensor is a subject in `Sensors`, keyed by its sensor ID.

Every reading subject has `Sources`, the sensors in `Sensors` that can feed it, chosen by their type codes. They are the same subjects as in `Sensors`, not copies, so their history is recorded under `Sensors[id]`. When a value is missing in a reading, the sources show which sensor is offline or has an empty battery.

```
Weather Station (EcowittGateway)
├── OutdoorSensor              Temperature, Humidity, WindSpeed, Illuminance, ...
│   └── Sources
│       ├── [0x9637] ──> Sensors[0x9637]
│       └── [0x51B]  ──> Sensors[0x51B]
├── IndoorSensor               Temperature, Humidity, pressures
├── RainGauge                  RainRate, DailyRain, TotalRain, ...
│   └── Sources
│       └── [0x1C183] ──> Sensors[0x1C183]
└── Sensors
    ├── [0x9637]   WS90, Temp & Humidity & Solar & Wind & Rain, offline, no battery, no signal
    ├── [0x51B]    WH68, Solar & Wind, online, battery 100 %, 4 bars, -75 dBm
    └── [0x1C183]  WH40, Rain, online, battery 40 %, 4 bars, -69 dBm
```

With the WS90 offline, temperature and humidity are empty, while wind and light still come from the WH68.

### Sensor properties

| Property | Description |
|----------|-------------|
| SensorId | Sensor ID, for example `0x9637`, also the key in `Sensors` |
| TypeCode | Type code of the slot the sensor is paired in (see the table below) |
| TypeName | Sensor model for the type code, for example `WS90` or `WH40` |
| TypeDescription | What the sensor measures as the gateway describes the type, for example `Solar & Wind` |
| IsOnline | Whether the gateway currently receives the sensor |
| LastSeen | Last time the gateway received the sensor, kept while the sensor is offline. It is kept in memory and starts over when the gateway connection is lost or the service restarts. |
| BatteryLevel | Battery from 0 to 100 %, empty while the gateway reports none |
| SignalStrength | Reception quality in bars from 0 to 4 |
| Rssi | Received signal strength in dBm |

The sensor list is read on every poll, so sensors paired or removed in the gateway are added to or removed from `Sensors` on the next poll. Slots that are learning (`FFFFFFFF`) or disabled (`FFFFFFFE`) have no sensor.

### Sources of the readings

| Reading | Sources (type codes) |
|---------|----------------------|
| OutdoorSensor | 0, 1, 2, 5, 48, 49 |
| IndoorSensor | 4 |
| RainGauge | 0, 3 |
| PiezoRainGauge | 48, 49 |
| LightningSensor | 26 |
| ChannelSensors, SoilMoistureSensors, LeafWetnessSensors, TemperatureSensors, Pm25Sensors, LeakSensors | The sensor paired in the slot of the channel |

The CO2 readings have no sources, because the type code of their slot is not mapped yet. Their sensor still appears in `Sensors`.

## Sensor Types

The gateway automatically discovers these sensor types:

| Sensor | Measurements |
|--------|-------------|
| Outdoor | Temperature, humidity, dew point, feels-like, wind speed/gust/direction, UV index, solar radiation, illuminance, vapor pressure deficit |
| Indoor (the gateway's built-in sensor, or a paired WH25/WH32B) | Temperature, humidity, absolute/relative pressure |
| Rain Gauge | Event, rate, last hour, last 24 hours, daily/weekly/monthly/yearly totals |
| Piezo Rain (WS90) | Same as rain gauge |
| Channel Sensors | Per-channel temperature and humidity |
| Soil Moisture | Per-channel soil moisture percentage |
| Leaf Wetness | Per-channel leaf wetness |
| Temperature (WH34) | Per-channel temperature-only sensors |
| PM2.5 | Per-channel particulate matter with 24h average |
| CO2 | Per-channel CO2 with temperature and humidity |
| Leak Detectors | Per-channel water leak status |
| Lightning | Distance, strike count, last strike time |

The gateway's built-in sensor measures wherever the gateway is placed and is not in the gateway's sensor list, so it has no entry in `Sensors`.

## Hidden Sensors

The gateway may pick up sensors from neighboring stations. Use the Hidden Sensors configuration to exclude unwanted sensors. Sensor keys follow the format: `outdoor`, `indoor`, `rain`, `piezo`, `lightning`, and per channel `ch:1`, `soil:2`, `leaf:1`, `temp:1`, `pm25:1`, `co2:1`, `leak:1`. A sensor in `Sensors` is left out as well when every reading it feeds is hidden, for example the sensor in channel 3 with `ch:3`, or a WS90 with both `outdoor` and `piezo`. `co2:1` hides only the CO2 reading: the WH45 (type 39) stays in `Sensors`, because its slot is not mapped to a reading. The same holds for the other types without a mapped reading, such as the WH54 (66 to 69), the WN20 (70) and the WN38 (71): `HiddenSensors` cannot hide them, only disabling their slot with `FFFFFFFE` does. To stop the gateway from learning a neighbor's sensor at all, disable its channel in the gateway's web interface by setting the sensor ID to `FFFFFFFE`.

## JSON Configuration Example

```json
{
  "$type": "Namotion.Devices.Ecowitt.EcowittGateway",
  "name": "Weather Station",
  "hostAddress": "192.168.1.x",
  "pollingInterval": "00:00:30",
  "retryInterval": "00:01:00",
  "hiddenSensors": ["ch:3", "ch:6"]
}
```

## Troubleshooting

- **Not connecting**: Verify the gateway IP is reachable. The gateway must be on the same network.
- **Missing sensors**: New sensors may take one polling cycle to appear. Check that they aren't in the hidden sensors list.
- **Wrong units**: The library automatically detects and converts from imperial to metric. Values are always displayed in SI units.

## Local HTTP API Reference

The Ecowitt gateway exposes an undocumented but stable local HTTP API. Values are returned as strings with embedded units (e.g., `"1.8 m/s"`, `"946.2 hPa"`, `"44%"`). Units depend on the gateway's display configuration (metric vs imperial). `EcowittValueParser` detects and converts all values to metric SI.

### Endpoints

| Endpoint | Purpose |
|----------|---------|
| `GET /get_livedata_info` | All sensor readings, grouped by section |
| `GET /get_sensors_info?page=1` | Sensor slots page 1: outdoor, rain, indoor, channels |
| `GET /get_sensors_info?page=2` | Sensor slots page 2: soil, leaf, temp, PM2.5, CO2, leak, lightning |
| `GET /get_device_info` | Model, station type, RF frequency |
| `GET /get_version` | Firmware version string (prefixed with `"Version: "`) |
| `GET /get_network_info` | MAC, IP, gateway, DNS (field names differ for WiFi vs Ethernet) |

### Live data sections

| Section | Format | Contents |
|---------|--------|----------|
| `common_list[]` | Array of `{id, val, unit}` | Outdoor readings keyed by hex/decimal IDs |
| `wh25[]` | Array of `{intemp, inhumi, abs, rel, unit}` | Indoor sensor (single element) |
| `rain[]` | Array of `{id, val}` | Traditional rain gauge buckets |
| `piezoRain[]` | Array of `{id, val}` | Piezo rain sensor (WS90) buckets |
| `ch_aisle[]` | Array of `{channel, temp, humidity, unit, battery}` | Multi-channel temp+humidity |
| `ch_soil[]` | Array of `{channel, humidity, battery}` | Soil moisture channels |
| `ch_temp[]` | Array of `{channel, temp, unit, battery}` | Temperature-only channels (WH34) |
| `ch_leaf[]` | Array of `{channel, leaf_wetness, battery}` | Leaf wetness channels (WH35) |
| `ch_pm25[]` | Array of `{channel, pm25, pm25_24h, battery}` | PM2.5 air quality |
| `ch_co2[]` | Array of `{channel, co2, co2_24h, temp, humidity, unit, battery}` | CO2 sensors |
| `ch_leak[]` | Array of `{channel, leak, battery}` | Water leak detectors |
| `lightning` | Object `{distance, count, timestamp, battery}` | Lightning (NOT an array) |

### common_list ID mapping

| ID | Measurement | Parser |
|----|-------------|--------|
| `0x02` | Outdoor temperature | `ParseTemperature` (°F → °C) |
| `0x03` | Dew point | `ParseTemperature` |
| `3` | Feels-like temperature (plain decimal ID, not hex) | `ParseTemperature` |
| `5` | Vapor pressure deficit (always kPa, gateway-calculated) | `ParseDecimal` |
| `0x07` | Outdoor humidity | `ParseHumidity` (% → 0..1) |
| `0x0A` | Wind direction (degrees) | `ParseDecimal` |
| `0x0B` | Wind speed | `ParseWindSpeed` (mph/km/h/knots → m/s) |
| `0x0C` | Wind gust | `ParseWindSpeed` |
| `0x19` | Max daily gust | `ParseWindSpeed` |
| `0x15` | Illuminance | `ParseIlluminance` (Klux/W/m² → lux) |
| `0x16` | Solar radiation (W/m²) | `ParseDecimal` |
| `0x17` | UV index | `ParseDecimal` |

### Sensor type codes and battery classification

In `/get_sensors_info`, each slot has a `type` code, an `img` key naming the model, a `name` describing the type, an `id`, and `batt`, `rssi` and `signal` (0 to 4 bars). A learning slot has the ID `FFFFFFFF`, a disabled one `FFFFFFFE`. A paired sensor that is offline reports `batt` 9 and `--` for `rssi` and `signal`. The battery in `Sensors` comes from the `batt` field.

**Binary battery** (0=OK/full, 1=Low/empty): WH25 (type 4), WH26 (type 5, per the Ecowitt protocol, not verified on a device) and WH31 (types 6-13).

**0-5 level scale** (0=empty, 5=full, maps to 0-100%): All other sensor types.

**Special values**: `9` = sensor off/not available, `6` = DC powered.

| Type Code | Sensor |
|-----------|--------|
| 0 | WH69 (temp, humidity, solar, wind, rain) |
| 1 | WH68 (solar, wind) |
| 2 | WH80 (temp, humidity, solar, wind) |
| 3 | WH40 (rain gauge) |
| 4 | WH25 (indoor temp, humidity, pressure) |
| 5 | WH26 (outdoor temp, humidity) |
| 6-13 | WH31 ch1-8 (temp+humidity) |
| 14-21 | WH51 soil 1-8 |
| 22-25 | WH41 PM2.5 1-4 |
| 26 | WH57 (lightning) |
| 27-30 | WH55 leak 1-4 |
| 31-38 | WH34 temp 1-8 |
| 39 | WH45 (PM2.5, PM10, CO2) |
| 40-47 | WH35 leaf 1-8 |
| 48 | WS90 (temp, humidity, solar, wind, piezo rain; the gateway reports the `img` key `wh90`) |
| 49 | WS85 (wind, piezo rain; the gateway reports the `img` key `wh85`) |
| 58-65 | WH51 soil 9-16 |
| 66-69 | WH54 ch1-4 |
| 70 | WN20 (rain mini; whether it feeds `rain` is unconfirmed, so it is no source of `RainGauge`) |
| 71 | WN38 (BGT) |

For a type code missing from this table, `TypeName` is the slot's `img` key in upper case.

Note: The local HTTP API battery format differs from the Ecowitt push/custom server protocol, where sensors like WH34 and WH51 report actual voltage values (e.g., `tf_batt1=1.68`).

### Rain accumulation

The `rain` and `piezoRain` arrays of `get_livedata_info` identify each value by item id:

| ID | Value |
|----|-------|
| `0x0D` | Rain event |
| `0x0E` | Rain rate |
| `0x7D` | Last hour, not sent by every firmware and not documented by Ecowitt |
| `0x7C` | Last 24 hours, not sent by every firmware |
| `0x10` | Daily |
| `0x11` | Weekly |
| `0x12` | Monthly |
| `0x13` | Yearly (carries the sensor battery) |
| `0x14` | Gateway rain total, not sent by every firmware |

The daily, weekly, monthly and yearly buckets reset at their respective boundaries. `TotalRain` is a monotonically increasing cumulative counter built from the longest available bucket (the gateway total, falling back to yearly). When that bucket decreases compared to the previous poll, the gateway detects a reset and adds the previous value to a persisted `RainCumulativeOffset`. Separate offsets are maintained for traditional and piezo rain gauges.
