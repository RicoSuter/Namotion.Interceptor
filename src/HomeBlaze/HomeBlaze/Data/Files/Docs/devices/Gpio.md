---
title: GPIO
icon: Memory
---

# GPIO (General Purpose Input/Output)

The GPIO device provides access to GPIO pins on various Linux-based single-board computers and optional analog-to-digital converters (ADC).

## Configuration

### GpioSubject Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `UseSimulation` | bool | false | Enable simulation mode for testing without hardware |
| `PollingInterval` | TimeSpan | 5 seconds | Interval for GPIO verification and ADC polling |
| `RetryInterval` | TimeSpan | 30 seconds | Retry interval when initialization fails |
| `Mcp3008` | Mcp3008Configuration | null | Optional MCP3008 ADC configuration |
| `Ads1115` | Ads1115Configuration | null | Optional ADS1115 ADC configuration |
| `Pins` | Dictionary&lt;int, GpioPin&gt; | - | Auto-discovered GPIO pins (persisted) |
| `AnalogChannels` | Dictionary&lt;int, AnalogChannel&gt; | - | ADC channels (0-7 MCP3008, 8-11 ADS1115), read-only |
| `Status` | ServiceStatus | - | Status of the GPIO controller, read-only |
| `StatusMessage` | string? | - | Error or info message, read-only |

### GpioSubject Operations

| Operation | Description |
|-----------|-------------|
| `ApplyRaspberryPiDefaults` | Names the pins with a fixed function (I2C, SPI, UART, PWM, HAT EEPROM) and applies the power-on pull of a Raspberry Pi 3, 4 or Zero 2 W (pull-up for GPIO 0 to 8, pull-down for GPIO 9 to 27), both only to unconfigured pins: those without a name in plain `Input` mode. Other pins and the pins of a configured MCP3008 are not changed, so applying it again changes nothing. Saves the configuration when a pin changed. Check the defaults before using it on a Raspberry Pi 5. |

### JSON Configuration Example

```json
{
  "$type": "Namotion.Devices.Gpio.GpioSubject",
  "useSimulation": false,
  "pollingInterval": "00:00:05",
  "retryInterval": "00:00:30",
  "mcp3008": {
    "clockPin": 11,
    "mosiPin": 10,
    "misoPin": 9,
    "chipSelectPin": 8
  },
  "ads1115": {
    "i2cBus": 1,
    "address": 72
  },
  "pins": {
    "5": { "name": "Relay", "mode": "Output", "isInverted": true },
    "18": { "name": "Button", "mode": "InputPullUp", "isInverted": true, "debounceTime": "00:00:00.05" }
  }
}
```

## Simulation Mode

Enable `UseSimulation = true` to test GPIO functionality without real hardware:

- Simulates 28 GPIO pins (BCM 0-27)
- All pins are available and functional
- Pin values and modes are tracked in memory
- Useful for development on Windows/macOS

## Requirements

### Linux Dependencies

On Raspberry Pi OS, install the libgpiod library:

```bash
sudo apt update
sudo apt install libgpiod3
```

On older distributions, the package may be named `libgpiod2`.

### Supported Platforms

- **Raspberry Pi 3/4/5** - Full GPIO support (pin count detected automatically)
- **Raspberry Pi Zero 2 W** - Supported with a 64-bit OS (`linux-arm64`). The original Raspberry Pi Zero W has an ARMv6 CPU, which .NET does not support
- **Orange Pi** - Supported via libgpiod
- **BeagleBone** - Supported via libgpiod
- **ODROID** - Supported via libgpiod
- **Other Linux with GPIO** - Any board exposing `/dev/gpiochip*` via libgpiod
- **Windows IoT Core** - Native support
- **Windows/macOS** - Use simulation mode for development/testing

## Pin Configuration

Each GPIO pin supports four modes:

| Mode | Description |
|------|-------------|
| `Input` | Digital input (floating) |
| `InputPullUp` | Digital input with internal pull-up resistor |
| `InputPullDown` | Digital input with internal pull-down resistor |
| `Output` | Digital output |

### GpioPin Properties

| Property | Type | Description |
|----------|------|-------------|
| `PinNumber` | int | BCM pin number (read-only) |
| `Name` | string? | Optional friendly name (persisted) |
| `Mode` | GpioPinMode | Pin operating mode (persisted) |
| `IsInverted` | bool | `Value` is the inverse of the pin level (persisted, default `false`), see [Inversion](#inversion) |
| `DebounceTime` | TimeSpan | Time an input ignores further edges after a change (persisted, default 50 ms, zero disables it), see [Debouncing](#debouncing) |
| `Value` | bool | Current logical value: the pin level (true=high), inverted when `IsInverted` is set (read-only) |
| `Status` | ServiceStatus | Pin availability status (read-only) |
| `StatusMessage` | string? | Error or info message (read-only) |

`PinNumber`, `Value`, `Status` and `StatusMessage` are state: only the library changes them. `Name`, `Mode`, `IsInverted` and `DebounceTime` are configuration.

### GpioPin Operations

| Operation | Description |
|-----------|-------------|
| `TurnOn`, `TurnOff`, `Toggle` | Switch an output pin |
| `SetValue` | Sets the logical value of an output pin, such as from an automation expression |

The state of a pin is read-only; an output changes only through these operations. They are disabled while the pin is not an output or not running, and `TurnOn` and `TurnOff` also while the output already has that value. They throw on an input pin, whose value follows the hardware.

### Pin Status

Pins report their status via `ServiceStatus`:

- **Running** - Pin is operational
- **Unavailable** - Pin reserved by system (I2C, SPI) or platform not supported
- **Error** - Hardware fault detected (e.g., write verification failed)

## Hardware Synchronization

### Output Pins

When the GPIO controller starts, an output is opened at the level the pin has, and `Value` follows from it: after a restart of HomeBlaze the level it drove, so a restart does not switch outputs, and after a reboot of the Pi its power-on pull. This applies to the Raspberry Pi register driver (`/dev/gpiomem`, Raspberry Pi 3, 4 and Zero 2 W). With libgpiod 1.x (package `libgpiod2`), which cannot read a pin before it is opened, an output starts low and `Value` is read back from it. With libgpiod 2.x (package `libgpiod3`) or sysfs, an output keeps its level, but a high level can drop low for a moment while the pin becomes an output, at startup and when the mode changes to `Output`, because these drivers set the direction before the level. This is not verified on hardware. On a Raspberry Pi 3, 4 or Zero 2 W, GPIO 9 to 27 are pulled down while the Pi boots, so an active-low relay on one of them can switch on during boot and then stays on when HomeBlaze starts: connect an active-low relay to GPIO 4, 5 or 6, which are pulled up at boot, or add an external pull-up.

When `TurnOn` or `SetValue` sets an output pin to true:
1. The level is written to GPIO hardware (low for an inverted pin)
2. A read-back verification confirms the write succeeded
3. If verification fails, `Status` is set to `Error` with a message indicating possible short circuit or external driver conflict

### Input Pins

Input pins use interrupt-driven updates:
- Rising/falling edge triggers update the `Value` property, with [inversion](#inversion) and [debouncing](#debouncing) applied
- At startup, an input reads its level with inversion applied
- Polling verification runs every `PollingInterval` to catch missed interrupts
- Mode changes automatically register/unregister interrupt handlers
- Interrupts are only registered for input pins (not output)

### Inversion

`IsInverted` makes `Value` the inverse of the pin level, both for reading and for writing. Use it for active-low hardware:

- **Active-low relay boards** switch on when the pin is low. With `IsInverted`, `TurnOn` turns the relay on, and `IsOn`, the icon and automations show the state of the relay.
- **Buttons to ground** on an `InputPullUp` pin read low while pressed. With `IsInverted`, pressed is `true`.

Changing `IsInverted` at runtime keeps the pin level and reads `Value` again, so `Value` flips and an output is never switched.

### Debouncing

A mechanical contact, such as a button, switch, reed contact or relay, bounces for a few milliseconds when it closes or opens, so the pin sees several edges for one press. After a change of `Value`, an input ignores further edges for `DebounceTime`, then reads the level again and corrects `Value` if the contact settled differently. A press therefore changes `Value` at once and only once.

- The default of 50 ms suits buttons and switches. A pulse shorter than `DebounceTime` still shows as `true` and then `false`, but changes closer together than `DebounceTime` merge.
- Set it to zero for inputs that change faster than that and do not bounce.
- Outputs ignore it.
- A negative value, or one above `GpioPin.MaximumDebounceTime` (10 seconds), is rejected with `ArgumentOutOfRangeException`, so a configuration file with one fails to load.

### Common Wiring

| Use case | Mode | IsInverted | DebounceTime |
|----------|------|------------|--------------|
| Button or switch to ground | `InputPullUp` | `true` | 50 ms |
| Reed contact to ground | `InputPullUp` | `true` | 50 ms |
| PIR or other electronic sensor | `Input` | `false` | 0 |
| Pulse meter (S0) | `InputPullUp` | `true` | short or 0 |
| Active-low relay board | `Output` | `true` | not used |
| Active-high relay, MOSFET or LED | `Output` | `false` | not used |

The pin editor shows this table and hints for the selected mode.

### Configuration Persistence

Pin configurations are persisted and restored on restart:
- When `GpioSubject` loads, existing pin configurations from JSON are reused
- New pins (not in configuration) default to `Input` mode
- Changes to `Name`, `Mode`, `IsInverted` and `DebounceTime` made in the pin editor or with a control on a page are saved, and `ApplyRaspberryPiDefaults` saves the changes it makes; changes made from code are not saved by themselves

## Analog Input (ADC)

The GPIO device supports two ADC chips. Both can be configured simultaneously.

### Channel Indexing

| ADC | Dictionary Keys | Hardware Channels |
|-----|-----------------|-------------------|
| MCP3008 | 0-7 | CH0-CH7 |
| ADS1115 | 8-11 | AIN0-AIN3 |

### AnalogChannel Properties

| Property | Type | Description |
|----------|------|-------------|
| `Source` | AdcSource | ADC hardware source (Mcp3008 or Ads1115) |
| `ChannelNumber` | int | Hardware channel number |
| `Value` | double | Normalized value (0.0 to 1.0) |
| `RawValue` | int | Raw ADC value |
| `Status` | ServiceStatus | Channel status |
| `StatusMessage` | string? | Error or info message |

`Value`, `RawValue`, `Status` and `StatusMessage` are read-only state that the library updates while polling. `Source` and `ChannelNumber` are set when the channel is created.

### MCP3008

8-channel 10-bit ADC connected via SPI (software SPI on GPIO pins).

| Property | Default | Description |
|----------|---------|-------------|
| `ClockPin` | 11 | SPI clock GPIO pin (BCM) |
| `MosiPin` | 10 | SPI MOSI GPIO pin (BCM) |
| `MisoPin` | 9 | SPI MISO GPIO pin (BCM) |
| `ChipSelectPin` | 8 | SPI chip select GPIO pin (BCM) |

Raw values: 0-1023 (10-bit resolution)

### ADS1115

4-channel 16-bit ADC connected via I2C.

| Property | Default | Description |
|----------|---------|-------------|
| `I2cBus` | 1 | I2C bus number |
| `Address` | 0x48 (72) | I2C device address |

Raw values: 0-32767 (15-bit effective, single-ended)

## Example Usage

```csharp
// Access a pin
var pin = gpioSubject.Pins[17];

// Check if pin is available
if (pin.Status == ServiceStatus.Running)
{
    // Set a friendly name
    pin.Name = "Status LED";

    // Set as output and turn on
    pin.Mode = GpioPinMode.Output;
    await pin.TurnOnAsync(CancellationToken.None);

    // Read an input pin with a button to ground
    var buttonPin = gpioSubject.Pins[18];
    buttonPin.Name = "Power Button";
    buttonPin.Mode = GpioPinMode.InputPullUp;
    buttonPin.IsInverted = true; // Pull-up reads low while pressed, so pressed is true
    bool isPressed = buttonPin.Value;
}

// Apply Raspberry Pi names and power-on pulls to unconfigured pins
await gpioSubject.ApplyRaspberryPiDefaultsAsync(CancellationToken.None);

// Active-low relay
var relayPin = gpioSubject.Pins[5];
relayPin.IsInverted = true;
relayPin.Mode = GpioPinMode.Output;
await relayPin.TurnOnAsync(CancellationToken.None); // Drives the pin low

// Read MCP3008 analog value (channels 0-7)
if (gpioSubject.AnalogChannels.TryGetValue(0, out var mcp3008Channel))
{
    double voltage = mcp3008Channel.Value * 3.3; // Assuming 3.3V reference
}

// Read ADS1115 analog value (channels 8-11)
if (gpioSubject.AnalogChannels.TryGetValue(8, out var ads1115Channel))
{
    double voltage = ads1115Channel.Value * 4.096; // Default gain setting
}

// Enable simulation mode at runtime
gpioSubject.UseSimulation = true;
await gpioSubject.ApplyConfigurationAsync(CancellationToken.None);
```

## Troubleshooting

### "No supported libgpiod library file found"

Install the libgpiod library:
```bash
sudo apt install libgpiod3
```

### Pin shows "Unavailable" status

Common causes:
- Pin reserved for I2C (GPIO 2, 3) or SPI (GPIO 7-11) on Raspberry Pi
- Pin in use by another process
- Hardware-specific pin reservation

### No pins available (empty Pins dictionary)

This occurs when:
- Platform does not support GPIO (Windows/macOS without IoT support)
- libgpiod is not installed on Linux
- No GPIO chip detected on the system

**Solution**: Enable `UseSimulation = true` for development/testing.

### Write verification failed

The pin's `StatusMessage` will indicate "Write verification failed". This means:
- Possible short circuit on the pin
- External device driving the line
- Pin configured incorrectly in hardware

Check your wiring and ensure no external device is conflicting with the output.

### ADC not reading values

- Verify SPI/I2C pins are not conflicting with GPIO pins you're using
- Check wiring connections
- Ensure the ADC configuration matches your hardware setup
- MCP3008 requires 4 GPIO pins for software SPI
- ADS1115 requires I2C to be enabled on the system

## BCM Pin Reference (Raspberry Pi)

The pin count is detected automatically from the hardware. On Raspberry Pi, the following BCM pins are typically available:

| BCM | Common Use |
|-----|------------|
| 0-1 | I2C EEPROM (reserved) |
| 2-3 | I2C1 SDA/SCL |
| 4 | General purpose |
| 5-6 | General purpose |
| 7-11 | SPI0 |
| 12-13 | PWM capable |
| 14-15 | UART TX/RX |
| 16-27 | General purpose |

## Missing Features

The following features are not yet implemented but may be added in future versions:

### PWM (Pulse Width Modulation)

Hardware PWM output for controlling LED brightness, motor speed, and servo position. Raspberry Pi has 2 hardware PWM channels (GPIO 12/13 and 18/19).
