---
title: Registry Attribute Migration
navTitle: Attribute Migration
status: Planned
---

# Registry Attribute Migration

**Status: Work in Progress**

## Problem

HomeBlaze UI and services currently read property metadata (`[State]`, `[Configuration]`, `[Operation]`, `[Query]`) via .NET reflection on the concrete C# type. This only works when the concrete type is available. For dynamic proxied subjects (from WebSocket sync or OPC UA discovery), there is no concrete type to reflect on.

The registry already supports dynamic attributes on properties. If all consumers read metadata from registry attributes instead of reflection, the UI works identically for concrete and dynamic subjects.

## Design

### Registry attributes as single source of truth

All property metadata migrates from reflection-based C# attribute reading to registry attribute queries. C# attributes become authoring sugar that auto-registers registry attributes via `ISubjectPropertyInitializer`.

### Attribute Registration via ISubjectPropertyInitializer

### KnownAttributes Constants

All registry attribute names are defined as constants in the existing `KnownAttributes` class (`HomeBlaze.Abstractions`), avoiding magic strings:

```csharp
public static class KnownAttributes
{
    public const string IsEnabled = "IsEnabled";

    // New:
    public const string State = "state";
    public const string Configuration = "configuration";
    public const string Operation = "operation";
    public const string Query = "query";
}
```

All `ISubjectPropertyInitializer` implementations and all consumer code use these constants.

### Attribute Registration via ISubjectPropertyInitializer

C# attributes implement `ISubjectPropertyInitializer` to auto-register registry attributes when properties are created:

```csharp
public class StateAttribute : Attribute, ISubjectPropertyInitializer
{
    public string? Name { get; }
    public StateUnit? Unit { get; set; }
    public int? Position { get; set; }

    public void InitializeProperty(RegisteredSubjectProperty property)
    {
        property.AddAttribute(KnownAttributes.State, typeof(StateMetadata),
            _ => new StateMetadata { Name = Name, Unit = Unit, Position = Position },
            null);
    }
}

public class ConfigurationAttribute : Attribute, ISubjectPropertyInitializer
{
    public void InitializeProperty(RegisteredSubjectProperty property)
    {
        property.AddAttribute(KnownAttributes.Configuration, typeof(ConfigurationMetadata),
            _ => new ConfigurationMetadata(),
            null);
    }
}
```

### Attribute values are objects

Attribute values are always objects containing the metadata — extensible without breaking changes:

```csharp
public class StateMetadata
{
    public string? Name { get; set; }
    public StateUnit? Unit { get; set; }
    public int? Position { get; set; }
}

public class ConfigurationMetadata { }

public class OperationMetadata
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public int? Position { get; set; }
    public bool RequiresConfirmation { get; set; }
}

public class QueryMetadata
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public int? Position { get; set; }
}
```

### Attribute Mapping

| C# Attribute | Registry Attribute | Value Object |
|---|---|---|
| `[State("Name", Unit = StateUnit.DegreeCelsius, Position = 1)]` | `"state"` | `{ name, unit, position }` |
| `[Configuration]` | `"configuration"` | `{ }` (extensible) |
| `[Operation(Title = "Stop", RequiresConfirmation = true)]` | `"operation"` | `{ title, description, icon, position, requiresConfirmation }` |
| `[Query(Title = "Diagnostics")]` | `"query"` | `{ title, description, icon, position }` |

### Consumer Migration

All HomeBlaze UI and services migrate from reflection to registry attribute queries:

| Consumer | Before (reflection) | After (registry) |
|---|---|---|
| Property panel | `property.GetCustomAttribute<StateAttribute>()` | `registeredProperty.TryGetAttribute("state")` |
| Config serializer | `property.GetCustomAttribute<ConfigurationAttribute>()` | `registeredProperty.TryGetAttribute("configuration")` |
| Operations UI | `method.GetCustomAttribute<OperationAttribute>()` | `registeredProperty.TryGetAttribute("operation")` |
| MCP tools | Same reflection pattern | Same registry pattern |

### Wire Transport

Registry attributes are transmitted in the existing `SubjectPropertyUpdate.Attributes` mechanism — no protocol change needed. Attribute values are objects containing metadata:

```json
"attributes": {
  "state": {
    "kind": "Value",
    "value": { "name": "Temperature", "unit": "DegreeCelsius", "position": 1 }
  },
  "configuration": {
    "kind": "Value",
    "value": {}
  }
}
```

Dynamic proxied subjects on the central receive the same attributes that concrete subjects register locally.

## Benefits

This makes the UI and services work identically for:

- **Concrete typed subjects** — attributes auto-registered via `ISubjectPropertyInitializer` at property creation
- **Dynamic proxied subjects from WebSocket sync** — attributes arrived over the wire in `SubjectPropertyUpdate.Attributes`
- **Dynamic subjects from OPC UA discovery** — attributes added programmatically via `AddAttribute`

## Migration Scope

### Step 1: Add ISubjectPropertyInitializer to C# Attributes (HomeBlaze.Abstractions)

- `StateAttribute` — register `"state"` attribute with `StateMetadata`
- `ConfigurationAttribute` — register `"configuration"` attribute with `ConfigurationMetadata`
- `OperationAttribute` — register `"operation"` attribute with `OperationMetadata`
- `QueryAttribute` — register `"query"` attribute with `QueryMetadata`

### Step 2: Migrate Extension Methods (HomeBlaze.Services)

**`SubjectRegistryExtensions.cs`** — the key file. Most UI consumers call these extensions.

| Method | Current (reflection) | After (registry) |
|---|---|---|
| `IsConfigurationProperty()` | Iterates `property.ReflectionAttributes` for `ConfigurationAttribute` | `registeredProperty.TryGetAttribute("configuration") != null` |
| `GetStateAttribute()` | Iterates `property.ReflectionAttributes` for `StateAttribute` | `registeredProperty.TryGetAttribute("state")?.GetValue() as StateMetadata` |
| `GetConfigurationProperties()` | Calls `IsConfigurationProperty()` | Automatic — uses migrated method |
| `GetStateProperties()` | Calls `GetStateAttribute()` | Automatic |
| `GetDisplayName()` | Reads `StateAttribute.Name` | Reads `StateMetadata.Name` |
| `GetDisplayPosition()` | Reads `StateAttribute.Position` | Reads `StateMetadata.Position` |

**`RegisteredSubjectMethodExtensions.cs`** — method discovery.

| Current | After |
|---|---|
| `method.GetCustomAttribute<OperationAttribute>()` | Registry attribute lookup for `"operation"` |
| `method.GetCustomAttribute<QueryAttribute>()` | Registry attribute lookup for `"query"` |

### Step 3: Migrate JSON Serialization (HomeBlaze.Services)

**`ConfigurableSubjectSerializer.cs`** — checks `[Configuration]` during deserialization. Subjects are in the graph with a context by this point, so registry is available.

**`ConfigurationJsonTypeInfoResolver.cs`** — checks `[Configuration]` during serialization. Same — subjects are attached and registered.

Both migrate from `GetCustomAttributes(typeof(ConfigurationAttribute))` to registry attribute lookup.

### Step 4: UI Consumers (Automatic)

These all call the extension methods from Step 2, so they migrate automatically:

| File | What it does |
|---|---|
| `StateUnitExtensions.cs` | Reads `StateAttribute.Unit` → reads `StateMetadata.Unit` |
| `SubjectPropertyPanel.razor` | Filters/orders by state attribute → uses migrated extensions |
| `SubjectPathPicker.razor` | Checks state attribute for visibility → uses migrated extensions |

### Out of Scope (Stays as Reflection)

| File | Why |
|---|---|
| `SubjectTypeRegistry.cs` | Scans assemblies for `[InterceptorSubject]` and `[FileExtension]` — type-level discovery, not property metadata |
| `SubjectComponentRegistry.cs` | Scans for `[SubjectComponent]` — type-level component registration |

### Total Effort

~4 files of real migration work (`SubjectRegistryExtensions`, `RegisteredSubjectMethodExtensions`, `ConfigurableSubjectSerializer`, `ConfigurationJsonTypeInfoResolver`). UI files follow automatically.

## Dependencies

- `Namotion.Interceptor.Registry`: `ISubjectPropertyInitializer`, `AddAttribute`
- `HomeBlaze.Abstractions`: `StateAttribute`, `ConfigurationAttribute`, `OperationAttribute`, `QueryAttribute` gain `ISubjectPropertyInitializer` implementations
- `HomeBlaze.Services`: migrate service-level consumers from reflection to registry
- `HomeBlaze.Host.Services`: migrate display helpers from reflection to registry
- `HomeBlaze.Host`: migrate Blazor UI components from reflection to registry

## Dependents

- [Dynamic Subject Proxying](dynamic-subject-proxying.md) — requires this migration for dynamic proxies to be fully functional in the UI
