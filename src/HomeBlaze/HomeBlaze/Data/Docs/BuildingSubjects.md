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
    [State("Speed", Position = 1)]
    public partial int CurrentSpeed { get; set; }

    [State(Position = 2, Unit = StateUnit.DegreeCelsius)]
    public partial double Temperature { get; set; }

    [State(Position = 3)]
    public partial MotorStatus Status { get; set; }
}
```

**State attribute options:**

| Option | Description | Example |
|--------|-------------|---------|
| `Name` | Display name (overrides property name) | `"Speed"` |
| `Position` | Sort position in property panel | `1`, `2`, `3` |
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
| `Description` | Help text shown in dialogs | `"Sets the motor target speed"` |
| `Icon` | MudBlazor icon name | `"Speed"`, `"Stop"` |
| `Position` | Sort position in operations list | `1`, `2`, `3` |
| `RequiresConfirmation` | Show confirmation dialog before executing | `true` |

**Key points:**
- Operations appear in the subject's property panel under "Operations"
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
    [State("Target", Position = 2)]
    public partial int TargetSpeed { get; set; }

    [PropertyAttribute(nameof(TargetSpeed), "Minimum")]
    public partial int TargetSpeed_Minimum { get; set; }

    [PropertyAttribute(nameof(TargetSpeed), "Maximum")]
    public partial int TargetSpeed_Maximum { get; set; }

    [Configuration]
    public partial TimeSpan SimulationInterval { get; set; }

    // Live state (not persisted)

    [State("Speed", Position = 3)]
    public partial int CurrentSpeed { get; set; }

    [State(Position = 4, Unit = StateUnit.DegreeCelsius)]
    public partial double Temperature { get; set; }

    [State(Position = 1)]
    public partial MotorStatus Status { get; set; }

    // Derived properties

    [Derived]
    [State("Delta", Position = 5)]
    public int SpeedDelta => TargetSpeed - CurrentSpeed;

    [Derived]
    [State("At Target", Position = 6)]
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
| `[State]` | Live values for display | No | Yes |
| `[Configuration] + [State]` | Editable and displayed | Yes | Yes |
| `[Derived]` | Computed from other properties | No | Only if also `[State]` |
| `[Operation]` | Executable actions from UI | No | Yes (as buttons) |

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
| `Root.` | `Root.Demo.Conveyor` |
| `this.` | `this.Child.Property` |
| `../` | `../Sibling.Property` |
| Brackets | `Root.Demo[Setup.md]` (for keys with dots) |

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

### SubjectPathField Component

For subject path configuration properties, use the `SubjectPathField` component in edit forms. It provides a text input with a browse button that opens a tree picker dialog:

```razor
@using HomeBlaze.Components.Inputs

<SubjectPathField @bind-Value="_path"
                  @bind-Value:after="OnFieldChanged"
                  Label="Subject Path"
                  Placeholder="e.g., Root or Root.Demo.Conveyor"
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
| `Variant` | `Variant` | `Variant.Outlined` | MudBlazor input variant |
| `LocalSubjects` | `IDictionary<string, IInterceptorSubject>?` | `null` | Additional local subjects to show in picker |
| `Class` | `string?` | `null` | CSS class for styling |

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

### Edit Components

Edit components provide configuration forms:

```razor
@attribute [SubjectComponent(SubjectComponentType.Edit, typeof(Motor))]
@implements ISubjectEditComponent

<MudTextField @bind-Value="MotorSubject.Name" Label="Name" />
<MudNumericField @bind-Value="MotorSubject.TargetSpeed" Label="Target Speed" />

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private Motor? MotorSubject => Subject as Motor;

    public bool IsValid => !string.IsNullOrWhiteSpace(MotorSubject?.Name);
    public bool IsDirty { get; private set; }

    public event Action<bool>? IsValidChanged;
    public event Action<bool>? IsDirtyChanged;

    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        // Save logic here
        IsDirty = false;
        IsDirtyChanged?.Invoke(false);
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
| `SaveAsync` | Persist changes |

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
