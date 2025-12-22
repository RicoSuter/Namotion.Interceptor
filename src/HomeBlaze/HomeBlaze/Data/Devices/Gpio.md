---
title: GPIO
icon: Memory
---

# GPIO (General Purpose Input/Output)

The GPIO device provides access to GPIO pins on various Linux-based single-board computers and optional analog-to-digital converters (ADC).

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
- **Windows/macOS** - No pins available (empty pins dictionary) for development/testing

## Pin Configuration

Each GPIO pin supports four modes:

| Mode | Description |
|------|-------------|
| `Input` | Digital input (floating) |
| `InputPullUp` | Digital input with internal pull-up resistor |
| `InputPullDown` | Digital input with internal pull-down resistor |
| `Output` | Digital output |

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

## Analog Input (ADC)

The GPIO device supports two ADC chips:

### MCP3008

8-channel 10-bit ADC connected via SPI (software SPI on GPIO pins).

Configuration:
- **ClockPin** - SPI clock GPIO pin
- **MisoPin** - SPI MISO GPIO pin
- **MosiPin** - SPI MOSI GPIO pin
- **ChipSelectPin** - SPI chip select GPIO pin

### ADS1115

4-channel 16-bit ADC connected via I2C.

Configuration:
- **I2cBus** - I2C bus number (typically 1)
- **Address** - I2C address (default 0x48)

## Example Usage

```csharp
// Access a pin
var pin = gpioSubject.Pins[17];

// Check if pin is available
if (pin.Status == ServiceStatus.Running)
{
    // Set as output and turn on
    pin.Mode = GpioPinMode.Output;
    pin.Value = true;

    // Read an input pin
    var buttonPin = gpioSubject.Pins[18];
    buttonPin.Mode = GpioPinMode.InputPullUp;
    bool isPressed = !buttonPin.Value; // Pull-up means LOW when pressed
}

// Read analog value
if (gpioSubject.AnalogChannels.TryGetValue(0, out var channel))
{
    double voltage = channel.Value * 3.3; // Assuming 3.3V reference
}
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

### Write verification failed

The pin's `StatusMessage` will indicate "Write verification failed". This means:
- Possible short circuit on the pin
- External device driving the line
- Pin configured incorrectly in hardware

Check your wiring and ensure no external device is conflicting with the output.

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
