# Configurable Subject Serialization

## Overview

The `ConfigurableSubjectSerializer` persists subject configuration to JSON.
It uses the `[Configuration]` attribute to determine which properties are saved.

## Example

```csharp
[InterceptorSubject]
public partial class GpioSubject : IConfigurableSubject
{
    [Configuration]
    public partial Mcp3008Configuration? Mcp3008 { get; set; }  // Saved

    [State]
    public partial bool IsConnected { get; set; }               // Not saved

    public partial Dictionary<int, GpioPin> Pins { get; set; }  // Not saved
}

public class Mcp3008Configuration  // Value object - all saved
{
    public int ClockPin { get; set; }
    public int MosiPin { get; set; }
    public int MisoPin { get; set; }
    public int ChipSelectPin { get; set; }
}
```

**Resulting JSON:**

```json
{
    "$type": "Namotion.Devices.Gpio.GpioSubject",
    "mcp3008": {
        "clockPin": 11,
        "mosiPin": 10,
        "misoPin": 9,
        "chipSelectPin": 8
    }
}
```

## Implementation

Uses `System.Text.Json` with custom `ConfigurationJsonTypeInfoResolver`:

```csharp
var options = new JsonSerializerOptions
{
    TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(),
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
};
```
