---
title: Configurable Subject Serialization
navTitle: Configurable Subjects
---

# Configurable Subject Serialization

## Overview

The `ConfigurableSubjectSerializer` persists subject configuration to JSON.
It uses the `[Configuration]` attribute to determine which properties are saved.

## Serialization Rules

| Type | What Gets Serialized |
|------|---------------------|
| `IInterceptorSubject` | Only properties marked with `[Configuration]` |
| Plain classes (value objects) | All public properties |

## Why Two Rules?

**Subjects** have mixed concerns:
- `[Configuration]` - persisted settings (saved)
- `[State]` - runtime values (not saved)
- `[Derived]` - computed properties (not saved)

**Value objects** referenced from `[Configuration]` properties are
entirely configuration by definition - no need to annotate every field.

## Example

```csharp
[InterceptorSubject]
public partial class GpioSubject : IConfigurable
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

Uses `System.Text.Json` with custom `ConfigurationJsonTypeInfoResolver`. The options for configuration files:

```csharp
var options = new JsonSerializerOptions
{
    TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(typeProvider, serviceProvider),
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
};
```

The serializer has a second set of options for the blocks of a markdown page, which name every property by its C# property name, like `TargetSpeed`. Each method of `ConfigurableSubjectSerializer` that reads or writes JSON takes a `ConfigurationNaming`: `JsonProperty` is the default and the naming of configuration files, and `CSharpProperty` is the naming of pages. See [Markdown Pages](../administration/pages.md#names) for the page side.

## Values That Cannot Be Read

A value whose JSON does not convert to the type of its property, such as text for a number, is skipped: the property keeps its default, or its value when a file is read again, and the other properties are read. `Deserialize` and `UpdateConfiguration` hand each skipped property to a caller that passes a callback for them, and `ReportIssues` turns them into the issues of the configuration. The storage and the root configuration log those issues and refuse to save a configuration that has any, because the file would lose the values that could not be read, see [Values That Cannot Be Read](../administration/subjects.md#values-that-cannot-be-read).

A key at the top level of a configuration file that names none of the configuration properties is ignored, so a file can keep the key of a property that a type no longer has. A subject that implements `IConfigurationIssueReceiver` opts out of that: `ReportIssues` hands it every issue, a skipped value as well as such a key, after its properties were set and before `ApplyConfigurationAsync`, so that it can show them, for example as a configuration error, and its configuration is not saved while it has any. A configurable subject nested in a configuration that collects the keys it does not know in a `[JsonExtensionData]` property, as an automation rule does, gets there the keys of its properties without `[Configuration]` as well.

