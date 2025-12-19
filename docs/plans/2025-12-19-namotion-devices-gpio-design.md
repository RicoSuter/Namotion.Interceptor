# Namotion.Devices.Gpio Design

A trackable object model for Raspberry Pi GPIO and optional analog I/O, built on Namotion.Interceptor.

## Overview

**Purpose:** Expose Raspberry Pi GPIO pins as a synchronized object tree. Changes to properties write to hardware; hardware changes update properties.

**Packages:**

| Package | Contents |
|---------|----------|
| `Namotion.Devices.Gpio` | Core subjects (GpioSubject, GpioPin, AnalogChannel, configurations) |
| `Namotion.Devices.Gpio.HomeBlaze` | Widget and EditComponent for HomeBlaze UI |

**Key Design Decisions:**

1. **Pin discovery** - Auto-detected from hardware, stable dictionary keys
2. **ADC support** - Optional, user-configured (Mcp3008, Ads1115)
3. **Higher-level devices** - Not included, app-specific concern
4. **Value objects** - Plain classes for configuration, fully serialized

## Package Structure

```
Namotion.Devices.Gpio/
├── GpioSubject.cs
├── GpioPin.cs
├── GpioPinMode.cs
├── AnalogChannel.cs
└── Configuration/
    ├── Mcp3008Configuration.cs
    └── Ads1115Configuration.cs

Namotion.Devices.Gpio.HomeBlaze/
├── _Imports.razor
├── GpioSubjectWidget.razor
└── GpioSubjectEditComponent.razor
```

## Dependencies

```
Namotion.Devices.Gpio
├── Namotion.Interceptor
├── System.Device.Gpio
└── Iot.Device.Bindings

Namotion.Devices.Gpio.HomeBlaze
├── Namotion.Devices.Gpio
├── HomeBlaze.Components.Abstractions
└── MudBlazor
```

## Subject Model

### GpioSubject

```csharp
[InterceptorSubject]
public partial class GpioSubject : BackgroundService, IConfigurableSubject, ITitleProvider, IIconProvider
{
    // Auto-discovered from hardware (stable dictionary, values change)
    public partial Dictionary<int, GpioPin> Pins { get; set; }

    // Optional ADC configurations (user-configured via edit component)
    [Configuration]
    public partial Mcp3008Configuration? Mcp3008 { get; set; }

    [Configuration]
    public partial Ads1115Configuration? Ads1115 { get; set; }

    // Populated when ADC is configured (empty if no ADC)
    public partial Dictionary<int, AnalogChannel> AnalogChannels { get; set; }

    public string? Title => "GPIO";
    public string? Icon => "Memory";

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        // React to configuration changes - initialize/dispose ADC hardware
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Discover pins from hardware
        // 2. Register pin change callbacks
        // 3. Poll ADC channels if configured
    }
}
```

### GpioPin

```csharp
[InterceptorSubject]
public partial class GpioPin
{
    [Configuration]
    public partial GpioPinMode Mode { get; set; }        // Input/Output/InputPullUp/InputPullDown

    [State]
    public partial bool Value { get; set; }              // Synced with hardware

    public partial int PinNumber { get; set; }           // Immutable
}
```

### AnalogChannel

```csharp
[InterceptorSubject]
public partial class AnalogChannel
{
    public partial int ChannelNumber { get; set; }

    [State]
    public partial double Value { get; set; }            // 0.0 - 1.0 normalized

    [State]
    public partial int RawValue { get; set; }            // 0 - 1023 (10-bit)
}
```

### Configuration Value Objects

```csharp
public class Mcp3008Configuration
{
    public int ClockPin { get; set; } = 11;
    public int MosiPin { get; set; } = 10;
    public int MisoPin { get; set; } = 9;
    public int ChipSelectPin { get; set; } = 8;
}

public class Ads1115Configuration
{
    public int I2cBus { get; set; } = 1;
    public int Address { get; set; } = 0x48;
}
```

### GpioPinMode Enum

```csharp
public enum GpioPinMode
{
    Input,
    InputPullUp,
    InputPullDown,
    Output
}
```

## UI Components

### GpioSubjectWidget

Dashboard widget showing GPIO state overview:
- Connection status
- Pin count with mode breakdown (X inputs, Y outputs)
- ADC status (enabled/disabled, channel count)

### GpioSubjectEditComponent

Configuration editor with sections:
1. **Pin Configuration Table** - Mode dropdown per pin, current value display
2. **MCP3008 ADC** - Enable checkbox, pin configuration when enabled
3. **ADS1115 ADC** - Enable checkbox, I2C configuration when enabled
4. **Analog Channels** - Read-only values when ADC configured

## Sample Configuration

**Data/Gpio.json:**

```json
{
    "type": "Namotion.Devices.Gpio.GpioSubject"
}
```

## Serialization Enhancement

### Problem

Current `ConfigurableSubjectSerializer` only filters `[Configuration]` on root subject. Nested subjects and value objects not handled correctly.

### Solution

Custom `JsonTypeInfoResolver` with clear rules:

| Type | Serialization |
|------|---------------|
| `IInterceptorSubject` | Only `[Configuration]` properties |
| Plain objects (value objects) | All properties |

### Implementation

```csharp
public class ConfigurationJsonTypeInfoResolver : DefaultJsonTypeInfoResolver
{
    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = base.GetTypeInfo(type, options);

        // Only filter for IInterceptorSubject types
        if (typeof(IInterceptorSubject).IsAssignableFrom(type) &&
            typeInfo.Kind == JsonTypeInfoKind.Object)
        {
            foreach (var property in typeInfo.Properties)
            {
                var hasConfigurationAttribute = property.AttributeProvider?
                    .GetCustomAttributes(typeof(ConfigurationAttribute), true)
                    .Any() ?? false;

                if (!hasConfigurationAttribute)
                {
                    property.ShouldSerialize = static (_, _) => false;
                }
            }
        }
        // Plain objects: regular serialization (all properties)

        return typeInfo;
    }
}
```

### Usage

```csharp
public class ConfigurableSubjectSerializer
{
    private readonly JsonSerializerOptions _jsonOptions;

    public ConfigurableSubjectSerializer(...)
    {
        _jsonOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(),
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public string Serialize(IInterceptorSubject subject)
    {
        return JsonSerializer.Serialize(subject, subject.GetType(), _jsonOptions);
    }
}
```

## Test Coverage

```csharp
public class ConfigurationJsonTypeInfoResolverTests
{
    // Basic rules
    [Fact]
    public void Serialize_Subject_OnlyIncludesConfigurationProperties()

    [Fact]
    public void Serialize_ValueObject_IncludesAllProperties()

    // Nested scenarios
    [Fact]
    public void Serialize_SubjectWithNestedValueObject_ValueObjectFullySerialized()

    [Fact]
    public void Serialize_SubjectWithNestedSubject_NestedSubjectFiltered()

    // Collections of subjects
    [Fact]
    public void Serialize_ListOfSubjects_EachSubjectFiltered()

    [Fact]
    public void Serialize_DictionaryOfSubjects_EachSubjectFiltered()

    // Collections of value objects
    [Fact]
    public void Serialize_ListOfValueObjects_AllPropertiesIncluded()

    [Fact]
    public void Serialize_DictionaryOfValueObjects_AllPropertiesIncluded()

    // Edge cases
    [Fact]
    public void Serialize_NullConfigurationProperty_OmittedFromOutput()

    [Fact]
    public void Serialize_EmptyCollection_SerializedAsEmptyArray()

    // Deserialization
    [Fact]
    public void Deserialize_Subject_PopulatesConfigurationProperties()

    [Fact]
    public void Deserialize_NestedValueObject_FullyPopulated()

    [Fact]
    public void Deserialize_ListOfSubjects_AllElementsPopulated()
}
```

## Graceful Degradation

When GPIO hardware is unavailable (dev machine, container, non-Pi):

- `Pins` dictionary is empty
- `AnalogChannels` dictionary is empty
- No crash, subject still exists in object tree
- UI renders empty state
- Configuration can still be edited and saved

This enables development and testing on non-Raspberry Pi machines.

## Hardware APIs

Built on Microsoft's official .NET IoT libraries:

- **System.Device.Gpio** - Digital I/O, PWM, I2C, SPI
- **Iot.Device.Bindings** - ADC chips (Mcp3008, Ads1115, etc.)

### GPIO Abstraction

```csharp
// Digital I/O
GpioController controller = new GpioController();
controller.OpenPin(17, PinMode.Output);
controller.Write(17, PinValue.High);

// Events for input changes
controller.RegisterCallbackForPinValueChangedEvent(
    pin: 17,
    eventTypes: PinEventTypes.Rising | PinEventTypes.Falling,
    callback: OnPinChanged);
```

### ADC (Analog Input)

Raspberry Pi has no native analog I/O. External ADC chips required:

| Chip | Interface | Resolution | Channels |
|------|-----------|------------|----------|
| MCP3008 | SPI | 10-bit | 8 |
| ADS1115 | I2C | 16-bit | 4 |

## References

- [System.Device.Gpio on NuGet](https://www.nuget.org/packages/System.Device.Gpio/)
- [Microsoft Learn - GPIO Input Tutorial](https://learn.microsoft.com/en-us/dotnet/iot/tutorials/gpio-input)
- [dotnet/iot GitHub - Mcp3xxx](https://github.com/dotnet/iot/blob/main/src/devices/Mcp3xxx/README.md)
- [Custom JSON serialization contracts](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/custom-contracts)
