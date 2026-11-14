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

A value whose JSON does not convert to the type of its property, such as text for a number, is skipped: the property keeps its default, or its value when a file is read again, and the other properties are read. `Deserialize` and `UpdateConfiguration` hand each skipped property to a caller that passes a callback for them, and `DescribeSkippedProperties` names each by its key. The storage and the root configuration log them as a warning. Saving writes the configuration as it was loaded, so the file loses the values that could not be read, see [Values That Cannot Be Read](../administration/subjects.md#values-that-cannot-be-read) for how an administrator corrects the file.

A key at the top level of a configuration file that names none of the configuration properties is ignored, so a file can keep the key of a property that a type no longer has. A configurable subject nested in a configuration that collects the keys it does not know in a `[JsonExtensionData]` property, as an automation rule does, gets there the keys of its properties without `[Configuration]` as well.

## Updating in place or recreating

When the configuration of a subject changes, HomeBlaze either gives the running instance the new values and calls `ApplyConfigurationAsync`, or creates a new instance from the new configuration, as after a restart. It updates in place only when all of these hold:

1. The configuration describes the same type.
2. No key of the configuration applied last is missing. An update in place sets only the keys that are present, so a removed key would keep its old value.
3. No property marked `[Configuration(RecreateOnChange = true)]` has another value.

When the file or the block on a page is edited from outside, all three rules apply. When the configuration is saved from the edit dialog, it is what the instance already holds, so only rule 3 applies (the serializer leaves out null values and keys it does not know, which would otherwise count as missing), and the old instance is not applied again after a recreation.

Mark a property with `RecreateOnChange` when the running instance cannot apply a change of it, for example because it adds dynamic properties when it is attached and the registry cannot remove them. Values are compared as JSON. A missing key keeps the value on an update, so a value that disappears counts as unchanged only if it was null before, while a value that is newly written, null included, counts as changed. A hand-written value in another but equivalent form, such as an enum as a number, differs from what HomeBlaze writes, so the first save from the dialog creates such a subject again once.

A recreated subject is a new instance at the same path.
