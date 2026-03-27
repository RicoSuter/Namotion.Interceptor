# MCP Server Design

**Status: Work in Progress**

Expose the subject registry via MCP (Model Context Protocol) server, enabling external AI agents to browse, query, and interact with the knowledge graph.

**PR:** [#158](https://github.com/RicoSuter/Namotion.Interceptor/pull/158)

**Prerequisite:** [Registry Attribute Migration](../../src/HomeBlaze/HomeBlaze/Data/Docs/plans/registry-attribute-migration.md) — HomeBlaze metadata provider reads from registry attributes instead of reflection.

## Overview

Create `Namotion.Interceptor.Mcp` package that exposes the subject registry via MCP, enabling AI assistants and autonomous agents to browse, read, write, and invoke methods on the object graph.

## Use Cases

1. **AI Assistant for Monitoring/Control** — An AI that can browse, read, and control the object graph (e.g., home automation, industrial monitoring)
2. **Autonomous Agent Integration** — Enable AI agents to autonomously interact with and manipulate subjects based on goals
3. **Developer Assistance** — Natural language debugging, live system exploration, documentation on demand

## Architecture

### Package Structure

**Namotion.Interceptor.Mcp** (core package):
- Uses `IPathProvider` to determine which properties are exposed
- Default: `AttributeBasedPathProvider("mcp")` — explicit `[Path("mcp", "...")]` opt-in
- Uses `IMcpMetadataProvider` for extensible metadata
- Tool implementations are also available as `AIFunction` objects for in-process use by built-in agents (see [HomeBlaze AI Agents](../../src/HomeBlaze/HomeBlaze/Data/Docs/plans/ai-agents.md))

**HomeBlaze.AI** (HomeBlaze integration — see [AI Agents plan](../../src/HomeBlaze/HomeBlaze/Data/Docs/plans/ai-agents.md)):
- Provides `StateAttributePathProvider` — includes properties with `"state"` registry attribute
- Provides `HomeBlazeMcpMetadataProvider` — reads title, icon, unit, etc. from registry attributes (requires [Registry Attribute Migration](../../src/HomeBlaze/HomeBlaze/Data/Docs/plans/registry-attribute-migration.md))
- Discovers methods via `"operation"` and `"query"` registry attributes

### Usage

```csharp
// Generic usage (explicit [Path("mcp", ...)] attributes)
services.AddMcpServer(subject);

// HomeBlaze usage — one-liner with sensible defaults
services.AddHomeBlazeMcpServer(subject);
```

### Extension Points

```csharp
public interface IMcpMetadataProvider
{
    /// <summary>
    /// Enriches subject metadata (title, description, icon, etc.)
    /// </summary>
    void EnrichSubjectMetadata(RegisteredSubject subject, McpSubjectMetadata metadata);

    /// <summary>
    /// Discovers methods to expose on a subject.
    /// </summary>
    IEnumerable<McpMethodInfo> GetMethods(RegisteredSubject subject);

    /// <summary>
    /// Enriches property metadata.
    /// </summary>
    void EnrichPropertyMetadata(RegisteredSubjectProperty property, McpPropertyMetadata metadata);

    /// <summary>
    /// Returns known interfaces with metadata.
    /// </summary>
    IEnumerable<McpTypeInfo> GetKnownTypes() => [];
}
```

## MCP Tools

| Tool | Parameters | Description |
|------|------------|-------------|
| `query` | `path?`, `depth?`, `includeProperties?`, `filter?` | Browse/query subject tree |
| `get_property` | `path` | Read property value |
| `set_property` | `path`, `value` | Write property value |
| `list_types` | none | List available interfaces |
| `list_methods` | `path` | List methods on subject |
| `invoke_method` | `path`, `method`, `arguments?` | Call a method |

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
      "$methods": ["Calibrate"],
      "$hasChildren": false,
      "temperature": { "value": 21.5, "unit": "DegreeCelsius", "isWritable": false },
      "humidity": { "value": 45, "unit": "Percent", "isWritable": false }
    },
    "bedroom": {
      "$type": "Bedroom",
      "$interfaces": ["HomeBlaze.Abstractions.Sensors.ITemperatureSensor"],
      "$title": "Bedroom",
      "$hasChildren": true,
      "temperature": { "value": 19.2, "unit": "DegreeCelsius", "isWritable": false },
      "thermostat": {
        "$type": "Thermostat",
        "$interfaces": [
          "HomeBlaze.Abstractions.Sensors.ITemperatureSensor",
          "HomeBlaze.Abstractions.Devices.IControllable"
        ],
        "$methods": ["SetTarget", "TurnOff"],
        "$hasChildren": false,
        "currentTemperature": { "value": 19.2, "isWritable": false },
        "targetTemperature": { "value": 20.0, "isWritable": true }
      }
    }
  },
  "truncated": false,
  "subjectCount": 3
}
```

**Response conventions:**
- `$` prefix for metadata (`$type`, `$interfaces`, `$title`, `$icon`, `$methods`, `$hasChildren`)
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

### `list_methods` / `invoke_method` Tools

**list_methods:**
```json
// Request
{ "path": "/home/thermostat" }

// Response
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
    }
  ]
}
```

**invoke_method:**
```json
// Request
{ "path": "/home/thermostat", "method": "SetTarget", "arguments": { "temperature": 23 } }

// Response
{ "success": true }
```

## Project Structure

### Namotion.Interceptor.Mcp

```
Namotion.Interceptor.Mcp/
├── McpSubjectServer.cs
├── McpServerConfiguration.cs
├── InterceptorSubjectContextExtensions.cs
├── Abstractions/
│   ├── IMcpMetadataProvider.cs
│   ├── McpTypeInfo.cs
│   ├── McpSubjectMetadata.cs
│   └── McpPropertyMetadata.cs
└── Tools/
    ├── QueryTool.cs
    ├── GetPropertyTool.cs
    ├── SetPropertyTool.cs
    ├── ListTypesTool.cs
    ├── ListMethodsTool.cs
    └── InvokeMethodTool.cs
```

## Safety Limits

Configurable limits to prevent overwhelming responses:
- Max depth (default: 10)
- Max subjects in response (default: 100)
- Truncation indicator when limit is hit

## Access Control

MCP exposes `set_property` and `invoke_method`, so unrestricted access is unsafe even for development. Minimal access control before shipping:
- Connection-level allow/deny or read-only mode
- Coordinate with [#137](https://github.com/RicoSuter/Namotion.Interceptor/pull/137) (authorization) for full graph-level access control

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| On-demand only | No push notifications or change tracking | AI queries when needed |
| Interfaces over types | `list_types` returns capability interfaces, not concrete implementations | More stable, plugin-independent |
| Full interface names | Use full namespace.interface names | Avoid collisions, support plugins |
| Extension points | `IMcpMetadataProvider` for enrichment | HomeBlaze and plugins enhance metadata without core dependencies |
| Property discovery | Via `RegisteredSubjectProperty` | Use existing registry infrastructure, not raw reflection |
| Tool reuse | Tool implementations available as `AIFunction` for in-process use | Built-in agents use the same tools without MCP protocol overhead |

## Dependencies

- `Namotion.Interceptor.Registry`: subject and property discovery
- `ModelContextProtocol` (v1.1.0): MCP server SDK
- `Microsoft.Extensions.AI.Abstractions`: `AIFunction` for in-process tool reuse
- [Registry Attribute Migration](../../src/HomeBlaze/HomeBlaze/Data/Docs/plans/registry-attribute-migration.md): HomeBlaze metadata provider reads from registry attributes
- [#137](https://github.com/RicoSuter/Namotion.Interceptor/pull/137) (Authorization): access control before exposing write tools
