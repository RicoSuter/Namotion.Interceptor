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

## Sensor Types

The gateway automatically discovers these sensor types:

| Sensor | Measurements |
|--------|-------------|
| Outdoor | Temperature, humidity, dew point, feels-like, wind speed/gust/direction, UV index, solar radiation, illuminance, vapor pressure deficit |
| Indoor (WH25) | Temperature, humidity, absolute/relative pressure |
| Rain Gauge | Event, rate, hourly/daily/weekly/monthly/yearly totals |
| Piezo Rain (WS90) | Same as rain gauge |
| Channel Sensors | Per-channel temperature and humidity |
| Soil Moisture | Per-channel soil moisture percentage |
| Leaf Wetness | Per-channel leaf wetness |
| Temperature (WH34) | Per-channel temperature-only sensors |
| PM2.5 | Per-channel particulate matter with 24h average |
| CO2 | Per-channel CO2 with temperature and humidity |
| Leak Detectors | Per-channel water leak status |
| Lightning | Distance, strike count, last strike time |

## Hidden Sensors

The gateway may pick up sensors from neighboring stations. Use the Hidden Sensors configuration to exclude unwanted sensors. Sensor keys follow the format: `ch:1`, `soil:2`, `leaf:1`, `rain`, `piezo`, `lightning`.

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

In `/get_sensors_info`, each slot has a `type` code. Battery is reported in the `batt` field.

**Binary battery** (0=OK/full, 1=Low/empty): Only WH31 (types 6-13) and WH25 (type 4).

**0-5 level scale** (0=empty, 5=full, maps to 0-100%): All other sensor types.

**Special values**: `9` = sensor off/not available, `6` = DC powered.

| Type Code | Sensor |
|-----------|--------|
| 0, 1, 2 | WH69/WH68/WH80 (anemometer/solar+wind) |
| 3 | WH40 (rain gauge) |
| 4 | WH25 (indoor) |
| 6-13 | WH31 ch1-8 (temp+humidity) |
| 14-21 | WH51 soil 1-8 |
| 22-25 | WH41 PM2.5 1-4 |
| 26 | WH57 (lightning) |
| 27-30 | WH55 leak 1-4 |
| 31-38 | WH34 temp 1-8 |
| 40-47 | WH35 leaf 1-8 |
| 48, 49 | WS90/WH85 (all-in-one) |
| 58-65 | WH51 soil 9-16 |

Note: The local HTTP API battery format differs from the Ecowitt push/custom server protocol, where sensors like WH34 and WH51 report actual voltage values (e.g., `tf_batt1=1.68`).

### Rain accumulation

The API provides periodic rain buckets (hourly, daily, weekly, monthly, yearly) that reset at their respective boundaries. `TotalRain` is a monotonically increasing cumulative counter built from the longest available bucket (yearly, falling back to monthly). When that bucket decreases compared to the previous poll, the gateway detects a reset and adds the previous value to a persisted `RainCumulativeOffset`. Separate offsets are maintained for traditional and piezo rain gauges.
