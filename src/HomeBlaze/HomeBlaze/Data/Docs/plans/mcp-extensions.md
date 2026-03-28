---
title: HomeBlaze MCP Extensions
navTitle: MCP Extensions
status: Planned
---

# HomeBlaze MCP Extensions Plan

**Status: Planned**

**Prerequisites:**
- [MCP Server](../../../../../docs/plans/mcp-server.md) — core `Namotion.Interceptor.Mcp` package

## Overview

HomeBlaze-specific extensions for the MCP server, configured via the core `McpServerConfiguration`. These extensions enrich the generic interceptor MCP server with HomeBlaze domain metadata, type discovery, method tools, and property filtering.

Implemented together with the core MCP server — no agent or LLM dependency required.

## Package

All HomeBlaze MCP extensions live in `HomeBlaze.AI` (alongside the agent code, but independently usable):

```
HomeBlaze.AI → Namotion.Interceptor.Mcp → Namotion.Interceptor.Registry
             → HomeBlaze.Abstractions
             → HomeBlaze.Services
```

## Configuration

```csharp
services.AddMcpSubjectServer(subject, new McpServerConfiguration
{
    PathProvider = new StateAttributePathProvider(),
    SubjectEnrichers = { new HomeBlazeMcpSubjectEnricher() },
    TypeProviders =
    {
        new SubjectAbstractionsAssemblyTypeProvider(),
        new SubjectTypeRegistryTypeProvider(typeRegistry)
    },
    ToolProviders = { new HomeBlazeMcpToolProvider() },
    IsReadOnly = false
});
```

## Implementations

### StateAttributePathProvider (IPathProvider)

Determines which properties are exposed via MCP. Extends `PathProviderBase` and reads the `"State"` registry attribute so all `[State]` properties are automatically visible without extra annotations:

```csharp
public class StateAttributePathProvider : PathProviderBase
{
    public override bool IsPropertyIncluded(RegisteredSubjectProperty property)
        => property.TryGetAttribute(KnownAttributes.State) != null;

    public override string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        var metadata = property.TryGetAttribute(KnownAttributes.State)?.GetValue() as StateMetadata;
        return metadata?.Name ?? property.Name;
    }
}
```

### HomeBlazeMcpSubjectEnricher (IMcpSubjectEnricher)

Adds subject-level metadata to `query` responses. The core MCP package has no knowledge of `ITitleProvider` or `IIconProvider` — this enricher bridges that gap.

```csharp
public class HomeBlazeMcpSubjectEnricher : IMcpSubjectEnricher
{
    public void EnrichSubject(RegisteredSubject subject,
                              IDictionary<string, object?> metadata)
    {
        if (subject.Subject is ITitleProvider titleProvider)
            metadata["$title"] = titleProvider.Title;

        if (subject.Subject is IIconProvider iconProvider)
            metadata["$icon"] = iconProvider.IconName;

        metadata["$type"] = subject.Subject.GetType().Name;
    }
}
```

Property-level metadata (units, position, etc.) is already available as registry attributes (`StateMetadata`) and included directly in `query` responses when `includeAttributes=true` — no enrichment needed.

### SubjectTypeRegistryTypeProvider (IMcpTypeProvider)

Returns concrete subject types from HomeBlaze's `SubjectTypeRegistry` (the `$type` discriminator → CLR type mappings used for JSON polymorphism):

```csharp
public class SubjectTypeRegistryTypeProvider : IMcpTypeProvider
{
    private readonly SubjectTypeRegistry _typeRegistry;

    public SubjectTypeRegistryTypeProvider(SubjectTypeRegistry typeRegistry)
        => _typeRegistry = typeRegistry;

    public IEnumerable<McpTypeInfo> GetTypes()
    {
        // Read registered $type → CLR type mappings
        // Return each as McpTypeInfo with IsInterface = false
    }
}
```

Combined with the core `SubjectAbstractionsAssemblyTypeProvider` (which returns interfaces from `[SubjectAbstractionsAssembly]` assemblies), the `list_types` tool provides both abstractions and concrete types.

### HomeBlazeMcpToolProvider (IMcpToolProvider)

Registers two additional tools. These are HomeBlaze-specific because method discovery uses `MethodMetadata` with `[Operation]`/`[Query]` registry attributes — a HomeBlaze convention, not a core interceptor concept.

```csharp
public class HomeBlazeMcpToolProvider : IMcpToolProvider
{
    public IEnumerable<McpToolDescriptor> GetTools()
    {
        yield return new McpToolDescriptor
        {
            Name = "list_methods",
            Description = "List operations and queries available on a subject.",
            InputSchema = ...,
            Handler = ListMethodsAsync
        };

        yield return new McpToolDescriptor
        {
            Name = "invoke_method",
            Description = "Execute a method on a subject. When server is read-only, only query methods are allowed.",
            InputSchema = ...,
            Handler = InvokeMethodAsync
        };
    }
}
```

#### `list_methods` Tool

List operations and queries on a subject, discovered from `MethodMetadata` registry properties.

**Request:**
```json
{ "path": "root.thermostat" }
```

**Response:**
```json
{
  "methods": [
    {
      "name": "SetTarget",
      "kind": "operation",
      "parameters": [{ "name": "temperature", "type": "decimal" }]
    },
    {
      "name": "TurnOff",
      "kind": "operation",
      "parameters": []
    },
    {
      "name": "GetDiagnostics",
      "kind": "query",
      "parameters": []
    }
  ]
}
```

#### `invoke_method` Tool

Execute a method on a subject. When `IsReadOnly=true`, only methods with `MethodKind.Query` are allowed — operations are blocked.

**Request:**
```json
{ "path": "root.thermostat", "method": "SetTarget", "arguments": { "temperature": 23 } }
```

**Response:**
```json
{ "success": true }
```

## Access Control

| `IsReadOnly` | `set_property` | `invoke_method` (Query) | `invoke_method` (Operation) |
|--------------|---------------|------------------------|-----------------------------|
| `true` | Blocked | Allowed | Blocked |
| `false` | Allowed | Allowed | Allowed |

Full per-subject and per-agent authorization is a separate cross-cutting concern (graph-level authorization).

## Future Tools

When the underlying systems are implemented, additional tools will be added via `HomeBlazeMcpToolProvider`:

| Tool | Parameters | Description | Depends on |
|------|-----------|-------------|------------|
| `get_property_history` | `path`, `from?`, `to?` | Query time-series data | Time-series history |
| `get_event_history` | `from?`, `to?`, `eventType?`, `sourcePath?` | Query persisted events | Event store |
| `get_command_history` | `from?`, `to?`, `commandType?`, `sourcePath?` | Query persisted commands | Event store |

## Dependencies

- `Namotion.Interceptor.Mcp`: `McpServerConfiguration`, `IMcpSubjectEnricher`, `IMcpTypeProvider`, `IMcpToolProvider`, `McpToolDescriptor`
- `HomeBlaze.Abstractions`: `ITitleProvider`, `IIconProvider`, `MethodMetadata`, `[Operation]`, `[Query]`, `KnownAttributes`, `StateMetadata`
- `HomeBlaze.Services`: `SubjectTypeRegistry`, `RegisteredSubjectMethodExtensions`
