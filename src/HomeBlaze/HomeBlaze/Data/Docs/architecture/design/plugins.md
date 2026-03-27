---
title: Plugin System
navTitle: Plugins
status: Implemented
---

# Plugin System Design

## Overview

Everything in HomeBlaze is a subject, and a plugin is simply a NuGet package that provides subject types. Connectors, agents, document stores, device subjects, UI components, and business logic are all delivered as plugins.

## What a Plugin Provides [Implemented]

One or more `[InterceptorSubject]` classes. That is the only contract.

| Role | Example |
|------|---------|
| Device connector | An OPC UA client subject with `[SourcePath]` properties |
| Protocol server | An MQTT or OPC UA server subject exposing the graph |
| AI agent | A `BackgroundService` subject with LLM integration |
| Document store | A storage subject managing files |
| Dashboard | A subject with `[SubjectEditor(typeof(...))]` for custom Blazor UI |
| Business logic | A subject with `[Operation]` methods and `[Derived]` properties |
| Domain model | A domain-specific subject (Press, Motor, Thermostat) |

## UI Association [Implemented]

Plugins can associate custom Blazor UI components with their subjects:

```csharp
[InterceptorSubject]
[SubjectEditor(typeof(PressEditor))]
public partial class Press
{
    public partial decimal Temperature { get; set; }
    public partial decimal Speed { get; set; }
}
```

## Plugin Loading

### Build-time (compiled in) [Implemented]

Core plugins are standard NuGet package references:

```xml
<ItemGroup>
  <PackageReference Include="Namotion.Interceptor.Mqtt" Version="..." />
  <PackageReference Include="Namotion.Interceptor.OpcUa" Version="..." />
  <PackageReference Include="HomeBlaze.Philips.Hue" Version="..." />
</ItemGroup>
```

### Runtime (dynamic) [Planned]

Additional plugins specified in configuration as NuGet packages, resolved and loaded at startup:

```json
{
  "Plugins": {
    "Feeds": [
      "https://api.nuget.org/v3/index.json",
      "https://my-company.pkgs.visualstudio.com/nuget/v3/index.json"
    ],
    "Packages": [
      { "id": "HomeBlaze.Zigbee", "version": "1.2.0" },
      { "id": "MyCompany.PlantFloor", "version": "3.0.1" }
    ]
  }
}
```

On startup: resolve packages from feeds, download to local cache, load assemblies, scan for `[InterceptorSubject]` types, register with subject type registry, and make available for instantiation.

### Bootstrap Ordering [Planned]

Plugin loading must happen before configuration is loaded, because configuration references subject types from plugins. The bootstrap sequence is:

1. Plugin resolution and assembly loading (core DI service, not a subject)
2. Subject type registration from all loaded assemblies
3. Configuration loaded, subjects instantiated from config
4. Connectors activate, state flows

The plugin loading mechanism itself needs its own detailed design — it is a core DI service rather than a subject, as it must bootstrap before the subject infrastructure is available.

## Same Binary, Different Config [Implemented]

The HomeBlaze binary ships with all core plugins compiled in. Each deployment's configuration determines which additional packages to load, which subjects to instantiate, which connectors to activate, whether the UI is enabled, and the node's role in the topology.

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Plugin contract | Subject types only | Everything is a subject. No separate plugin interfaces for connectors, agents, UI |
| Distribution | NuGet packages | Standard .NET ecosystem, versioning, feeds |
| Loading modes | Build-time + runtime | Core compiled in, extensibility via dynamic loading |
| Bootstrap | Core DI service, not a subject | Must load before subject infrastructure is available |

## Open Questions

- Runtime plugin loader detailed design (NuGet resolution, assembly loading, error handling)
- Plugin update mechanism (hot reload vs. restart required)
- Plugin dependency resolution (what if two plugins need different versions of a shared dependency)
- **Assembly isolation strategy** — Loading all plugins into the default `AssemblyLoadContext` is simplest but means two plugins with conflicting transitive dependencies (e.g., different versions of the same library) crash the host. Using isolated `AssemblyLoadContext` per plugin solves conflicts but introduces type identity problems: if plugin A and plugin B each load their own copy of `HomeBlaze.Abstractions`, their interfaces are incompatible types — casting fails, registry can't discover them. The standard workaround is loading shared abstractions in the default context while isolating plugin-specific dependencies, but this requires a well-defined boundary of "shared" packages and still doesn't cover all conflict scenarios. This is a fundamental design decision for the runtime loader
