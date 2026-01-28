---
title: Glossary
navTitle: Glossary
position: 10
---

# Glossary

This glossary defines key terms used throughout HomeBlaze documentation and codebase.

---

## Core Concepts

### Subject

An intercepted object in the HomeBlaze object graph. Subjects are classes decorated with `[InterceptorSubject]` that participate in property tracking, change detection, and UI integration. Examples include motors, sensors, files, and folders.

```csharp
[InterceptorSubject]
public partial class Motor : IInterceptorSubject
{
    public partial int Speed { get; set; }
}
```

### Object Graph

The hierarchical tree structure of all subjects in the system. The root is typically a storage container, with children being folders and files/subjects.

```
Root (FluentStorageContainer)
├── Children
│   ├── demo/
│   │   └── motor.json (Motor)
│   └── docs/
│       └── guide.md (MarkdownFile)
```

### Property

A value on a subject. Properties can be marked with attributes to control their behavior:

- **Configuration Property**: Persisted to JSON (`[Configuration]`)
- **State Property**: Displayed in the UI (`[State]`)
- **Derived Property**: Computed from other properties (`[Derived]`)

### Context

The `IInterceptorSubjectContext` that manages a subject's lifecycle, dependencies, and interceptors. Created via `InterceptorSubjectContext.Create()` with optional configuration like `.WithFullPropertyTracking()` or `.WithRegistry()`.

---

## Attributes

### `[InterceptorSubject]`

Marks a class as an interceptable subject. The class must be `partial` and properties to track must also be `partial`.

### `[Configuration]`

Marks a property for JSON persistence. When the subject is saved, configuration properties are written to the JSON file.

### `[State]`

Marks a property for display in the UI property panel. Options include:
- `Name` - Display name
- `Position` - Sort order
- `Unit` - Formatting unit (e.g., `StateUnit.DegreeCelsius`)

### `[Derived]`

Marks a computed property that auto-updates when its dependencies change. Must be expression-bodied (`=>`).

### `[Operation]`

Marks a method as an executable action from the UI. Operations appear as buttons in the subject's property panel. Options include:
- `Title` - Display name
- `Description` - Help text
- `Icon` - MudBlazor icon name
- `Position` - Sort order
- `RequiresConfirmation` - Show confirmation dialog

### `[Query]`

Marks a method as a read-only query (no side effects). Similar to `[Operation]` but semantically indicates the method doesn't modify state. UI support deferred to V2.

### `[PropertyAttribute]`

Associates metadata with another property. Used for constraints like minimum/maximum values.

```csharp
[PropertyAttribute(nameof(TargetSpeed), "Minimum")]
public partial int TargetSpeed_Minimum { get; set; }
```

---

## Methods

### Operation Method

A method marked with `[Operation]` that can be invoked from the UI. Operations typically have side effects like starting a process, resetting state, or sending commands.

```csharp
[Operation(Title = "Emergency Stop", RequiresConfirmation = true)]
public void EmergencyStop()
{
    TargetSpeed = 0;
    Status = MotorStatus.Stopped;
}
```

### Query Method

A method marked with `[Query]` that returns data without side effects. Queries are safe to call repeatedly without changing system state.

```csharp
[Query(Title = "Get Diagnostics")]
public MotorDiagnostics GetDiagnostics()
{
    return new MotorDiagnostics { Status = Status, Speed = CurrentSpeed };
}
```

---

## UI Components

### Subject Component

A Blazor component associated with a specific subject type for rendering. Registered via `[SubjectComponent]` attribute.

### Component Types

| Type | Purpose | Interface |
|------|---------|-----------|
| `Widget` | Inline visualization | `ISubjectComponent` |
| `Edit` | Configuration editor | `ISubjectEditComponent` |
| `Page` | Full-page view | `ISubjectComponent` |

### Widget

A compact inline visualization of a subject. Widgets can be embedded in markdown pages or rendered via the `<SubjectComponent>` component.

```csharp
[SubjectComponent(SubjectComponentType.Widget, typeof(Motor))]
public partial class MotorWidget : ISubjectComponent
{
    [Parameter] public IInterceptorSubject? Subject { get; set; }
}
```

### Page

A full-page component for a subject type. Pages appear in the navigation when the subject is selected.

---

## Navigation

### Navigation Location

Where a page appears in the application:
- `NavBar` - Sidebar navigation (default)
- `AppBar` - Top application bar

### AppBar Alignment

For `AppBar` items, where they appear:
- `Left` - Left side of the top bar
- `Right` - Right side of the top bar

---

## Storage

### Storage Container

A subject that provides blob storage access. The default implementation uses FluentStorage for local or cloud storage.

### root.json

The configuration file that defines the storage location and root subject type.

```json
{
    "$type": "HomeBlaze.Storage.FluentStorageContainer",
    "storageType": "disk",
    "connectionString": "./Data"
}
```

### File Types

| Extension | Subject Type | Description |
|-----------|--------------|-------------|
| `.json` | Configured type | Subject defined by `$type` property |
| `.md` | `MarkdownFile` | Interactive page with expressions |
| Other | `GenericFile` | Basic file representation |

---

## Markdown Pages

### Frontmatter

YAML metadata at the top of markdown files controlling navigation and display.

```yaml
---
title: My Dashboard
navTitle: Dashboard
icon: Dashboard
position: 1
---
```

### Live Expression

Dynamic value binding in markdown using `{{ path }}` syntax. Updates automatically when the source property changes.

```markdown
Speed: {{ motor.CurrentSpeed }} RPM
```

### Embedded Subject

A subject defined inline within a markdown page using fenced code blocks.

~~~markdown
```subject(mymotor)
{
  "$type": "HomeBlaze.Samples.Motor",
  "name": "My Motor"
}
```
~~~

---

## Paths

### Path Syntax

Paths reference subjects and properties in the object graph:

| Prefix | Description | Example |
|--------|-------------|---------|
| `Root.` | Absolute from root | `Root.Demo.Conveyor` |
| `this.` | Relative to current | `this.Child.Name` |
| `../` | Parent navigation | `../Sibling.Temperature` |

### Simplified Syntax

For `[InlinePaths]` dictionaries, use dot notation: `Root.Demo.Conveyor`

### Bracket Notation

Use brackets when keys contain dots (like file extensions): `Root.Demo[Setup.md]`

---

## Interfaces

### ITitleProvider

Interface for subjects that provide a display title.

```csharp
public interface ITitleProvider
{
    string? Title { get; }
}
```

### IIconProvider

Interface for subjects that provide a display icon.

```csharp
public interface IIconProvider
{
    string? Icon { get; }
}
```

### IConfigurableSubject

Interface for subjects that react to configuration changes.

```csharp
public interface IConfigurableSubject
{
    Task ApplyConfigurationAsync(CancellationToken cancellationToken = default);
}
```

### ISubjectMethodInvoker

Service for invoking operation and query methods on subjects.

```csharp
public interface ISubjectMethodInvoker
{
    Task<MethodInvocationResult> InvokeAsync(
        IInterceptorSubject subject,
        SubjectMethodInfo method,
        object?[] parameters,
        CancellationToken cancellationToken = default);
}
```

---

## Services

### RootManager

Manages loading and saving the root subject tree from `root.json`.

### SubjectTypeRegistry

Discovers and resolves subject types by name or file extension.

### SubjectPathResolver

Resolves path expressions to subjects in the object graph.

### SubjectComponentRegistry

Discovers and resolves UI components for subject types.

### ConfigurableSubjectSerializer

Serializes and deserializes subjects to/from JSON with polymorphic type handling.

---

## State Units

Units for formatting state values:

| Unit | Display | Example |
|------|---------|---------|
| `Percent` | % | 75% |
| `DegreeCelsius` | °C | 23.5 °C |
| `Watt` | W | 100 W |
| `KiloWatt` | kW | 1.5 kW |
| `Volt` | V | 230 V |
| `Ampere` | A | 5 A |
| `Hertz` | Hz | 50 Hz |

See `StateUnit` enum for full list.

---

## Related Documentation

- [Architecture](Architecture.md) - System design overview
- [Building Subjects](BuildingSubjects.md) - Creating custom subject types
- [Configuration Guide](Configuration.md) - Configuring HomeBlaze
- [Markdown Pages](Pages.md) - Creating interactive pages
