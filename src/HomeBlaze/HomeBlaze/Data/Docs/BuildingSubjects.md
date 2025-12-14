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

## State Properties

Properties marked with `[State]` are displayed in the UI property panel:

```csharp
[InterceptorSubject]
public partial class Motor
{
    [State("Speed", Order = 1)]
    public partial int CurrentSpeed { get; set; }

    [State(Order = 2, Unit = StateUnit.DegreeCelsius)]
    public partial double Temperature { get; set; }

    [State(Order = 3)]
    public partial MotorStatus Status { get; set; }
}
```

**State attribute options:**

| Option | Description | Example |
|--------|-------------|---------|
| `Name` | Display name (overrides property name) | `"Speed"` |
| `Order` | Sort order in property panel | `1`, `2`, `3` |
| `Unit` | Formatting unit | `StateUnit.DegreeCelsius` |
| `IsCumulative` | Value accumulates over time | `true` for energy meters |
| `IsSignal` | Precise value (not a sensor reading) | `true` for commands |
| `IsEstimated` | Calculated/estimated value | `true` for predictions |

**Available units:**

```csharp
public enum StateUnit
{
    Default,           // No formatting
    Percent,           // 75%
    DegreeCelsius,     // 23.5 °C
    Watt,              // 100 W
    KiloWatt,          // 1.5 kW
    WattHour,          // 500 Wh
    Volt,              // 230 V
    Ampere,            // 5 A
    Hertz,             // 50 Hz
    Lumen,             // 800 lm
    Lux,               // 500 lx
    Meter,             // 1.5 m
    Millimeter,        // 10 mm
    MillimeterPerHour, // 5 mm/h
    Kilobyte,          // 1024 KB
    KilobytePerSecond, // 100 KB/s
    MegabitsPerSecond, // 100 Mbps
    LiterPerHour,      // 50 L/h
    Currency,          // $10.00
    HexColor           // #FF0000
}
```

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
using MudBlazor;

public partial class Motor : IIconProvider
{
    public string? Icon => Icons.Material.Filled.Settings;
}
```

## Configuration Lifecycle

Implement `IConfigurableSubject` to react when configuration changes:

```csharp
public partial class Motor : IConfigurableSubject
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
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Samples;

[InterceptorSubject]
public partial class Motor : BackgroundService, IConfigurableSubject, ITitleProvider, IIconProvider
{
    // Configuration (persisted to JSON)

    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    [State("Target", Order = 2)]
    public partial int TargetSpeed { get; set; }

    [PropertyAttribute(nameof(TargetSpeed), "Minimum")]
    public partial int TargetSpeed_Minimum { get; set; }

    [PropertyAttribute(nameof(TargetSpeed), "Maximum")]
    public partial int TargetSpeed_Maximum { get; set; }

    [Configuration]
    public partial TimeSpan SimulationInterval { get; set; }

    // Live state (not persisted)

    [State("Speed", Order = 3)]
    public partial int CurrentSpeed { get; set; }

    [State(Order = 4, Unit = StateUnit.DegreeCelsius)]
    public partial double Temperature { get; set; }

    [State(Order = 1)]
    public partial MotorStatus Status { get; set; }

    // Derived properties

    [Derived]
    [State("Delta", Order = 5)]
    public int SpeedDelta => TargetSpeed - CurrentSpeed;

    [Derived]
    [State("At Target", Order = 6)]
    public bool IsAtTargetSpeed => Math.Abs(SpeedDelta) < 50;

    // Display interfaces

    public string? Title => Name;
    public string? Icon { get; } = null;

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
    <TargetFramework>net9.0</TargetFramework>
    <LangVersion>preview</LangVersion>  <!-- Required for partial properties -->
  </PropertyGroup>
  <ItemGroup>
    <!-- Core interception - required -->
    <ProjectReference Include="..\Namotion.Interceptor\Namotion.Interceptor.csproj" />
    <ProjectReference Include="..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />

    <!-- HomeBlaze integration - for [Configuration], [State], ITitleProvider, etc. -->
    <ProjectReference Include="..\HomeBlaze.Abstractions\HomeBlaze.Abstractions.csproj" />

    <!-- Optional: For BackgroundService base class -->
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="9.*" />
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
    <TargetFramework>net9.0</TargetFramework>
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
  "type": "HomeBlaze.Samples.Motor",
  "Name": "Pump Motor",
  "TargetSpeed": 1500,
  "SimulationInterval": "00:00:01"
}
```

Only `[Configuration]` properties are persisted. The `type` field enables polymorphic deserialization.

## Summary

| Attribute | Purpose | Persisted | Displayed |
|-----------|---------|-----------|-----------|
| `[Configuration]` | User-editable settings | Yes | No (unless also `[State]`) |
| `[State]` | Live values for display | No | Yes |
| `[Configuration] + [State]` | Editable and displayed | Yes | Yes |
| `[Derived]` | Computed from other properties | No | Only if also `[State]` |

| Interface | Purpose |
|-----------|---------|
| `ITitleProvider` | Display name in navigation |
| `IIconProvider` | Icon in navigation/browser |
| `IConfigurableSubject` | React to configuration changes |

---

## Advanced Patterns

### Constructor Injection

Subjects can inject services via constructor. The system uses `ActivatorUtilities.CreateInstance` to resolve dependencies from the DI container:

```csharp
[InterceptorSubject]
public partial class Widget : IConfigurableSubject
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
| `ILogger<T>` | Logging |

### Referencing Other Subjects

Use paths to reference subjects in the object graph. See [Configuration Guide - Path Syntax](Configuration.md#path-syntax) for full documentation.

**Quick Reference:**

| Prefix | Example |
|--------|---------|
| `Root.` | `Root.Children[demo].Children[motor.json]` |
| `this.` | `this.Child.Property` |
| `../` | `../Sibling.Property` |

Use `SubjectPathResolver.ResolveFromRelativePath()` to resolve paths in code:

```csharp
[Derived]
public IInterceptorSubject? ResolvedSubject => ResolveSubject();

private IInterceptorSubject? ResolveSubject()
{
    if (string.IsNullOrEmpty(Path))
        return null;

    // ResolveFromRelativePath handles Root., this., ../, and relative paths
    return _pathResolver.ResolveFromRelativePath(Path);
}
```
