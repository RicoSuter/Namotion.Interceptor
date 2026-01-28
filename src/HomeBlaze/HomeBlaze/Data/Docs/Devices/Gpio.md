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
| `AnalogChannels` | Dictionary&lt;int, AnalogChannel&gt; | - | ADC channels (0-7 MCP3008, 8-11 ADS1115) |

### GpioSubject Operations

| Operation | Description |
|-----------|-------------|
| `SetRaspberryPiPinNames` | Sets friendly names for Raspberry Pi GPIO pins based on their common functions (I2C, SPI, UART, PWM) |

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
    "17": { "name": "LED", "mode": "Output" },
    "18": { "name": "Button", "mode": "InputPullUp" }
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
| `PinNumber` | int | BCM pin number |
| `Name` | string? | Optional friendly name (persisted) |
| `Mode` | GpioPinMode | Pin operating mode (persisted) |
| `Value` | bool | Current value (true=high, false=low) |
| `Status` | ServiceStatus | Pin availability status |
| `StatusMessage` | string? | Error or info message |

### Pin Status

Pins report their status via `ServiceStatus`:

- **Running** - Pin is operational
- **Unavailable** - Pin reserved by system (I2C, SPI) or platform not supported
- **Error** - Hardware fault detected (e.g., write verification failed)

## Hardware Synchronization

### Output Pins

When you set `Value = true` on an output pin:
1. The value is written to GPIO hardware
2. A read-back verification confirms the write succeeded
3. If verification fails, `Status` is set to `Error` with a message indicating possible short circuit or external driver conflict

### Input Pins

Input pins use interrupt-driven updates:
- Rising/falling edge triggers update the `Value` property
- Polling verification runs every `PollingInterval` to catch missed interrupts
- Mode changes automatically register/unregister interrupt handlers
- Interrupts are only registered for input pins (not output)

### Configuration Persistence

Pin configurations are persisted and restored on restart:
- When `GpioSubject` loads, existing pin configurations from JSON are reused
- New pins (not in configuration) default to `Input` mode
- Runtime changes to `Name` and `Mode` are persisted automatically

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
    pin.Value = true;

    // Read an input pin
    var buttonPin = gpioSubject.Pins[18];
    buttonPin.Name = "Power Button";
    buttonPin.Mode = GpioPinMode.InputPullUp;
    bool isPressed = !buttonPin.Value; // Pull-up means LOW when pressed
}

// Set Raspberry Pi standard pin names
gpioSubject.SetRaspberryPiPinNames();

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

### Input Debouncing

Software debouncing for mechanical switches and buttons. Configurable debounce time to filter out contact bounce noise from physical switches.
