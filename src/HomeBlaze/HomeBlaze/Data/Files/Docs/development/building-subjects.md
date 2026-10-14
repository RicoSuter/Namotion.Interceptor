---
title: Building Custom Subjects
navTitle: Building Subjects
position: 0
---

# Building Custom Subjects

This guide explains how to create custom subjects for HomeBlaze, using the Motor example as reference.

## What is a Subject?

A **subject** is an intercepted object in the HomeBlaze object graph. Subjects can:
- Have **configuration** properties that persist to JSON
- Have **state** properties that display in the UI
- Have **derived** properties that auto-update when dependencies change
- Implement interfaces for display metadata (title, icon)
- Run background tasks via `BackgroundService`

## Minimal Subject

The simplest subject only needs the `[InterceptorSubject]` attribute:

```csharp
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class Sensor
{
    public partial double Value { get; set; }
}
```

**Key requirements:**
- Class must be `partial`
- Properties you want tracked must be `partial`
- Add `[InterceptorSubject]` attribute

## Configuration Properties

Properties marked with `[Configuration]` are persisted to JSON:

```csharp
using HomeBlaze.Abstractions.Attributes;

[InterceptorSubject]
public partial class Motor
{
    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial int TargetSpeed { get; set; }

    [Configuration]
    public partial TimeSpan SimulationInterval { get; set; }
}
```

**When to use:**
- Settings that should survive restarts
- User-configurable values
- Connection strings, intervals, thresholds

### Secret Configuration

For sensitive values like API keys and passwords, use `IsSecret = true`:

```csharp
[Configuration(IsSecret = true)]
public partial string? AppKey { get; set; }
```

Secret configuration properties:
- Are **persisted** to JSON like regular configuration
- Are **hidden** in the browser property panel
- Show as **masked password fields** in the edit dialog (empty by default, only overwritten when the user enters a new value)
- **Cannot** be combined with `[State]`: the app throws at startup to prevent accidental exposure

## State Properties

Properties marked with `[State]` are displayed in the UI property panel:

```csharp
[InterceptorSubject]
public partial class Motor
{
    [State("Speed", Position = 1)]
    public partial int CurrentSpeed { get; set; }

    [State(Unit = StateUnit.DegreeCelsius, Position = 2)]
    public partial double Temperature { get; set; }

    [State(IsDiscrete = true, Position = 3)]
    public partial MotorStatus Status { get; set; }
}
```

**State attribute options:**

| Option | Description | Example |
|--------|-------------|---------|
| `Name` | Display name (overrides property name) | `"Speed"` |
| `Position` | Sort position in property panel | `1`, `2`, `3` |
| `Unit` | Formatting unit | `StateUnit.DegreeCelsius` |
| `IsCumulative` | Counter that only grows, see [Naming Cumulative Properties](#naming-cumulative-properties) | `true` for energy meters, rain totals |
| `IsDiscrete` | Discrete variable (binary on/off, every transition matters) vs analog (sensor readings) | `true` for commands, flags |
| `IsEstimated` | Calculated/estimated value | `true` for predictions |

**Available units:**

```csharp
public enum StateUnit
{
    Default,                // No formatting
    Percent,                // 0.75 shown as 75%
    DegreeCelsius,          // 23.5 °C
    Watt,                   // 100 W
    Kilowatt,               // 1.5 kW
    WattHour,               // 500 Wh
    Volt,                   // 230 V
    Ampere,                 // 5 A
    Hertz,                  // 50 Hz
    Lumen,                  // 800 lm
    Lux,                    // 500 lx
    Meter,                  // 1.5 m
    Kilometer,              // 12.3 km
    Millimeter,             // 10 mm
    MillimeterPerHour,      // 5 mm/h
    Kilobyte,               // 1024 KB
    KilobytePerSecond,      // 100 KB/s
    MegabitPerSecond,       // 100 Mbps
    LiterPerHour,           // 50 L/h
    KilowattHour,           // 500 kWh
    Milliampere,            // 100 mA
    Currency,               // $10.00
    HexColor,               // #FF0000
    MeterPerSecond,         // 3.2 m/s
    Hectopascal,            // 1013 hPa
    Degree,                 // 270° (angles such as wind direction)
    UvIndex,                // 5 UV
    Byte,                   // 512 B
    Kelvin,                 // 1.5 K (temperature differences)
    Minute,                 // 30 min
    Hour,                   // 1234 h
    VoltAmpere,             // 880 VA (apparent power)
    VoltAmpereReactive,     // 1.5 kvar (reactive power)
    VoltAmpereHour,         // 2.5 kVAh (apparent energy)
    VoltAmpereReactiveHour, // 2.5 kvarh (reactive energy)
    AmpereHour,             // 50 Ah (charge, battery capacity)
    WattPerSquareMeter      // 850 W/m² (irradiance)
}
```

Values are auto-scaled for display within unit families (e.g., 1500 W displays as "1.5 kW", 0.5 A displays as "500 mA"). A `Percent` value, state or operation parameter, is a fraction from 0 to 1: it displays as 0 to 100 %, the operation dialog takes 0 to 100 and divides by 100, and MCP `invoke_method` passes the fraction unchanged.

### Naming Cumulative Properties

A property with `IsCumulative = true` is a counter that only grows and is named `Total<Modifier><Quantity>`, with the modifier (such as a direction) before the quantity like in plain English: `TotalConsumedEnergy`, `TotalImportedEnergy`, `TotalExportedEnergy`, `TotalProducedThermalEnergy`, `TotalRain`, `TotalOperatingHours`. The `Total` prefix is reserved for such counters. A value that sums parts at one instant, such as the power or current of all phases of a meter, takes no prefix (`MeasuredPower`, `ElectricalCurrent`), and a value that covers a period, such as a charging session or a day, is not cumulative and does not start with `Total`.

The subject keeps the value from decreasing: if the API reports periodic buckets (daily, monthly) that reset, the subject accumulates them into a true counter and persists the offset via `[Configuration]`. Only a reset of the device or service that owns the counter (for example a reboot that clears it) may restart the value at 0 or step it back once, and a change of the counter's source passes through `null`. A consumer that computes deltas treats a decrease as such a reset and restarts after `null`, counting no change for that interval.

### Interface Inheritance and Attribute Merging

When a class implements an interface with `[State]` properties, the attributes are **merged**. Class values take priority; interface values fill in any fields the class doesn't specify.

This means you can add `Position` on the class without re-specifying `Unit` from the interface:

```csharp
// Interface provides Unit
public interface IPowerSensor
{
    [State(Unit = StateUnit.Watt)]
    decimal? Power { get; }
}

// Class adds Position; Unit is inherited from the interface
[InterceptorSubject]
public partial class MyDevice : IPowerSensor
{
    [Derived]
    [State(Position = 1)]
    public decimal? Power => InternalPower;  // Merged: Position=1, Unit=Watt
}
```

Properties without any `Position` (from class or interface) sort **last** in the UI panel.

### Position Ranges

HomeBlaze abstraction interfaces use reserved position ranges to ensure consistent default ordering when a device implements multiple interfaces:

| Range | Kind of property | Examples |
|-------|------------------|---------|
| 0–99 | Device-specific primary (custom per class) | Pin number, enabled state |
| 100–199 | Primary device function | Switch on/off, brightness, cover position |
| 200–299 | Sensor measurements | Temperature, humidity, presence |
| 300–399 | Power & energy | Power, energy, voltage, current |
| 400–499 | Device-specific details | Uptime, source, firmware |
| 800–849 | Device identity | Manufacturer, model, serial number |
| 850–899 | Software & networking | Firmware version, IP address |
| 900–999 | Connection & service status | IsConnected, Status, StatusMessage |

Concrete classes can override interface positions via the merge. Use ranges 0–99 and 400–499 for class-specific properties.

## Derived Properties

Properties marked with `[Derived]` auto-update when their dependencies change:

```csharp
[InterceptorSubject]
public partial class Motor
{
    [Configuration]
    public partial int TargetSpeed { get; set; }

    [State]
    public partial int CurrentSpeed { get; set; }

    [Derived]
    [State("Delta")]
    public int SpeedDelta => TargetSpeed - CurrentSpeed;

    [Derived]
    [State("At Target")]
    public bool IsAtTargetSpeed => Math.Abs(SpeedDelta) < 50;
}
```

**Key points:**
- Must be expression-bodied (`=>`) or have only a getter
- Cannot be `partial` (they're computed, not stored)
- Can combine with `[State]` to display in UI
- Automatically recalculates when `TargetSpeed` or `CurrentSpeed` changes

## Child Subject Collections

When a subject owns child subjects (e.g., a bridge owns lights), the collection property type determines how paths are built. Since paths are used as persistent identifiers throughout HomeBlaze (automation rules, dashboards, configuration references), path stability is critical.

**Use `Dictionary<string, T>` when child identity comes from an external system:**

```csharp
[State]
public partial Dictionary<string, HueLightbulb> Lights { get; set; }
// Path: /Devices/Hue/Lights[a1b2c3d4-...], stable across restarts
```

**Use arrays when the index itself is the stable identity:**

```csharp
[State]
public partial GpioPin[] Pins { get; set; }
// Path: /Devices/Gpio/Pins[0], pin 0 is always pin 0
```

**Rule of thumb:** If reordering or re-discovery could change which item is at index N, use a dictionary with a stable key (e.g., hardware ID, API resource ID). If the index has inherent meaning (hardware pin numbers, fixed slots), arrays are fine.

**Dictionary keys must be:**
- Stable across restarts and reconnections
- Unique within the collection
- Deterministic (same device always produces the same key)

Good key sources: API resource IDs (GUIDs), hardware serial numbers, MAC addresses. Bad key sources: user-assignable names, discovery order, timestamps.

## Display Interfaces

Implement these interfaces for UI integration:

### ITitleProvider

```csharp
public partial class Motor : ITitleProvider
{
    [Configuration]
    public partial string Name { get; set; }

    public string? Title => Name;  // Shows in navigation, browser, etc.
}
```

### IIconProvider

```csharp
public partial class Motor : IIconProvider
{
    public string? IconName => "Settings";  // Name of a Material filled icon, shown in navigation, browser, etc.
}
```

## Create Dialog

Every registered, non-abstract subject type that implements `IConfigurable` is offered in the create dialog, which is opened from a storage folder, from the markdown editor and from the grid and canvas layouts. The dialog shows a searchable list of types with a group sidebar, and then a configure page for the chosen type. The configure page shows the type's Setup component when it has one, otherwise its Edit component, and otherwise the generated properties editor for its `[Configuration]` properties. Setup and Edit components receive `IsCreating = true`. "Back" on the configure page returns to the list. Typing in the search box selects the group "All" so that a match is never hidden behind a group that was selected before, and selecting a group during a search narrows the matches to it.

The dialog describes a type with attributes on the class. All of them are optional:

| Attribute | Purpose | Without it |
|-----------|---------|------------|
| `[DisplayName("Motor")]` | Title in the list and in the dialog title, in sentence case | Class name split into words in sentence case: `ShellyDevice` becomes "Shelly device" and `HTTPServer` becomes "HTTP server" |
| `[Description("...")]` | One sentence below the title, also searched | No description |
| `[Category("Samples")]` | Group in the sidebar, see [Groups](#groups) | Group "Other", listed last |
| `[SubjectIcon("Settings")]` | Icon in the list, using the same names as `IIconProvider.IconName`. An unknown icon name shows the generic article icon | Icon of the group for a [built-in group](#groups), otherwise a generic icon |

`[DisplayName]`, `[Description]` and `[Category]` come from `System.ComponentModel`, `[SubjectIcon]` from `HomeBlaze.Abstractions.Attributes`. The create dialog attributes, including the two under [Hiding a Type](#hiding-a-type), are read from the class itself and are not inherited from a base class.

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

[Category("Samples")]
[Description("Simulated motor with speed control and temperature monitoring")]
[DisplayName("Motor")]
[SubjectIcon("Settings")]
[InterceptorSubject]
public partial class Motor : BackgroundService, IConfigurable
{
    // ...
}
```

`[SubjectIcon]` is needed in addition to `IIconProvider` because the dialog has no instance to ask, and an instance's icon often depends on its state.

### Groups

A type belongs to exactly one group, the one that its `[Category]` names. The sidebar lists the groups alphabetically, with "Other" last. Use the built-in group that says what the type is for:

| Group | Use it for |
|-------|------------|
| Climate | Heating, cooling and weather |
| Dashboard | Layouts and the widgets that are placed in them |
| Energy | Inverters, energy meters, batteries and vehicle chargers |
| Hardware | Local hardware access, such as GPIO pins |
| History | Stores that record history |
| Integrations | Connections to external systems, such as an OPC UA client or server |
| Lighting | Lights and lighting bridges |
| Media | Audio and video players |
| Samples | Examples and simulated devices |
| Switches | Switches, relays and smart plugs |
| System | Parts of HomeBlaze itself, such as storage and plugins |

When none of them fits, name a new group in `[Category]`. Using the name is all it takes to create the group, and group names are compared ignoring case. A type in such a group that sets no `[SubjectIcon]` gets a generic icon.

On a property or a method, `[Category]` names a [category](#categories) of the property panel instead.

### Name of a New Subject

Where the new subject needs a name, the dialog asks for it on the configure page and suggests the class name, with a number appended when it is already used (`Motor`, `Motor2`). A name that already exists in the same place is rejected, and the messages name the field by its label ("This file name is already used").

- In a storage folder the field is labelled "File name" and shows the suffix `.json`. The file name is part of the subject's path, and the path is its ID, so it should not be changed later.
- In the markdown editor the field is labelled "Block name" and has no suffix. It becomes the `#name` of the new block, so it must also be a valid block name: it starts with a letter or `_` and contains only letters, digits, `_` and `-`.
- The grid and canvas layouts ask for no name.

### Hiding a Type

Two attributes from `HomeBlaze.Abstractions.Attributes` control whether a type is offered:

- `[ExcludeFromCreation]`: the type is never offered. Use it for a type that only exists as part of another subject, such as the cell of a grid.
- `[SingleInstance]`: the type is offered only while no instance of it exists in the subject graph. An instance of a derived type does not count.

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

[ExcludeFromCreation]
[InterceptorSubject]
public partial class GridCell : IConfigurable
{
    // ...
}
```

## Configuration Lifecycle

Implement `IConfigurable` to react when configuration changes:

```csharp
public partial class Motor : IConfigurable
{
    [Configuration]
    public partial string ConnectionString { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken ct = default)
    {
        // Called after [Configuration] properties are updated
        // Use this to:
        // - Reconnect to external systems
        // - Recalculate cached values
        // - Validate and fix invalid combinations
        return Task.CompletedTask;
    }
}
```

## Operations

Methods marked with `[Operation]` become executable from the UI. Operations are actions with side effects like starting a process, resetting state, or sending commands.

```csharp
using HomeBlaze.Abstractions.Attributes;

[InterceptorSubject]
public partial class Motor
{
    [State]
    public partial int TargetSpeed { get; set; }

    [State]
    public partial MotorStatus Status { get; set; }

    [Operation(Title = "Set Speed", Description = "Sets the motor target speed", Position = 1)]
    public void SetTargetSpeed(int speed)
    {
        TargetSpeed = Math.Clamp(speed, 0, 3000);
    }

    [Operation(Title = "Emergency Stop", RequiresConfirmation = true, Position = 2)]
    public void EmergencyStop()
    {
        TargetSpeed = 0;
        Status = MotorStatus.Stopped;
    }

    [Query(Title = "Get Diagnostics", Position = 3)]
    public MotorDiagnostics GetDiagnostics()
    {
        return new MotorDiagnostics { Status = Status, Speed = TargetSpeed };
    }

    [Operation(Title = "Run Test", Position = 4)]
    public async Task RunTestAsync(int speed, int durationSeconds)
    {
        var previousSpeed = TargetSpeed;
        TargetSpeed = speed;
        await Task.Delay(TimeSpan.FromSeconds(durationSeconds));
        TargetSpeed = previousSpeed;
    }
}
```

**Operation attribute options:**

| Option | Description | Example |
|--------|-------------|---------|
| `Title` | Display name (defaults to method name without "Async") | `"Set Speed"` |
| `Description` | Help text shown in the operation dialog and returned to agents by MCP `list_methods`, so write it to stand alone | `"Sets the motor target speed"` |
| `Icon` | MudBlazor icon name | `"Speed"`, `"Stop"` |
| `Position` | Sort position in operations list | `1`, `2`, `3` |
| `RequiresConfirmation` | Show confirmation dialog before executing | `true` |

**Key points:**
- Operations appear as buttons in the subject's property panel, grouped by [category](#categories)
- Parameters with supported types (primitives, enums, nullable variants) show input dialogs
- Async methods are supported and show a progress indicator
- Methods returning values display the result in a dialog
- Errors are caught and shown in an error dialog
- Operations with `RequiresConfirmation = true` show a confirmation dialog first

**Supported parameter types:**
- `string`, `int`, `long`, `double`, `float`, `decimal`, `bool`
- `DateTime`, `DateTimeOffset`, `Guid`, `TimeSpan`
- Nullable versions of the above (`int?`, `bool?`, etc.)
- Enums and nullable enums

**Parameter units:**

`[OperationParameter(Unit = ...)]` gives a parameter a `StateUnit`. The operation dialog shows the unit after the input, and MCP `list_methods` returns it in the parameter's `description`.

```csharp
[Operation(Title = "Set Volume", Description = "Sets the volume of this player.")]
public Task SetVolumeAsync([OperationParameter(Unit = StateUnit.Percent)] decimal volume, CancellationToken cancellationToken)
```

A `Percent` parameter is a fraction from 0 to 1, as described under units above: the dialog takes 0 to 100 and divides by 100, while agents pass the fraction. The description is shown to both, so it leaves the numeric range to the unit instead of stating "0 to 1".

**Operations vs Configuration:**
- Use `[Configuration]` for values that should persist and can be edited at any time
- Use `[Operation]` for one-time actions that execute immediately

### Conditional Operations (IsEnabled)

Use `[PropertyAttribute]` with `KnownAttributes.IsEnabled` to conditionally enable/disable operation buttons based on runtime state:

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Registry.Attributes;

[InterceptorSubject]
public partial class Server
{
    [State]
    public partial ServiceStatus Status { get; set; }

    [Operation(Title = "Start", Position = 1)]
    public Task StartAsync() { /* ... */ }

    [Derived]
    [PropertyAttribute("Start", KnownAttributes.IsEnabled)]
    public bool Start_IsEnabled => Status == ServiceStatus.Stopped || Status == ServiceStatus.Error;

    [Operation(Title = "Stop", Position = 2)]
    public Task StopAsync() { /* ... */ }

    [Derived]
    [PropertyAttribute("Stop", KnownAttributes.IsEnabled)]
    public bool Stop_IsEnabled => Status == ServiceStatus.Running;
}
```

**Key points:**
- The property attribute name (e.g., `"Start"`) must match the method name without the `Async` suffix
- The `IsEnabled` property should be `[Derived]` so it automatically updates when dependencies change
- When `IsEnabled` is `false`, the button is disabled in the UI
- This uses the `TrackingScope` for automatic re-rendering when the derived property changes

## Categories

`[Category("...")]` from `System.ComponentModel` on a `[State]` property or on an `[Operation]` or `[Query]` method puts it under a heading of that name in the property panel, below the members without a category.

```csharp
[State("Speed", Position = 3)]
[Category("Speed")]
public partial int CurrentSpeed { get; set; }

[Operation(Title = "Set Speed", Position = 1)]
[Category("Speed")]
public void SetTargetSpeed(int speed) { /* ... */ }
```

- Categories are ordered by the lowest `Position` of their state properties. Categories with only methods come last, and names break ties.
- A `[Category]` on an interface member applies to the class member that implements it. One on the class member wins, and `[Category("")]` there removes the category. The shared interfaces in `HomeBlaze.Abstractions` carry "Device", "Network", "Light", "Playback", "Volume" and "Track" this way.
- Names are case-sensitive. .NET shows "Default" as "Misc", "Config" as "Configurations", "DragDrop" as "Drag Drop" and "WindowStyle" as "Window Style".

## Background Services

Extend `BackgroundService` for subjects that need to run continuously:

```csharp
[InterceptorSubject]
public partial class Motor : BackgroundService
{
    [State]
    public partial MotorStatus Status { get; set; }

    [State]
    public partial int CurrentSpeed { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Status = MotorStatus.Starting;
        await Task.Delay(500, stoppingToken);
        Status = MotorStatus.Running;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Update state
            CurrentSpeed = await ReadSpeedFromHardware();

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }

        Status = MotorStatus.Stopped;
    }
}
```

**The host automatically:**
- Starts `BackgroundService` subjects when loaded
- Stops them gracefully on shutdown
- Handles cancellation via `stoppingToken`

## Property Attributes

Add metadata to properties using `[PropertyAttribute]`:

```csharp
[InterceptorSubject]
public partial class Motor
{
    [Configuration]
    [State("Target")]
    public partial int TargetSpeed { get; set; }

    [PropertyAttribute(nameof(TargetSpeed), "Minimum")]
    public partial int TargetSpeed_Minimum { get; set; }

    [PropertyAttribute(nameof(TargetSpeed), "Maximum")]
    public partial int TargetSpeed_Maximum { get; set; }
}
```

This associates `TargetSpeed_Minimum` and `TargetSpeed_Maximum` as metadata for `TargetSpeed`, enabling UI features like range sliders.

## Complete Example: Motor

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Samples;

[Category("Samples")]
[Description("Simulated motor with speed control and temperature monitoring")]
[DisplayName("Motor")]
[SubjectIcon("Settings")]
[InterceptorSubject]
public partial class Motor : BackgroundService, IConfigurable, ITitleProvider, IIconProvider
{
    // Configuration (persisted to JSON)

    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    [State("Target", Position = 2)]
    [Category("Speed")]
    public partial int TargetSpeed { get; set; }

    [PropertyAttribute(nameof(TargetSpeed), "Minimum")]
    public partial int TargetSpeed_Minimum { get; set; }

    [PropertyAttribute(nameof(TargetSpeed), "Maximum")]
    public partial int TargetSpeed_Maximum { get; set; }

    [Configuration]
    public partial TimeSpan SimulationInterval { get; set; }

    // Live state (not persisted)

    [State("Speed", Position = 3)]
    [Category("Speed")]
    public partial int CurrentSpeed { get; set; }

    [State(Unit = StateUnit.DegreeCelsius, Position = 4)]
    public partial double Temperature { get; set; }

    [State(Position = 1)]
    public partial MotorStatus Status { get; set; }

    // Derived properties

    [Derived]
    [State("Delta", Position = 5)]
    [Category("Speed")]
    public int SpeedDelta => TargetSpeed - CurrentSpeed;

    [Derived]
    [State("At Target", Position = 6)]
    [Category("Speed")]
    public bool IsAtTargetSpeed => Math.Abs(SpeedDelta) < 50;

    // Display interfaces

    public string? Title => Name;
    public string? IconName => "Settings";

    // Constructor with defaults

    public Motor()
    {
        Name = string.Empty;
        TargetSpeed = 0;
        TargetSpeed_Minimum = 0;
        TargetSpeed_Maximum = 3000;
        SimulationInterval = TimeSpan.FromSeconds(1);
        Status = MotorStatus.Stopped;
    }

    // Background execution

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Status = MotorStatus.Starting;
        await Task.Delay(500, stoppingToken);
        Status = MotorStatus.Running;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Simulate speed approaching target
            if (CurrentSpeed < TargetSpeed)
                CurrentSpeed = Math.Min(CurrentSpeed + 50, TargetSpeed);
            else if (CurrentSpeed > TargetSpeed)
                CurrentSpeed = Math.Max(CurrentSpeed - 50, TargetSpeed);

            // Simulate temperature based on speed
            Temperature = 25.0 + (CurrentSpeed / 1000.0 * 20.0);

            await Task.Delay(SimulationInterval, stoppingToken);
        }

        Status = MotorStatus.Stopped;
    }

    // Configuration lifecycle

    public Task ApplyConfigurationAsync(CancellationToken ct = default)
    {
        // React to configuration changes if needed
        return Task.CompletedTask;
    }
}

public enum MotorStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}
```

## Project Structure

**Recommended:** Separate subject implementation from UI components into different projects.

```
MyModule/
+-- MyModule/                    # Subject implementations (no UI)
+-- MyModule.Blazor/             # Blazor components for subjects
```

### Subject Project (No UI)

The subject project should only depend on `Namotion.Interceptor` and optionally `HomeBlaze.Abstractions`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <!-- Core interception - required -->
    <ProjectReference Include="..\Namotion.Interceptor\Namotion.Interceptor.csproj" />
    <ProjectReference Include="..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />

    <!-- HomeBlaze integration - for [Configuration], [State], ITitleProvider, etc. -->
    <ProjectReference Include="..\HomeBlaze.Abstractions\HomeBlaze.Abstractions.csproj" />

    <!-- Optional: For BackgroundService base class -->
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.*" />
  </ItemGroup>
</Project>
```

**Why separate?**
- Subjects can be used in headless scenarios (APIs, console apps, background services)
- No dependency on Blazor, MudBlazor, or any UI framework
- Easier testing without UI dependencies
- Cleaner dependency graph

### UI Project (Blazor Components)

Create a separate project for Blazor components that visualize or edit your subjects:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <!-- Reference your subject project -->
    <ProjectReference Include="..\MyModule\MyModule.csproj" />

    <!-- HomeBlaze UI infrastructure -->
    <ProjectReference Include="..\HomeBlaze.Host\HomeBlaze.Host.csproj" />
  </ItemGroup>
</Project>
```

### Example: Motor Module Structure

```
HomeBlaze.Samples/           # Subject implementations
    Motor.cs                 # [InterceptorSubject] class
    MotorStatus.cs          # Enum

HomeBlaze.Samples.Blazor/    # UI components (optional)
    MotorDashboard.razor    # Custom visualization
    MotorEditComponent.razor # Custom editor
```

The `HomeBlaze.Samples` project has no Blazor dependencies - it can run in any .NET application. The optional `.Blazor` project adds rich UI when running in HomeBlaze

## JSON Configuration File

Subjects are configured via JSON files. The Motor would be saved as:

```json
{
  "$type": "HomeBlaze.Samples.Motor",
  "Name": "Pump Motor",
  "TargetSpeed": 1500,
  "SimulationInterval": "00:00:01"
}
```

Only `[Configuration]` properties are persisted. The `$type` field enables polymorphic deserialization.

## Summary

| Attribute | Purpose | Persisted | Displayed |
|-----------|---------|-----------|-----------|
| `[Configuration]` | User-editable settings | Yes | No (unless also `[State]`) |
| `[Configuration(IsSecret = true)]` | Sensitive settings (API keys, passwords) | Yes | Never (masked in editor) |
| `[State]` | Live values for display | No | Yes |
| `[Configuration] + [State]` | Editable and displayed | Yes | Yes |
| `[Configuration] + [SubjectPath]` | Path with a picker, see [Path Properties](#path-properties) | Yes | No (unless also `[State]`) |
| `[Derived]` | Computed from other properties | No | Only if also `[State]` |
| `[Operation]` | Executable actions from UI | No | Yes (as buttons) |
| `[Category]` on a property or method | Heading in the property panel, see [Categories](#categories) | No | Yes |
| `[Slot]` | Content that a markdown page fills in | No | Rendered by the widget |
| `[DisplayName]`, `[Description]`, `[Category]`, `[SubjectIcon]` | Describe the type in the create dialog | No | In the create dialog |
| `[ExcludeFromCreation]`, `[SingleInstance]` | Hide the type from the create dialog | No | No |

| Interface | Purpose |
|-----------|---------|
| `ITitleProvider` | Display name in navigation |
| `IIconProvider` | Icon in navigation/browser |
| `IConfigurable` | React to configuration changes |
| `IMonitoredService` | Background service with status monitoring |
| `IConnectionState` | Connection state tracking (IsConnected) |
| `IHubDevice` | Hub/bridge that manages child devices |
| `IUnknownDevice` | Generic/unrecognized device |
| `IVirtualSubject` | Virtual/computed subject (e.g., rooms, zones) |

---

## Abstraction Libraries

HomeBlaze provides several abstraction packages with standard interfaces that subjects can implement. These enable interoperability between plugins. For example, a dashboard can display any subject implementing `ITemperatureSensor`, regardless of which plugin provides it.

### HomeBlaze.Abstractions

Core platform interfaces and attributes:

| Area | Contents |
|------|----------|
| Attributes | `[Configuration]`, `[State]`, `[Operation]`, `[Query]`, `[Slot]`, `[SubjectPath]`, `[SubjectIcon]`, `[ExcludeFromCreation]`, `[SingleInstance]`, `StateUnit` |
| Display | `ITitleProvider`, `IIconProvider` |
| Lifecycle | `IConfigurable`, `IMonitoredService`, `IServerSubject` |
| Messaging | `IEvent`, `ICommand`, `IMessageBus`, `INotificationPublisher` |
| Security | `IAuthenticatedSubject` |
| Networking | `IConnectionState` |
| Devices | Switches, lighting, covers, locks, energy, media, input devices |
| Sensors | Temperature, humidity, presence, light, door, rain, soil, power, camera |

Subjects implement these interfaces to declare their capabilities. For example, a Zigbee temperature sensor plugin would implement `ITemperatureSensor`, and a Philips Hue light plugin would implement `ILightbulb` (which composes `ISwitchDevice`, `IBrightnessController`, `IColorController`).

Device interfaces compose small capability interfaces, split into state and controller. A speaker implements `IAudioPlayer`, which composes playback (`IMediaPlaybackState`, `IMediaPlaybackController`), volume (`IVolumeState`, `IVolumeController`) and the current track (`IMediaTrackState`). A device with only some of these, such as a receiver with volume but no playback, implements just the capability interfaces it has.

### HomeBlaze.Storage.Abstractions

Storage contracts: `IStorageContainer`, `IStorageFile`, `IConfigurationWriter`, `StorageStatus`, `[FileExtension]`.

### HomeBlaze.Components.Abstractions

UI component contracts: `ISubjectComponent`, `ISubjectEditComponent`, `[SubjectComponent]`, `SubjectComponentType`.

See [Project Structure](../architecture/project-structure.md) for the full dependency graph between these packages.

---

## Advanced Patterns

### Constructor Injection

Subjects can inject services via constructor. The system uses `ActivatorUtilities.CreateInstance` to resolve dependencies from the DI container. This also holds for a subject that is nested in the configuration of another subject, for example the child of a grid cell, and for a subject that a block on a markdown page creates.

**Basic pattern** (HomeBlaze services only):

```csharp
[InterceptorSubject]
public partial class Widget : IConfigurable
{
    private readonly SubjectPathResolver _pathResolver;
    private readonly RootManager _rootManager;

    [Configuration]
    public partial string Path { get; set; }

    public Widget(SubjectPathResolver pathResolver, RootManager rootManager)
    {
        _pathResolver = pathResolver;
        _rootManager = rootManager;
        Path = string.Empty;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
```

### Available Injectable Services

| Service | Purpose |
|---------|---------|
| `SubjectPathResolver` | Resolve subjects from paths |
| `RootManager` | Access root subject |
| `SubjectTypeRegistry` | Resolve types by name |
| `ConfigurableSubjectSerializer` | Serialize/deserialize subjects |
| `IHttpClientFactory` | HTTP client management |
| `ILogger<T>` | Logging |
| `IInterceptorSubjectContext` | Interceptor context for property tracking |

### Referencing Other Subjects

Use paths to reference subjects in the object graph. See [Paths](../administration/paths.md) for full documentation.

**Quick Reference:**

| Prefix | Example |
|--------|---------|
| `/` (absolute) | `/Demo/Conveyor` |
| `./` (relative) | `./Child/Property` |
| `../` (parent) | `../Sibling/Property` |
| Brackets | `/Devices[0]` (for non-inlined collection indices) |

Use `SubjectPathResolver` to resolve paths in code. A subject that takes a path from its configuration calls `ResolveScopedSubject` and passes itself:

```csharp
[Derived]
public IInterceptorSubject? ResolvedSubject => ResolveSubject();

private IInterceptorSubject? ResolveSubject()
{
    if (string.IsNullOrEmpty(Path))
        return null;

    // Handles /, ./, ../, relative paths and the names of blocks on a markdown page
    return _pathResolver.ResolveScopedSubject(Path, PathStyle.Canonical, this);
}
```

`ResolveScopedSubject` and `ResolveScopedValue` are extension methods in `HomeBlaze.Services`. When the subject is on a markdown page, a relative path starts at the page and its first segment can be the name of a block on that page, as [Paths](../administration/paths.md) describes for page authors. When the subject is not on a page, they resolve relative to the subject, like `ResolveSubject` and `ResolveValue` with the subject as the third argument.

### Path Properties

`[SubjectPath]` on a `string` `[Configuration]` property makes the generated properties editor show a [`SubjectPathField`](#subjectpathfield-component), so that the path can be picked as well as typed. Without a type the path leads to a property, and with a type to a subject of that type, which can be an interface:

```csharp
[Configuration]
[SubjectPath]
public partial string? Path { get; set; }

[Configuration]
[SubjectPath(typeof(ValueMap))]
public partial string? Map { get; set; }
```

The attribute only changes the editor: the subject still resolves the path itself. The picker returns absolute paths, so a relative path has to be typed.

### SubjectPathField Component

In an Edit component of your own, use the `SubjectPathField` component for a path. It provides a text input with a browse button that opens a tree picker dialog:

```razor
@using HomeBlaze.Components.Inputs

<SubjectPathField @bind-Value="_path"
                  @bind-Value:after="OnFieldChanged"
                  Label="Subject Path"
                  Placeholder="e.g., / or /Demo/Conveyor"
                  Class="mt-4" />
```

**Features:**
- Text input for direct path entry
- Search icon button opens tree picker dialog
- Tree shows all subjects with lazy loading
- State properties and child properties are displayed
- Disabled properties shown grayed but still selectable
- Preview shows resolved value

**Parameters:**

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `Value` | `string?` | `null` | The path value (two-way bindable) |
| `Label` | `string` | `"Path"` | Input label |
| `Placeholder` | `string` | `"Enter path or click to browse"` | Placeholder text |
| `Variant` | `Variant` | `Variant.Text` | MudBlazor input variant |
| `Required` | `bool` | `false` | Whether a `MudForm` reports the empty field as invalid |
| `LocalSubjects` | `IDictionary<string, IInterceptorSubject>?` | `null` | Additional local subjects to show in picker |
| `Class` | `string?` | `null` | CSS class for styling |
| `IsPropertyVisible` | `Func<RegisteredSubjectProperty, bool>?` | `null` | Which properties the picker shows |
| `CanSelectProperty` | `Func<RegisteredSubjectProperty, bool>?` | `null` | Which properties can be picked. Typed text is not checked |
| `CanSelectSubject` | `Func<IInterceptorSubject, bool>?` | `null` | Which subjects can be picked. Typed text is not checked |

---

### Excluding Subjects from Browsing

Internal implementation subjects that should not appear in the UI browser panel or MCP tools can be marked with the `[ExcludeFromBrowsing]` attribute:

```csharp
using HomeBlaze.Abstractions.Attributes;

[InterceptorSubject]
[ExcludeFromBrowsing]
public partial class HtmlSegment
{
    // Internal implementation detail, not user-facing
}
```

This hides the subject from:
- The Blazor browser/panel tree view
- MCP `browse` and `search` tool results

Use this for internal subjects that are part of a parent subject's implementation but have no meaning to end users or AI agents (e.g., parsed HTML segments, render expressions).

---

## Building Subject Components

Subject components are Blazor components that visualize or edit subjects. There are three types:

| Component Type | Interface | Purpose | Example |
|----------------|-----------|---------|---------|
| Widget | `ISubjectComponent` | Compact display, dashboard cards | `MotorWidgetComponent` |
| Edit | `ISubjectEditComponent` | Configuration form | `MotorEditComponent` |
| Page | `ISubjectComponent` | Full page view | `MarkdownFilePageComponent` |

### Registration

Register components with the `[SubjectComponent]` attribute:

```csharp
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(Motor))]
@implements ISubjectComponent

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private Motor? MotorSubject => Subject as Motor;
}
```

### Widget Components

Widgets are compact visual representations shown in dashboards and markdown pages:

```razor
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(Motor))]
@implements ISubjectComponent

<MudPaper Class="pa-4" Elevation="2">
    <MudText Typo="Typo.h6">@MotorSubject?.Name</MudText>
    <MudText>Speed: @MotorSubject?.CurrentSpeed RPM</MudText>

    @if (IsEditing)
    {
        <!-- Inline editing when page is in edit mode -->
        <MudSlider T="int"
                   Value="@MotorSubject.TargetSpeed"
                   ValueChanged="@OnSpeedChanged"
                   Min="0" Max="3000" />
    }
</MudPaper>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [CascadingParameter(Name = "IsEditing")]
    public bool IsEditing { get; set; }

    private Motor? MotorSubject => Subject as Motor;

    private void OnSpeedChanged(int speed)
    {
        if (MotorSubject != null)
            MotorSubject.TargetSpeed = speed;
    }
}
```

### IsEditing Cascading Parameter

When a page enters edit mode, an `IsEditing` cascading parameter is propagated to all child widgets. This enables inline editing behaviors:

```csharp
[CascadingParameter(Name = "IsEditing")]
public bool IsEditing { get; set; }
```

**Widget editing behaviors:**

| Approach | Description | Example |
|----------|-------------|---------|
| Inline editing | Show editable controls directly in widget | Slider for TargetSpeed |
| Ignore | Read-only widget, no editing support | Status displays |
| Hybrid | Simple fields inline, complex via dialog | Basic fields inline, advanced via dialog |

### Auto Edit Button

When a widget has a corresponding Edit component, the host automatically renders an edit button overlay when `IsEditing` is true:

- The button appears in the top-right corner of the widget
- Clicking opens the `SubjectEditDialog` with the Edit component
- No boilerplate needed in widget components

### Content Slots

A widget type can take content from the markdown page it is used on: text, expressions and other widgets. The type declares where the content goes with `[Slot]` on properties, and the page fills these properties from the content of the block. The attribute is in `HomeBlaze.Abstractions.Attributes`.

[Markdown Pages](../administration/pages.md) is the guide for the page side: the colon lines that open and end a block, the arguments and what a page author sees when something is wrong. [Page Widgets](../administration/page-widgets.md) is the reference of the widgets that ship, and [Page Errors](../administration/page-errors.md) lists the messages. This section is the side of the widget author. Its samples of page source use the names of that guide: a block starts with a head line of colons and a name, a block whose name has no dot is a slot block, and a closing line is written only where the next head line does not end the block.

#### Declaring Slots

The type of the property decides what a block produces, and the name of the slot decides what a block is called on the page.

| Declaration | Blocks on the page | Value of the property |
|-------------|--------------------|-----------------------|
| `[Slot(IsDefault = true)] IInterceptorSubject? Content` | Everything in the widget block that is not a slot block | The content, or `null` when there is none |
| `[Slot] IInterceptorSubject? Footer` | One `Footer` block | The content of that block, or `null` without a block |
| `[Slot("Item")] IInterceptorSubject[] Items` | Any number of `Item` blocks | One content value per block, in page order |
| `[Slot] Sidebar? Sidebar` | One `Sidebar` block with arguments | One `Sidebar` that the page created, or `null` without a block |
| `[Slot("Tab")] Tab[] Tabs` | Any number of `Tab` blocks with arguments | One `Tab` per block, in page order |

**The default slot.** `IsDefault = true` makes a slot the place for everything in the widget block that is not a slot block. `Card` declares its body this way:

```csharp
[Slot(IsDefault = true)]
public partial IInterceptorSubject? Content { get; set; }
```

~~~~markdown
:: HomeBlaze.Components.Card Title="Conveyor Belt"
This text, the expression {{ ../Conveyor/CurrentSpeed }} and the widget below are the value of `Content`.

::: HomeBlaze.Components.Widget Path=../Conveyor
:::
::
~~~~

**A named slot.** Without a name in the attribute, the slot has the name of the property. `Card` declares its footer this way, and the page fills it with a `Footer` block:

```csharp
[Slot]
public partial IInterceptorSubject? Footer { get; set; }
```

~~~~markdown
:: HomeBlaze.Components.Card Title="Conveyor Belt"
The body of the card.

::: Footer
This text is the value of `Footer`.
:::
::
~~~~

**A block name that differs from the property name, and a list of content.** The name in the attribute is what one block is called on the page. An array takes one value per block, so the property has a plural name and the block the singular. `FlowLayout` declares its cells this way:

```csharp
[Slot("Item")]
public partial IInterceptorSubject[] Items { get; set; }
```

~~~~markdown
:: HomeBlaze.Components.FlowLayout
::: Item
The first value of `Items`.
:::
::: Item
The second value of `Items`.
:::
::
~~~~

**A list of typed values.** When the type of the slot is a subject class of your own, the page creates one instance of that class per block. The arguments of the block set the `[Configuration]` properties of the instance, the block can have a `#name`, and the content of the block goes to the default slot of the instance. `TabLayout` gets its tabs this way, and `Tab` is the class of one tab (both shortened):

```csharp
[InterceptorSubject]
public partial class TabLayout : IConfigurable, ITitleProvider
{
    public TabLayout()
    {
        Tabs = [];
    }

    [Slot("Tab")]
    public partial Tab[] Tabs { get; set; }

    public string? Title => null;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class Tab : IConfigurable
{
    [Configuration]
    public partial string? Title { get; set; }

    [Configuration]
    public partial string? Icon { get; set; }

    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
```

~~~~markdown
:: HomeBlaze.Components.TabLayout
::: Tab#cooling Title="Cooling Fan" Icon=AcUnit
The value of `Content` of the first `Tab`.
:::
::: Tab#exhaust Title="Exhaust Fan"
The value of `Content` of the second `Tab`.
:::
::
~~~~

**A single typed value.** The same without an array: one block creates one instance. None of the widgets that ship has such a slot. The `SidebarLayout` of ["A Complete Widget"](#a-complete-widget) below declares one:

```csharp
[Slot]
public partial Sidebar? Sidebar { get; set; }
```

~~~~markdown
:: MyCompany.Widgets.SidebarLayout
::: Sidebar#menu Width=200
The value of `Content` of the `Sidebar`.
:::

The value of `Content` of the layout.
::
~~~~

What a page author cannot do with each kind:

- **Content slots take no arguments**: A block for a slot of the type `IInterceptorSubject` takes no arguments, no `#name` and no `json` block. The page shows an error for them.
- **Typed slots are configured like widget blocks**: A block for a slot with a class of your own takes the `[Configuration]` properties of that class as arguments and in a `json` block, with the same checks as a widget block.
- **One block for a single value**: A second block for a slot that is no array is an error on the page.

**An editor for a list of typed values.** To add or remove a value, the editor of a widget replaces the array with a new one and saves the owner, as the editors of `GridLayout` and `CanvasLayout` do with `RootManager.WriteConfigurationOrWarnAsync`. To change a value, it saves that value, which writes the head line and the `json` block of its block. A new value is written as a block after the last block of the slot, as [Editing in the Page Editor](../administration/pages.md#editing-in-the-page-editor) describes for cells and nodes. The order of the values is never written, because the text decides it: a save with the values in another order than their blocks is refused, and so is a save with one value twice.

#### Rules for a Slot Property

A slot property must meet these rules. A declaration that breaks one cannot be used: every block of that type then shows an error box with the message of the table, with the short type name and a colon before it, like "Gauge: the slot 'Content' needs a setter." No instance is created.

| Rule | Message when it is broken |
|------|---------------------------|
| The property is a `partial` property of an `[InterceptorSubject]` class. The page writes through the intercepted property, which is what attaches the content to the object graph | `the slot 'Content' must be a partial property of a subject class.` |
| The property has a setter | `the slot 'Content' needs a setter.` |
| The type is `IInterceptorSubject`, a subject class, or an array of one of them | `the slot 'Content' must hold subjects.` |
| A subject class as slot type can be created, so it is no interface and no abstract class | `the type GaugeBase of the slot 'Scale' cannot be created because it is abstract or an interface.` |
| The slot name is not empty | `the slot of the property 'Content' needs a name.` |
| The slot name contains no dot, because a name with a dot is a type name on a page | `the slot name 'My.Slot' must not contain a dot.` |
| The slot name can be written as the name of a slot block: it starts with a letter or `_` and has only letters, digits and `_` | `the slot name 'My-Item' cannot be written on a page. A slot name starts with a letter or '_' and has only letters, digits and '_'.` |
| Each slot name is declared once per type | `the slot 'Content' is declared twice.` |
| A type has at most one default slot | `there is more than one default slot.` |
| The default slot has the type `IInterceptorSubject` and is no array | `the default slot 'Content' must be of type IInterceptorSubject.` |

More that a declaration should follow:

- **Name**: The slot name defaults to the property name and is case-sensitive. For an array, set the singular explicitly, like `[Slot("Item")]` on `Items`.
- **Arrays**: Initialize an array property with an empty array in the constructor. All values of one slot have the same type. Mixed types in one list slot are not supported.
- **The class of a typed slot**: It must implement `IConfigurable` and be known to HomeBlaze like every subject type. Otherwise the page shows "The type 'MyCompany.Widgets.Sidebar' of the slot 'Sidebar' could not be created. It must be a registered configurable subject type." Give it a default slot of its own when its blocks should take content. Put `[ExcludeFromCreation]` on it, because it only exists inside its widget and the create dialog would offer it on its own otherwise, see [Hiding a Type](#hiding-a-type).
- **Not configuration on a page**: A slot property is not configuration on a page. Its values live in the text of the page as blocks, and an argument or a name in the `json` block that sets it is an error on the page that says how the slot is filled. Only an empty list of a slot that takes several blocks is ignored in the `json` block, because earlier versions wrote one for every layout. Put `[Configuration]` on it as well only when the same property must also be read and written in a configuration file, as `GridLayout.Cells` does.

#### Rendering Slots

A widget component renders a slot with `SubjectComponent` and does not need to know what the content is. The value of a content slot is a subject that stands for the content of the block. Treat it as opaque and render it.

**One slot.** `CardWidget.razor` renders the body and the footer of a card (shortened):

```razor
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(Card))]
@implements ISubjectComponent

<MudPaper Class="pa-4 card-widget" Elevation="2">
    <MudText Typo="Typo.h6">@CardSubject?.Title</MudText>

    <SubjectComponent Subject="@CardSubject?.Content" Type="SubjectComponentType.Widget" />

    @if (CardSubject?.Footer is not null)
    {
        <MudDivider Class="my-2" />
        <SubjectComponent Subject="@CardSubject.Footer" Type="SubjectComponentType.Widget" />
    }
</MudPaper>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private Card? CardSubject => Subject as Card;
}
```

**A list slot.** `FlowLayoutWidget.razor` renders one cell per value of `Items` (shortened):

```razor
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(FlowLayout))]
@implements ISubjectComponent

<div class="flow-layout" style="display: grid; gap: 16px;">
    @foreach (var item in Flow?.Items ?? [])
    {
        <div class="flow-layout-item">
            <SubjectComponent Subject="@item" Type="SubjectComponentType.Widget" />
        </div>
    }
</div>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private FlowLayout? Flow => Subject as FlowLayout;
}
```

**Typed slot values.** `TabLayoutWidget.razor` reads the configuration of each `Tab` and renders its content (shortened):

```razor
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(TabLayout))]
@implements ISubjectComponent

@{
    // Read once per render and never kept: a reload assigns a new array.
    var tabs = Layout?.Tabs ?? [];
}

<MudTabs KeepPanelsAlive="true">
    @foreach (var tab in tabs)
    {
        <MudTabPanel Text="@tab.Title">
            <SubjectComponent @key="tab" Subject="@tab.Content" Type="SubjectComponentType.Widget" />
        </MudTabPanel>
    }
</MudTabs>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private TabLayout? Layout => Subject as TabLayout;
}
```

Rules for rendering:

- **`null` renders nothing**: `SubjectComponent` with a slot that is `null` renders nothing. Test for `null` only when the widget shows something around the slot, like the divider of the card. A slot block without content gives a value that is not `null` and renders nothing.
- **Read in the render, keep nothing**: Read slot properties inside the render and do not keep their values in fields. The page renders again when a property changes that was read while it rendered, and a change of the page text assigns new values and new arrays.
- **Content that renders later is not tracked**: Content inside a child component that renders on its own later is not tracked that way, for example a tab panel that is only created when it is first opened. `TabLayout` keeps all its panels alive for this reason.
- **Identity of typed values**: A typed value whose block has a `#name` stays the same instance when the page text changes, so a component can compare instances to keep its state. `TabLayoutWidget.razor` keeps the selected tab this way and compares it with the tabs of the current render only.

#### A Subject on a Page: Identity and Lifecycle

What a widget author must know about the life of a subject that a page creates. It holds for the subject of a widget block and for a typed slot value alike.

- **`IConfigurable` is required**: A type that a page uses as a block must implement `IConfigurable`, also when it has no configuration, like `ValueList`. For a type without it, the page shows an error that says the type cannot be used as a block.
- **Names of the configuration**: A page names a `[Configuration]` property by its property name, for example `TargetSpeed=1200` on the head line and `"TargetSpeed"` in the `json` block, also for the properties of nested objects and subjects. Configuration files keep camelCase, and a page uses the property names. An explicit JSON name on a property, such as `[JsonPropertyName]`, applies to configuration files only.
- **Creation**: The page creates the subject through the service provider, so constructor injection works, then sets the `[Configuration]` properties that the block names and then fills the slots. It does not call `ApplyConfigurationAsync` for a new subject.
- **Kept across reloads**: The page is read again with every change of its text. While the block stays, the page keeps the instance and updates it in place: it sets every `[Configuration]` property that the block names again, also when the value did not change, calls `ApplyConfigurationAsync`, and assigns the slots again. Expect all of this to run often.
- **A new instance**: The page creates a new instance when the name, the position or the type of the block changes, when an argument is removed from the block, and when the block around it gets a new instance. The section "What an Edit of the Page Keeps" of [Markdown Pages](../administration/pages.md#what-an-edit-of-the-page-keeps) has the rules as a page author sees them.
- **Slot values**: A content slot keeps its value while its block stays, and a list slot gets a new array on every reload. A slot whose block was removed is set to `null`, or to an empty array.
- **Subjects in the configuration**: A subject that the `json` block holds as a value of a `[Configuration]` property is created again on every reload, because the configuration is applied again. A property that is a slot as well, like the `Child` of a grid cell, is filled from the content of its block and follows the rule for slot values.
- **Release after an error**: The subject of a block that ends as an error box is released again. This also holds for the typed slot values and nested subjects that were created for it. A hosted subject can therefore get `StopAsync` without a preceding `StartAsync`.
- **Failures are shown**: An exception from the constructor, from a property setter or from `ApplyConfigurationAsync` does not take the page down. The block shows an error box with the message of the exception, and the log has the stack trace.
- **Never wait for the own page**: A subject must not wait for a write or a reload of its own page from its constructor, from a property setter or from `ApplyConfigurationAsync`. The reloads of one page run one after the other, so such a wait would never end.
- **Paths**: A subject that resolves a path from its configuration should call `ResolveScopedSubject` or `ResolveScopedValue`, so that the path starts at the page and can use the names of blocks. See "Referencing Other Subjects" above.

#### A Complete Widget

A layout that shows page content next to a sidebar, from the subject to the component to the page. The sidebar is a typed slot value with a width, and the main content is the default slot.

The subjects, in a subject project as [Project Structure](#project-structure) describes it:

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace MyCompany.Widgets;

/// <summary>
/// The sidebar of a <see cref="SidebarLayout"/>.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class Sidebar : IConfigurable
{
    public Sidebar()
    {
        Width = 240;
    }

    /// <summary>
    /// The width of the sidebar in pixels.
    /// </summary>
    [Configuration]
    public partial int Width { get; set; }

    /// <summary>
    /// What the sidebar shows.
    /// </summary>
    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Shows page content next to a sidebar.
/// </summary>
[Category("Dashboard")]
[Description("Shows page content next to a sidebar")]
[InterceptorSubject]
public partial class SidebarLayout : IConfigurable
{
    /// <summary>
    /// The main content.
    /// </summary>
    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    /// <summary>
    /// The sidebar, created from the <c>Sidebar</c> block.
    /// </summary>
    [Slot]
    public partial Sidebar? Sidebar { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
```

The widget component, in the UI project next to it. `SubjectComponent` is in `HomeBlaze.Components`:

```razor
@using HomeBlaze.Components
@using HomeBlaze.Components.Abstractions
@using HomeBlaze.Components.Abstractions.Attributes
@using Namotion.Interceptor

@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(SidebarLayout))]
@implements ISubjectComponent

@{
    // Read once per render and never kept: a reload can assign another sidebar.
    var sidebar = Layout?.Sidebar;
}

<div style="display: flex; gap: 16px;">
    @if (sidebar is not null)
    {
        <div style="flex: 0 0 @(sidebar.Width)px; min-width: 0;">
            <SubjectComponent Subject="@sidebar.Content" Type="SubjectComponentType.Widget" />
        </div>
    }

    <div style="flex: 1; min-width: 0;">
        <SubjectComponent Subject="@Layout?.Content" Type="SubjectComponentType.Widget" />
    </div>
</div>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private SidebarLayout? Layout => Subject as SidebarLayout;
}
```

A page that uses it:

~~~~markdown
:: MyCompany.Widgets.SidebarLayout#factory
::: Sidebar#menu Width=200
**Motors**

The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.
:::

::: HomeBlaze.Components.Card Title="Conveyor Belt"
:::: HomeBlaze.Components.Widget Path=../Conveyor
::::
:::
::
~~~~

The page author sees the sidebar with a width of 200 pixels at the left and the card at its right. `Width=200` sets `Sidebar.Width`, the text in the `Sidebar` block is `Sidebar.Content`, and the card is `SidebarLayout.Content`.

### Edit Components

Edit components provide configuration forms:

```razor
@attribute [SubjectComponent(SubjectComponentType.Edit, typeof(Motor))]
@implements ISubjectEditComponent

<MudTextField @bind-Value="_name"
              @bind-Value:after="OnFieldChanged"
              Label="Name"
              Required="true"
              Immediate="true" />

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public bool IsCreating { get; set; }

    private Motor? MotorSubject => Subject as Motor;

    private string _name = string.Empty;
    private string _originalName = string.Empty;

    public bool IsValid => !string.IsNullOrWhiteSpace(_name);
    public bool IsDirty => _name != _originalName;

    public event Action<bool>? IsValidChanged;
    public event Action<bool>? IsDirtyChanged;

    protected override void OnInitialized()
    {
        _name = MotorSubject?.Name ?? string.Empty;
        _originalName = _name;
    }

    private void OnFieldChanged()
    {
        IsValidChanged?.Invoke(IsValid);
        IsDirtyChanged?.Invoke(IsDirty);
    }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        if (MotorSubject != null && IsValid)
        {
            MotorSubject.Name = _name;
            _originalName = _name;
            IsDirtyChanged?.Invoke(false);
        }

        return Task.CompletedTask;
    }
}
```

**ISubjectEditComponent contract:**

| Member | Purpose |
|--------|---------|
| `IsValid` | Can the form be saved? |
| `IsDirty` | Have changes been made? |
| `IsValidChanged` | Event fired when validity changes |
| `IsDirtyChanged` | Event fired when dirty state changes |
| `IsCreating` | Set by the hosting panel: `true` while the subject is being created, `false` while it is edited |
| `SaveAsync` | Persist changes |

A component raises `IsValidChanged` whenever `IsValid` changes. The remarks on `ISubjectEditComponent.IsValidChanged` describe what the dialogs and the page editor do with it. The handler of `@bind-Value:after` runs each time the bound value is committed, whichever way it changed (typing, pasting, autofill), which a key handler such as `OnKeyUp` does not cover. A text field commits its value when it loses focus, and with `Immediate="true"` on every keystroke.

### Edit Component Lifecycle: OnInitialized vs OnParametersSet

**CRITICAL:** Edit components must use `OnInitialized()` or `OnInitializedAsync()`, **NOT** `OnParametersSet()` or `OnParametersSetAsync()`.

**Why?**

`OnParametersSet()` is called **multiple times** whenever any parameter changes, which causes:
- **Loss of user edits**: Form field values are reset to the Subject's current values
- **Broken dirty tracking**: `_original*` values are reset, breaking `IsDirty` logic
- **Poor UX**: Users lose their work mid-edit

**Correct pattern with local state and dirty tracking:**

```razor
@attribute [SubjectComponent(SubjectComponentType.Edit, typeof(Motor))]
@implements ISubjectEditComponent

<MudForm>
    <MudTextField @bind-Value="_name"
                  @bind-Value:after="OnFieldChanged"
                  Label="Name"
                  Required="true"
                  Immediate="true" />

    <MudNumericField @bind-Value="_targetSpeed"
                     @bind-Value:after="OnFieldChanged"
                     Label="Target Speed (RPM)"
                     Min="0" Max="10000"
                     Class="mt-4" />
</MudForm>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public bool IsCreating { get; set; }

    private Motor? MotorSubject => Subject as Motor;

    // Local backing fields for form inputs
    private string _name = string.Empty;
    private int _targetSpeed;

    // Original values for dirty tracking
    private string _originalName = string.Empty;
    private int _originalTargetSpeed;

    public bool IsValid => !string.IsNullOrWhiteSpace(_name);
    public bool IsDirty => _name != _originalName ||
                           _targetSpeed != _originalTargetSpeed;

    public event Action<bool>? IsValidChanged;
    public event Action<bool>? IsDirtyChanged;

    // ✓ CORRECT: Use OnInitialized - runs once when component is created
    protected override void OnInitialized()
    {
        if (MotorSubject != null)
        {
            // Load values from subject into local fields
            _name = MotorSubject.Name;
            _targetSpeed = MotorSubject.TargetSpeed;

            // Store originals for dirty tracking
            _originalName = _name;
            _originalTargetSpeed = _targetSpeed;
        }
    }

    private void OnFieldChanged()
    {
        IsValidChanged?.Invoke(IsValid);
        IsDirtyChanged?.Invoke(IsDirty);
    }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        if (MotorSubject != null && IsValid)
        {
            // Apply local values back to subject
            MotorSubject.Name = _name;
            MotorSubject.TargetSpeed = _targetSpeed;

            // Update originals after save
            _originalName = _name;
            _originalTargetSpeed = _targetSpeed;

            IsDirtyChanged?.Invoke(false);
        }

        return Task.CompletedTask;
    }
}
```

**For async initialization (e.g., file loading):**

```razor
@implements ISubjectEditComponent

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private JsonFile? File => Subject as JsonFile;
    private string _originalContent = string.Empty;
    private string _currentContent = string.Empty;

    public bool IsDirty => _currentContent != _originalContent;

    // ✓ CORRECT: Use OnInitializedAsync for async operations
    protected override async Task OnInitializedAsync()
    {
        if (File != null)
        {
            await using var stream = await File.ReadAsync(CancellationToken.None);
            using var reader = new StreamReader(stream);
            _originalContent = await reader.ReadToEndAsync();
            _currentContent = _originalContent;
        }
    }
}
```

**Why OnInitialized works:**

Edit components are **recreated** when editing different subjects. The component lifetime matches the editing session:
- Opening edit dialog → component created → `OnInitialized` runs once
- Editing in dialog → component stays alive, parameters don't change
- Closing/saving → component disposed
- Opening different subject → **new component instance** → `OnInitialized` runs for new subject

**Common mistake to avoid:**

```razor
// ✗ WRONG: Don't use OnParametersSet
protected override void OnParametersSet()
{
    if (MotorSubject != null)
    {
        _name = MotorSubject.Name;         // Overwrites user edits!
        _originalName = _name;              // Resets dirty tracking!
    }
}
```

This pattern applies to:
- **Edit components** (opened via SubjectEditDialog)
- **The create dialog** (SubjectSetupDialog hosts the same edit components with `IsCreating = true`)
- **Widget components** (if they use local state for inline editing)
- **Configuration editors** (ConfigurationPropertiesEditor pattern)

### Page Components

Page components provide full-page views:

```razor
@attribute [SubjectComponent(SubjectComponentType.Page, typeof(MarkdownFile))]
@implements ISubjectComponent

<CascadingValue Value="@_isEditing" Name="IsEditing">
    <div class="markdown-content">
        @foreach (var widget in GetWidgets())
        {
            <SubjectComponent Subject="@widget" Type="SubjectComponentType.Widget" />
        }
    </div>
</CascadingValue>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private bool _isEditing = false;

    private void ToggleEditMode()
    {
        _isEditing = !_isEditing;
    }
}
```

### Summary

| Feature | Widget | Edit | Page |
|---------|--------|------|------|
| Shows subject data | Yes | Yes | Yes |
| Compact/card format | Yes | No | No |
| Full configuration | No | Yes | No |
| Uses IsEditing | Yes (optional) | No | Yes (provides) |
| Auto edit button | Yes (if Edit exists) | N/A | No |
