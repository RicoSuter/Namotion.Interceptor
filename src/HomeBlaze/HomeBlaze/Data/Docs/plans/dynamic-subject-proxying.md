---
title: Dynamic Subject Proxying
navTitle: Dynamic Proxying
status: Planned
---

# Dynamic Subject Proxying for WebSocket Sync

**Status: Work in Progress**



## Problem

When a satellite syncs its subject graph to the central UNS via WebSocket, the central may not have the concrete types (e.g., `HueLight`, `Motor`). It cannot instantiate those types, and even if it could, they would try to connect to devices directly. The central needs a dynamic proxy representation that:

- Exposes the same properties (state and configuration)
- Implements the same domain abstraction interfaces (so registry queries like `GetAllSubjects<ITemperatureSensor>()` work)
- Proxies operations back to the satellite (via future RPC, message types 5-6)
- Works alongside concrete types when the plugin IS loaded

## Design Decisions

### Synced subjects are always dynamic proxies

Concrete types on satellites contain device connection logic (HTTP clients, SDK calls, background services). A concrete `HueLight` on the central would try to talk to the Hue bridge — which it can't reach. Therefore, subjects received via WebSocket sync are always represented as dynamic proxies, never concrete device types.

The exception is shared "dumb" model types — if both satellite and central reference the same model package containing POCOs without device logic, the concrete type can be used directly.

### Two paths to the central

**Path A: Pure dynamic** — Satellite has concrete `HueLight` with device connection logic. Central has no Hue plugin. Dynamic proxies with interfaces from the wire.

**Path B: Shared models** — A shared NuGet package (e.g., `MyDevice.Models`) contains clean data model classes (just properties, no device logic). Both satellite and central reference it. Satellite maps real device subjects into the model; central receives the model as a concrete type.

Both paths must work. Dynamic proxying with interfaces is the universal fallback. Shared models are an optimization for when you control the plugin and want full static typing on the central.

### Configuration properties are dynamic

Abstraction interfaces define state properties (`ITemperatureSensor.Temperature`), not configuration properties (`HueLight.ApiKey`). Configuration properties are specific to concrete types and don't appear on any interface. On the dynamic proxy, they become dynamic properties with registry attributes marking them as configuration.

## Wire Protocol: Subject Interfaces

### SubjectAbstractionAssemblyAttribute

A marker attribute in `Namotion.Interceptor` core that designates assemblies containing subject abstraction interfaces eligible for transmission:

```csharp
namespace Namotion.Interceptor;

[AttributeUsage(AttributeTargets.Assembly)]
public class SubjectAbstractionAssemblyAttribute : Attribute { }
```

Applied in abstraction packages:

```csharp
// HomeBlaze.Abstractions/AssemblyInfo.cs
[assembly: SubjectAbstractionAssembly]

// MyPlugin.Abstractions/AssemblyInfo.cs
[assembly: SubjectAbstractionAssembly]
```

### Interface Discovery on Sender

When `SubjectUpdateFactory.ProcessSubjectComplete` runs with interface inclusion enabled:

1. `subject.GetType().GetInterfaces()`
2. Filter: interface's declaring assembly has `[SubjectAbstractionAssembly]`
3. Exclude: `Namotion.Interceptor.*` assemblies (core infrastructure interfaces)
4. Map to: `{ type (full name), assembly (name), version (assembly version) }`

The interface list per concrete .NET type is cached in a `ConcurrentDictionary<Type, SubjectInterfaceMetadata[]>` to avoid repeated reflection.

Edge cases:

- **Interface inheritance** (e.g., `ISwitchDevice : ISwitchState, ISwitchController`) — include all interfaces individually; the receiver resolves what it can
- **Generic interfaces** (e.g., `IObservable<DeviceEvent>`) — skip for now, revisit later
- **No qualifying interfaces** — `interfaces` field omitted from the wire

### Wire Format

The `SubjectUpdate` gains an optional `interfaces` field per subject. Included only when a subject first appears (Welcome snapshot or structural change) — not on incremental property updates.

```json
{
  "root": "1",
  "subjects": {
    "1": {
      "interfaces": [
        {
          "type": "HomeBlaze.Abstractions.Devices.ITemperatureSensor",
          "assembly": "HomeBlaze.Abstractions",
          "version": "1.2.0"
        },
        {
          "type": "HomeBlaze.Abstractions.Devices.ISwitchController",
          "assembly": "HomeBlaze.Abstractions",
          "version": "1.2.0"
        }
      ],
      "temperature": {
        "kind": "Value",
        "value": 23.5,
        "attributes": {
          "state": {
            "kind": "Value",
            "value": { "name": "Temperature", "unit": "DegreeCelsius", "position": 1 }
          }
        }
      },
      "apiKey": {
        "kind": "Value",
        "value": "secret",
        "attributes": {
          "configuration": {
            "kind": "Value",
            "value": {}
          }
        }
      }
    }
  }
}
```

### Registry Attributes on the Wire

Registry attributes are transmitted in the existing `SubjectPropertyUpdate.Attributes` mechanism — no protocol change needed. Attribute values are objects containing metadata:

```json
"attributes": {
  "state": {
    "kind": "Value",
    "value": { "name": "Temperature", "unit": "DegreeCelsius", "position": 1 }
  }
}
```

## Receiving Side: DynamicWebSocketSubjectFactory

### Configuration

The WebSocket connector defaults to current behavior. Dynamic proxy creation is opt-in on both sides:

| Setting | Side | Default | Purpose |
|---|---|---|---|
| `IncludeInterfaces` | Server/sender | `false` | Emit `interfaces` in SubjectUpdate using hardcoded `[SubjectAbstractionAssembly]` check |
| `SubjectFactory` | Client/receiver | `DefaultSubjectFactory` (wrapped) | No dynamic handling unless explicitly configured with `DynamicWebSocketSubjectFactory` |

### DynamicWebSocketSubjectFactory

Follows the same pattern as `OpcUaSubjectFactory` — a connector-specific factory with its own method signature, wrapping `ISubjectFactory` for the default path:

```csharp
public class DynamicWebSocketSubjectFactory
{
    private readonly ISubjectFactory _subjectFactory;

    public IInterceptorSubject CreateSubject(
        RegisteredSubjectProperty property,
        SubjectInterfaceMetadata[]? interfaces,
        IServiceProvider? serviceProvider)
    {
        // 1. Property type is concrete and instantiable → use DefaultSubjectFactory
        // 2. Property type is abstract/interface → create dynamic proxy from interfaces
    }
}
```

### Subject Creation Decision

```
property.Type is concrete, instantiable class (e.g., MotorModel)
  → DefaultSubjectFactory creates the concrete instance
  → Wire interfaces ignored (concrete type already implements them)

property.Type is abstract/interface (e.g., IInterceptorSubject)
  → Resolve interfaces from wire metadata
  → DynamicSubjectFactory.CreateDynamicSubject(resolvedInterfaces)
```

### Interface Resolution with Version Check

For each interface entry from the wire:

1. Try `Type.GetType("type, assembly")`
2. **Not found** → add to `unresolvedInterfaces` with reason `"not found"`
3. **Found, major version matches** → include in dynamic proxy (semver: minor adds new interfaces, doesn't break existing)
4. **Found, major version differs** → add to `unresolvedInterfaces` with reason `"version mismatch"`

If any interfaces resolved → `DynamicSubjectFactory.CreateDynamicSubject(resolvedInterfaces)`
If none resolved → `DynamicSubjectFactory.CreateDynamicSubject()` (plain dynamic, no interfaces)

If any unresolved → set dynamic attribute `$unresolvedInterfaces` on subject:

```json
[
  { "type": "...", "assembly": "...", "version": "...", "reason": "not found" },
  { "type": "...", "assembly": "...", "version": "...", "reason": "version mismatch" }
]
```

The UI shows a warning badge on subjects with unresolved interfaces.

### Dynamic Property Creation for Unknown Properties

When the `SubjectUpdateApplier` encounters properties that don't exist on the local subject (neither static nor already dynamic):

1. `registeredSubject.AddProperty(name, inferredType, getter, setter)` — creates a dynamic property
2. Set value on the new property
3. Apply registry attributes from the wire (`state`, `configuration`, etc.) via `AddAttribute`

This is part of using `DynamicWebSocketSubjectFactory` — opting into dynamic proxy creation also enables dynamic property creation for unknown fields. Otherwise the proxy would be missing data.

## Subject Graph Structure on Central

```
Central UNS
+-- local/                          <- locally owned concrete subjects
|   +-- opcua-server (concrete)
+-- satellites/                     <- dictionary of IInterceptorSubject
    +-- satellite-a/                <- dynamic proxy, root of satellite's graph
        +-- hue-bridge              <- dynamic, implements [ITemperatureSensor, ...]
        +-- motor                   <- dynamic, implements [IMotor, ...]
```

Satellite subtrees arrive via WebSocket into generic collection/dictionary properties (`Dictionary<string, IInterceptorSubject>`). The property type is `IInterceptorSubject`, so `DefaultSubjectFactory` can't create a concrete type — the `DynamicWebSocketSubjectFactory` creates dynamic proxies with interfaces.

## Data Flow Summary

### Satellite (sender)

```
Concrete HueLight subject
  -> SubjectUpdateFactory.ProcessSubjectComplete
     -> Inspects subject interfaces
     -> Filters by [SubjectAbstractionAssembly], excludes core
     -> Adds "interfaces" field to SubjectUpdate (Welcome + new subjects only)
     -> Properties serialized with registry attributes (state, configuration)
  -> Sent via WebSocket
```

### Central (receiver)

```
SubjectUpdate arrives
  -> SubjectUpdateApplier needs to create subject
  -> DynamicWebSocketSubjectFactory.CreateSubject(property, interfaces, sp)
     -> property.Type is IInterceptorSubject (generic container)
     -> Resolves interfaces from wire metadata (version check)
     -> DynamicSubjectFactory.CreateDynamicSubject(resolvedInterfaces)
     -> Sets $unresolvedInterfaces attribute if any
  -> Properties applied:
     -> Interface properties: set on typed proxy properties
     -> Unknown properties: AddProperty (dynamic), then set value
     -> Registry attributes: applied from wire (state, configuration, etc.)
  -> Result: fully browsable, queryable, interface-castable dynamic proxy
```

## Dependencies

- `Namotion.Interceptor` (core): `SubjectAbstractionAssemblyAttribute`
- `Namotion.Interceptor.Dynamic`: `DynamicSubjectFactory` for proxy creation
- `Namotion.Interceptor.Connectors`: `SubjectUpdate` model, `ISubjectFactory`
- `Namotion.Interceptor.WebSocket`: `DynamicWebSocketSubjectFactory`, configuration flags
- `HomeBlaze.Abstractions`: `[assembly: SubjectAbstractionAssembly]`, `StateAttribute`, `ConfigurationAttribute` implement `ISubjectPropertyInitializer`
- HomeBlaze UI/Services: migrate from reflection to registry attribute queries

## Open Questions

- **Operation proxying**: Methods/operations are not covered by property sync. Cross-instance RPC (WebSocket message types 5-6) is future work. Method metadata (name, parameters, operation/query marker) could be included as subject-level metadata so the central knows what operations exist.
- **Generic interfaces**: `IObservable<T>` and similar generic interfaces are skipped for now. May need special handling later.
- **Hot reload**: If a plugin is reloaded on the satellite and adds an interface, the current design doesn't propagate that change. Acceptable for now since interfaces are immutable per subject lifetime.
