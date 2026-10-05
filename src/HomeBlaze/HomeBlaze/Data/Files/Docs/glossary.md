---
title: Glossary
navTitle: Glossary
position: 10
---

# Glossary

Terms and concepts used throughout HomeBlaze documentation and codebase. Each entry links to the page with deeper coverage.

---

## Core Concepts

### Subject

An intercepted object in the HomeBlaze object graph. Classes decorated with `[InterceptorSubject]` participate in property tracking, change detection, and UI integration. See [Concepts — Subjects](concepts.md#subjects) and [Building Subjects](development/building-subjects.md).

```csharp
[InterceptorSubject]
public partial class Motor : IInterceptorSubject
{
    public partial int Speed { get; set; }
}
```

### Object Graph

The hierarchical tree of all subjects in the system, rooted at a storage container. See [Concepts — The Object Graph](concepts.md#the-object-graph).

### Property

A value on a subject. Properties are categorized by attribute:

- **Configuration**: Persisted to JSON (`[Configuration]`)
- **State**: Displayed in the UI (`[State]`)
- **Derived**: Computed from other properties (`[Derived]`)

See [Concepts — Three Kinds of Properties](concepts.md#three-kinds-of-properties).

### Context

The `IInterceptorSubjectContext` that manages a subject's lifecycle, dependencies, and interceptors. Created via `InterceptorSubjectContext.Create()` with optional configuration like `.WithFullPropertyTracking()` or `.WithRegistry()`. See [Building Subjects](development/building-subjects.md).

---

## Attributes

All attributes below are documented in full with examples in [Building Subjects](development/building-subjects.md).

### `[InterceptorSubject]`
Marks a class as an interceptable subject. Class and tracked properties must be `partial`.

### `[Configuration]`
Marks a property for JSON persistence. See also [Configurable Subject Serialization](development/configurable-subject.md).

### `[State]`
Marks a property for display in the UI property panel. Options: `Name`, `Position`, `Unit` (e.g., `StateUnit.DegreeCelsius`).

### `[Derived]`
Marks a computed property that auto-updates when its dependencies change. Must be expression-bodied (`=>`).

### `[Operation]`
Marks a method as an executable action from the UI. Options: `Title`, `Description`, `Icon`, `Position`, `RequiresConfirmation`.

### `[Query]`
Marks a method as a read-only query (no side effects). Similar to `[Operation]` but semantically non-mutating.

### `[PropertyAttribute]`
Associates metadata with another property (e.g., minimum/maximum values).

```csharp
[PropertyAttribute(nameof(TargetSpeed), "Minimum")]
public partial int TargetSpeed_Minimum { get; set; }
```

---

## Methods

### Operation Method

A method marked with `[Operation]` that can be invoked from the UI. Operations typically have side effects. See [Building Subjects](development/building-subjects.md).

### Query Method

A method marked with `[Query]` that returns data without side effects. Safe to call repeatedly. See [Building Subjects](development/building-subjects.md).

---

## UI Components

Covered in depth in [Building Subjects](development/building-subjects.md) (author side) and [Markdown Pages](administration/pages.md) (consumer side).

### Subject Component

A Blazor component associated with a specific subject type, registered via `[SubjectComponent]`.

### Component Types

| Type | Purpose | Interface |
|------|---------|-----------|
| `Widget` | Inline visualization | `ISubjectComponent` |
| `Edit` | Configuration editor | `ISubjectEditComponent` |
| `Page` | Full-page view | `ISubjectComponent` |

### Widget

A compact inline visualization of a subject. See [Subjects — Widgets](administration/subjects.md#widgets) and [Markdown Pages — Embedded Subjects](administration/pages.md#embedded-subjects).

### Page

A full-page component for a subject type. Pages appear in the navigation when the subject is selected.

---

## Navigation

Set via markdown frontmatter on `.md` files — see [Markdown Pages — Frontmatter](administration/pages.md#frontmatter).

### Navigation Location

Where a page appears in the application:
- `NavBar` — Sidebar navigation (default)
- `AppBar` — Top application bar

### AppBar Alignment

For `AppBar` items:
- `Left` — Left side of the top bar (default)
- `Right` — Right side

---

## Storage

### Storage Container

A subject that provides blob storage access. The default implementation uses FluentStorage for local or cloud storage. See [Subjects, Storage & Files](administration/subjects.md) and [Storage Design](architecture/design/storage.md).

### `root.json`

The configuration file that defines the storage location and root subject type. See [Subjects — root.json](administration/subjects.md#rootjson).

### File Types

| Extension | Subject Type | Description |
|-----------|--------------|-------------|
| `.json` | Configured type | Subject defined by `$type` property |
| `.md` | `MarkdownFile` | Interactive page with expressions |
| Other | `GenericFile` | Basic file representation |

See [Subjects — File Types](administration/subjects.md#file-types).

---

## Markdown Pages

### Frontmatter

YAML metadata at the top of markdown files controlling navigation and display. See [Markdown Pages — Frontmatter](administration/pages.md#frontmatter).

### Live Expression

Dynamic value binding in markdown using `{{ path }}` syntax — updates automatically when the source property changes. See [Markdown Pages — Live Expressions](administration/pages.md#live-expressions).

### Embedded Subject

A subject defined inline within a markdown page using fenced `subject(name)` code blocks. See [Markdown Pages — Embedded Subjects](administration/pages.md#embedded-subjects).

---

## Paths

Paths reference subjects and properties in the object graph. Prefixes: `/` (absolute), `./` (relative), `../` (parent). See [Paths](administration/paths.md) for full syntax.

### Path Syntax

Full prefix table, examples, and resolution order: [Paths](administration/paths.md).

### Canonical Notation

For `[InlinePaths]` collections, child keys are inlined as path segments: `/Demo/Conveyor`. See [Paths — Canonical](administration/paths.md#canonical).

### Brackets for Collection Indices

Used for non-inlined collections: `/Devices[0]/Temperature`. See [Paths — Brackets](administration/paths.md#brackets).

---

## Interfaces

Implementation-oriented interfaces used by plugin authors. See [Building Subjects](development/building-subjects.md).

### ITitleProvider

Interface for subjects that provide a display title (human-readable name instead of the type name).

### IIconProvider

Interface for subjects that provide a display icon next to the subject name.

### IConfigurable

Interface for subjects that react to configuration changes. Called after `[Configuration]` properties are updated (e.g., after deserialization or editing).

### MethodMetadata

Registry-based metadata for subject methods. Bound to a specific subject instance and registered as a dynamic property by `MethodPropertyInitializer`. Describes kind, title, parameters, and provides direct invocation via `InvokeAsync`. Runtime-provided parameters (e.g., `CancellationToken`) are injected automatically. See [Methods Design](architecture/design/methods.md).

### MethodParameter

Describes a parameter of a subject method. Each parameter knows whether it requires user input, is resolved from DI (`IsFromServices`), or is runtime-provided (`IsRuntimeProvided`). See [Methods Design](architecture/design/methods.md).

### StateMetadata

Registry attribute metadata for `[State]` properties. Auto-registered by `PropertyAttributeInitializer` on subject attach. Contains display name, unit, position, and flags. See [Configurable Subject Serialization](development/configurable-subject.md).

### ConfigurationMetadata

Registry attribute metadata for `[Configuration]` properties. Auto-registered by `PropertyAttributeInitializer` on subject attach. See [Configurable Subject Serialization](development/configurable-subject.md).

---

## Services

### RootManager

Manages loading and saving the root subject tree from `root.json`. See [Storage Design](architecture/design/storage.md).

### SubjectTypeRegistry

Discovers and resolves subject types by name or file extension. See [Plugin System Design](architecture/design/plugins.md).

### SubjectPathResolver

Resolves path expressions to subjects in the object graph. See [Paths](administration/paths.md).

### SubjectComponentRegistry

Discovers and resolves UI components for subject types. See [Building Subjects](development/building-subjects.md).

### ConfigurableSubjectSerializer

Serializes and deserializes subjects to/from JSON with polymorphic type handling. See [Configurable Subject Serialization](development/configurable-subject.md).

---

## State Units

Units for formatting state values (used via `[State(Unit = StateUnit.X)]`):

| Unit | Display | Example |
|------|---------|---------|
| `Percent` | % | 75% |
| `DegreeCelsius` | °C | 23.5 °C |
| `Watt` | W | 100 W |
| `Kilowatt` | kW | 1.5 kW |
| `Volt` | V | 230 V |
| `Ampere` | A | 5 A |
| `Hertz` | Hz | 50 Hz |

See the `StateUnit` enum for the full list, and [Building Subjects](development/building-subjects.md) for attribute usage.

---

## Related Documentation

- [Concepts](concepts.md) — 5-minute mental model
- [Architecture Overview](architecture/overview.md) — System design (arc42)
- [Subjects, Storage & Files](administration/subjects.md) — Admin guide
- [Configuration](administration/configuration.md) — App-level settings
- [Paths](administration/paths.md) — Path syntax reference
- [Markdown Pages](administration/pages.md) — Interactive page authoring
- [Building Subjects](development/building-subjects.md) — Creating custom subject types
