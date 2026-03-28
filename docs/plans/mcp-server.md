# MCP Server Plan

**Status: Planned**

Expose the subject registry via MCP (Model Context Protocol) server, enabling external AI agents to browse, query, and interact with the knowledge graph.

## Overview

Create `Namotion.Interceptor.Mcp` package that exposes the subject registry via MCP, enabling AI assistants and autonomous agents to browse, read, and write properties and discover types on the object graph.

Tool implementations are transport-agnostic: each tool is a `McpToolDescriptor` (metadata + plain function). Consumers wrap them as MCP tools (for external agents via the MCP protocol) or `AIFunction` objects (for in-process use by built-in agents). One implementation, any delivery mode.

## Configuration

The MCP server is configured via a single `McpServerConfiguration` object, following the same pattern as `OpcUaServerConfiguration`:

```csharp
public class McpServerConfiguration
{
    // Property filtering & path resolution (existing IPathProvider from Registry)
    public required IPathProvider PathProvider { get; init; }

    // Subject-level JSON enrichment for query responses
    public IList<IMcpSubjectEnricher> SubjectEnrichers { get; init; } = [];

    // Type discovery for list_types tool
    public IList<IMcpTypeProvider> TypeProviders { get; init; } = [];

    // Additional tools beyond the 4 core tools (e.g., list_methods, invoke_method)
    public IList<IMcpToolProvider> ToolProviders { get; init; } = [];

    // Safety limits
    public int MaxDepth { get; init; } = 10;
    public int MaxSubjectsPerResponse { get; init; } = 100;

    // Access control
    public bool IsReadOnly { get; init; } = true;
}
```

### Extension Point Interfaces

**`IPathProvider`** — reuses the existing interface from `Namotion.Interceptor.Registry.Paths`. Determines which properties are exposed and how they map to path segments. No MCP-specific path provider interface is needed.

**`IMcpSubjectEnricher`** — adds subject-level metadata fields (prefixed with `$`) to `query` responses. The core registry has property-level attributes but no subject-level metadata. This extension point lets HomeBlaze inject `$title`, `$icon`, `$type` without the core MCP package depending on HomeBlaze.

```csharp
public interface IMcpSubjectEnricher
{
    void EnrichSubject(RegisteredSubject subject,
                       IDictionary<string, object?> metadata);
}
```

**`IMcpTypeProvider`** — provides type information for the `list_types` tool. Multiple providers can coexist (interfaces from abstraction assemblies, concrete types from a type registry).

```csharp
public interface IMcpTypeProvider
{
    IEnumerable<McpTypeInfo> GetTypes();
}

public record McpTypeInfo(
    string Name,
    string? Description,
    bool IsInterface);
```

**`IMcpToolProvider`** — registers additional tools beyond the 4 core tools. Each tool is a `McpToolDescriptor` with metadata and a plain function. Consumers (MCP server, built-in agents) wrap these into their respective formats.

```csharp
public class McpToolDescriptor
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonElement InputSchema { get; init; }
    public required Func<JsonElement, CancellationToken, Task<object?>> Handler { get; init; }
}

public interface IMcpToolProvider
{
    IEnumerable<McpToolDescriptor> GetTools();
}
```

### Usage

```csharp
// Generic usage — HTTP transport with stateless mode
services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithSubjectServerTools(resolveRootSubject, new McpServerConfiguration
    {
        PathProvider = new CamelCasePathProvider()
    });

app.MapMcp("/mcp");

// HomeBlaze usage — all extensions configured in one place
services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithSubjectServerTools(resolveRootSubject, new McpServerConfiguration
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

app.MapMcp("/mcp");
```

## Path Format

Paths use dot notation with bracket indexing for collections and dictionaries:

```
root.livingRoom.temperature
root.sensors[0].value
root.devices[myDevice].status
```

This matches C# member access syntax and the existing `PathProviderBase` default separator. The path format is documented in tool descriptions so AI agents can construct paths correctly.

## Core Tools (Namotion.Interceptor.Mcp)

### `query` Tool

Browse the subject tree with optional filtering, property inclusion, and attribute inclusion.

**Parameters:**
```
path?: string              // Starting path (default: root)
depth?: number             // Max depth (default: 1, max: configurable)
includeProperties?: bool   // Include property values (default: false)
includeAttributes?: bool   // Include registry attributes on properties (default: false)
types?: string[]           // Filter subjects by type/interface full names (default: all)
```

**Depth semantics:**
- `depth=0` — subject with its own properties (if requested) but no child subjects
- `depth=1` — subject with immediate child subjects (default)
- `depth=N` — N levels of child subjects

**Response:**
```json
{
  "path": "root",
  "subjects": {
    "livingRoom": {
      "$title": "Living Room",
      "$icon": "Weekend",
      "$hasChildren": false,
      "temperature": {
        "value": 21.5,
        "attributes": {
          "State": { "name": "Temperature", "unit": "DegreeCelsius", "position": 1 }
        }
      }
    },
    "bedroom": {
      "$title": "Bedroom",
      "$hasChildren": true,
      "temperature": {
        "value": 19.2,
        "attributes": {
          "State": { "name": "Temperature", "unit": "DegreeCelsius", "position": 2 }
        }
      }
    }
  },
  "truncated": false,
  "subjectCount": 2
}
```

**Response conventions:**
- `$` prefix for subject-level metadata (`$title`, `$icon`, `$type`, `$hasChildren`, etc.) — added by `IMcpSubjectEnricher` implementations
- `$hasChildren` indicates if there are child subjects beyond current depth (computed by core)
- Property values appear when `includeProperties=true`
- Registry attributes appear nested under `attributes` when `includeAttributes=true`
- `truncated` flag when `MaxSubjectsPerResponse` limit is hit

### `list_types` Tool

Returns types from all registered `IMcpTypeProvider` instances. Each type indicates whether it is an interface (abstraction) or a concrete type.

**Response:**
```json
{
  "types": [
    {
      "name": "HomeBlaze.Abstractions.Sensors.ITemperatureSensor",
      "description": "Provides temperature readings",
      "isInterface": true
    },
    {
      "name": "HomeBlaze.Abstractions.Devices.ISwitchDevice",
      "description": "Can be turned on/off",
      "isInterface": true
    },
    {
      "name": "HomeBlaze.Samples.Motor",
      "description": null,
      "isInterface": false
    }
  ]
}
```

### `get_property` Tool

Read a single property value with its type and registry attributes.

**Request:**
```json
{ "path": "root.thermostat.temperature" }
```

**Response:**
```json
{
  "value": 21.5,
  "type": "decimal",
  "isWritable": false,
  "attributes": {
    "State": { "name": "Temperature", "unit": "DegreeCelsius", "position": 1 }
  }
}
```

### `set_property` Tool

Write a property value. Blocked when `IsReadOnly=true`. Values are deserialized to the target property's .NET type via `JsonSerializer.Deserialize(jsonValue, property.Type)`.

**Request:**
```json
{ "path": "root.thermostat.target", "value": 23.0 }
```

**Response:**
```json
{ "success": true, "previousValue": 22.0 }
```

## HomeBlaze Tools (via IMcpToolProvider)

Method-related tools (`list_methods`, `invoke_method`) are HomeBlaze-specific because method discovery uses `MethodMetadata` with `[Operation]`/`[Query]` registry attributes — a HomeBlaze convention, not a core interceptor concept.

These tools are registered via `HomeBlazeMcpToolProvider` implementing `IMcpToolProvider`.

### `list_methods` Tool

List operations and queries on a subject.

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

### `invoke_method` Tool

Execute a method on a subject. When `IsReadOnly=true`, only methods with `MethodKind.Query` are allowed — operations are blocked.

**Request:**
```json
{ "path": "root.thermostat", "method": "SetTarget", "arguments": { "temperature": 23 } }
```

**Response:**
```json
{ "success": true }
```

## Core Implementations

### SubjectAbstractionsAssemblyTypeProvider

Default `IMcpTypeProvider` in the core package. Scans loaded assemblies marked with `[SubjectAbstractionsAssembly]` and returns all interfaces declared in those assemblies.

```csharp
public class SubjectAbstractionsAssemblyTypeProvider : IMcpTypeProvider
{
    public IEnumerable<McpTypeInfo> GetTypes()
    {
        // Find assemblies with [SubjectAbstractionsAssembly]
        // Return all interfaces from those assemblies
        // IsInterface = true for all results
    }
}
```

Note: `SubjectAbstractionsAssemblyAttribute` is a core `Namotion.Interceptor` attribute (see [Dynamic Subject Proxying](../src/HomeBlaze/HomeBlaze/Data/Docs/plans/dynamic-subject-proxying.md)).

## HomeBlaze Implementations

### HomeBlazeMcpSubjectEnricher

Adds subject-level metadata to `query` responses:

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

### SubjectTypeRegistryTypeProvider

Returns concrete subject types from HomeBlaze's `SubjectTypeRegistry` (the `$type` discriminator → CLR type mappings):

```csharp
public class SubjectTypeRegistryTypeProvider : IMcpTypeProvider
{
    public IEnumerable<McpTypeInfo> GetTypes()
    {
        // Read from SubjectTypeRegistry
        // IsInterface = false for all results
    }
}
```

### HomeBlazeMcpToolProvider

Registers `list_methods` and `invoke_method` as additional tools:

```csharp
public class HomeBlazeMcpToolProvider : IMcpToolProvider
{
    public IEnumerable<McpToolDescriptor> GetTools()
    {
        yield return new McpToolDescriptor { Name = "list_methods", ... };
        yield return new McpToolDescriptor { Name = "invoke_method", ... };
    }
}
```

### StateAttributePathProvider

Determines which properties are exposed via MCP. Reads the `"State"` registry attribute so all `[State]` properties are automatically visible:

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

## Project Structure

```
Namotion.Interceptor.Mcp/
├── McpSubjectServer.cs
├── McpServerConfiguration.cs
├── McpToolDescriptor.cs
├── Abstractions/
│   ├── IMcpSubjectEnricher.cs
│   ├── IMcpTypeProvider.cs
│   ├── IMcpToolProvider.cs
│   └── McpTypeInfo.cs
├── Implementations/
│   └── SubjectAbstractionsAssemblyTypeProvider.cs
├── Tools/
│   ├── QueryTool.cs
│   ├── GetPropertyTool.cs
│   ├── SetPropertyTool.cs
│   └── ListTypesTool.cs
└── Extensions/
    └── InterceptorSubjectExtensions.cs
```

## Safety Limits

Configurable limits to prevent overwhelming responses:
- Max depth (default: 10)
- Max subjects in response (default: 100)
- Truncation indicator when limit is hit

## Access Control

| `IsReadOnly` | `set_property` | `invoke_method` (Query) | `invoke_method` (Operation) |
|--------------|---------------|------------------------|-----------------------------|
| `true` | Blocked | Allowed | Blocked |
| `false` | Allowed | Allowed | Allowed |

`IsReadOnly` defaults to `true`. Full per-subject and per-agent authorization is a separate cross-cutting concern (graph-level authorization) — the MCP server will check it the same way any other code path does once available.

## Error Handling

MCP uses JSON-RPC 2.0. Tool calls return `CallToolResult` which supports `isError: true` with content describing the error. Error scenarios:

- **Path not found** → error result with descriptive message
- **`set_property` when `IsReadOnly`** → error result: "Server is in read-only mode"
- **`set_property` on read-only property** → error result: "Property is not writable"
- **`invoke_method` operation when `IsReadOnly`** → error result: "Operations are not allowed in read-only mode"
- **`invoke_method` throws exception** → error result with exception message
- **Response truncated** → not an error; `truncated: true` flag in response

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Configuration pattern | Single `McpServerConfiguration` object | Follows OPC UA server pattern. One place to configure everything |
| Path provider | Reuse existing `IPathProvider` from Registry | No duplication. Same interface used by OPC UA and other consumers |
| Subject enrichment | `IMcpSubjectEnricher` list | Core can't know about `ITitleProvider`/`IIconProvider`. Extension point lets HomeBlaze inject subject-level metadata |
| Property metadata | Registry attributes included directly | `StateMetadata` (units, position) and other attributes are already in the registry. No enrichment layer needed |
| Type discovery | `IMcpTypeProvider` list | `list_types` needs domain-specific filtering. Multiple providers combine interfaces and concrete types |
| Tool extensibility | `IMcpToolProvider` with `McpToolDescriptor` | Transport-agnostic: metadata + plain function. Consumers wrap as MCP tools or `AIFunction` |
| Value conversion | `JsonSerializer.Deserialize(value, property.Type)` | Leverages existing System.Text.Json infrastructure. No custom converter needed |
| Path format | Dot notation (`.` separator, `[]` indexing) | Matches C# syntax and existing `PathProviderBase` defaults |
| Core tool scope | `query`, `get_property`, `set_property`, `list_types` | Only tools the core registry can support. Method tools are HomeBlaze-specific |
| On-demand only | No push notifications or change tracking | AI queries when needed. Push depends on MCP protocol evolution |
| Error handling | Standard MCP `CallToolResult` with `isError` | No custom error model. Follows JSON-RPC 2.0 conventions |

## Dependencies

- `Namotion.Interceptor.Registry`: subject and property discovery, `IPathProvider`
- `ModelContextProtocol`: MCP server SDK
- `ModelContextProtocol.AspNetCore`: HTTP transport with `MapMcp()` endpoint mapping
- `SubjectAbstractionsAssemblyAttribute`: from core `Namotion.Interceptor` (shared with dynamic proxying)
- Graph-level authorization: access control before exposing write tools (future)
