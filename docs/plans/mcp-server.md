# MCP Server Plan

**Status: Planned**

Expose the subject registry via MCP (Model Context Protocol) server, enabling external AI agents to browse, query, and interact with the knowledge graph.

**Prerequisite:** [Registry Attribute Migration](../../src/HomeBlaze/HomeBlaze/Data/Docs/plans/registry-attribute-migration.md) — HomeBlaze metadata provider reads from registry attributes instead of reflection.

## Overview

Create `Namotion.Interceptor.Mcp` package that exposes the subject registry via MCP, enabling AI assistants and autonomous agents to browse, read, and write properties and discover types on the object graph.

Tool implementations are plain methods, wrapped as both MCP tools (for external agents) and `AIFunction` objects (for in-process use by built-in agents in `HomeBlaze.AI`). One implementation, two delivery modes.

## Package Scope

**`Namotion.Interceptor.Mcp`** provides 4 core tools:

| Tool | Parameters | Description |
|------|------------|-------------|
| `query` | `path?`, `depth?`, `includeProperties?`, `filter?` | Browse/query subject tree |
| `get_property` | `path` | Read property value |
| `set_property` | `path`, `value` | Write property value |
| `list_types` | none | List available interfaces |

Method-related tools (`list_methods`, `invoke_method`) are HomeBlaze-specific and live in `HomeBlaze.AI` — the core interceptor registry has no concept of "methods on a subject."

### Extension Points

- `IPathProvider` — determines which properties are exposed (filtering + path segments)
- `IMcpMetadataProvider` — enriches metadata (title, description, icon, units)

```csharp
public interface IPathProvider
{
    bool IsPropertyIncluded(RegisteredSubjectProperty property);
    string? TryGetPropertySegment(RegisteredSubjectProperty property);
}

public interface IMcpMetadataProvider
{
    void EnrichSubjectMetadata(RegisteredSubject subject, McpSubjectMetadata metadata);
    void EnrichPropertyMetadata(RegisteredSubjectProperty property, McpPropertyMetadata metadata);
    IEnumerable<McpTypeInfo> GetKnownTypes() => [];
}
```

### Usage

```csharp
// Generic usage (explicit [Path("mcp", ...)] attributes)
services.AddMcpServer(subject);

// HomeBlaze usage — one-liner with sensible defaults
services.AddHomeBlazeMcpServer(subject);
```

## Tool Specifications

### `query` Tool

Flexible querying with filters and depth control.

**Parameters:**
```
path?: string           // Starting path (default: root)
depth?: number          // Max depth (default: 1, max: configurable)
includeProperties?: bool // Include property values (default: false)
filter?: {
  types?: string[]      // Filter by interface full names
}
```

**Response:**
```json
{
  "path": "/home",
  "subjects": {
    "livingRoom": {
      "$type": "LivingRoom",
      "$interfaces": [
        "HomeBlaze.Abstractions.Sensors.ITemperatureSensor",
        "HomeBlaze.Abstractions.Sensors.IHumiditySensor"
      ],
      "$title": "Living Room",
      "$icon": "Weekend",
      "$hasChildren": false,
      "temperature": { "value": 21.5, "unit": "DegreeCelsius", "isWritable": false },
      "humidity": { "value": 45, "unit": "Percent", "isWritable": false }
    },
    "bedroom": {
      "$type": "Bedroom",
      "$interfaces": ["HomeBlaze.Abstractions.Sensors.ITemperatureSensor"],
      "$title": "Bedroom",
      "$hasChildren": true,
      "temperature": { "value": 19.2, "unit": "DegreeCelsius", "isWritable": false }
    }
  },
  "truncated": false,
  "subjectCount": 2
}
```

**Response conventions:**
- `$` prefix for metadata (`$type`, `$interfaces`, `$title`, `$icon`, `$hasChildren`)
- Property names without prefix are actual properties
- `$hasChildren` indicates if there are children beyond current depth
- `truncated` flag when limit is hit

### `list_types` Tool

Returns available interfaces (capabilities), not concrete types.

**Response:**
```json
{
  "types": [
    {
      "name": "HomeBlaze.Abstractions.Sensors.ITemperatureSensor",
      "description": "Provides temperature readings"
    },
    {
      "name": "HomeBlaze.Abstractions.Devices.ISwitchDevice",
      "description": "Can be turned on/off"
    }
  ]
}
```

### `get_property` / `set_property` Tools

**get_property:**
```json
// Request
{ "path": "/home/thermostat/temperature" }

// Response
{ "value": 21.5, "type": "decimal", "isWritable": false, "unit": "DegreeCelsius" }
```

**set_property:**
```json
// Request
{ "path": "/home/thermostat/target", "value": 23.0 }

// Response
{ "success": true, "previousValue": 22.0 }
```

## Project Structure

```
Namotion.Interceptor.Mcp/
├── McpSubjectServer.cs
├── McpServerConfiguration.cs
├── InterceptorSubjectContextExtensions.cs
├── Abstractions/
│   ├── IPathProvider.cs
│   ├── IMcpMetadataProvider.cs
│   ├── McpTypeInfo.cs
│   ├── McpSubjectMetadata.cs
│   └── McpPropertyMetadata.cs
└── Tools/
    ├── QueryTool.cs
    ├── GetPropertyTool.cs
    ├── SetPropertyTool.cs
    └── ListTypesTool.cs
```

## Safety Limits

Configurable limits to prevent overwhelming responses:
- Max depth (default: 10)
- Max subjects in response (default: 100)
- Truncation indicator when limit is hit

## Access Control

MCP exposes `set_property`, so unrestricted access is unsafe even for development. Minimal access control before shipping:
- Connection-level allow/deny or read-only mode
- Coordinate with graph-level authorization for full access control

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Core tool scope | `query`, `get_property`, `set_property`, `list_types` | Only tools the core registry can support. Method tools are HomeBlaze-specific |
| On-demand only | No push notifications or change tracking | AI queries when needed |
| Interfaces over types | `list_types` returns capability interfaces, not concrete implementations | More stable, plugin-independent |
| Full interface names | Use full namespace.interface names | Avoid collisions, support plugins |
| Extension points | `IPathProvider` + `IMcpMetadataProvider` | HomeBlaze and plugins enhance without core dependencies |
| Property discovery | Via `RegisteredSubjectProperty` | Use existing registry infrastructure, not raw reflection |
| Tool reuse | Plain methods wrapped as both MCP tools and `AIFunction` | One implementation, two delivery modes (external MCP + in-process agents) |

## Dependencies

- `Namotion.Interceptor.Registry`: subject and property discovery
- `ModelContextProtocol` (v1.1.0): MCP server SDK
- `Microsoft.Extensions.AI.Abstractions`: `AIFunction` for in-process tool reuse
- [Registry Attribute Migration](../../src/HomeBlaze/HomeBlaze/Data/Docs/plans/registry-attribute-migration.md): HomeBlaze metadata provider reads from registry attributes
- Graph-level authorization: access control before exposing write tools
