# MCP Server (Experimental)

> **Note:** This package is experimental and its API may change between versions.

Namotion.Interceptor.Mcp exposes the subject registry via [MCP (Model Context Protocol)](https://modelcontextprotocol.io), enabling AI agents to browse, search, and interact with the object graph.

## Installation

```xml
<PackageReference Include="Namotion.Interceptor.Mcp" />
```

## Quick Start

```csharp
// 1. Configure MCP server with HTTP transport
services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithSubjectRegistryTools(
        sp => resolveRootSubject(sp),
        sp => new McpServerConfiguration
        {
            PathProvider = new DefaultPathProvider()
        });

// 2. Map the MCP endpoint
app.MapMcp("/mcp");
```

The MCP server is exposed as an HTTP endpoint. AI agents (Claude Desktop, custom MCP clients) connect to `/mcp` using the MCP protocol over HTTP with Server-Sent Events (SSE).

## Tools

The MCP server provides 5 core tools:

| Tool | Description |
|------|-------------|
| `browse` | Browse the subject tree with depth control and property inclusion |
| `search` | Search across all subjects by text and/or type names |
| `get_property` | Read a property value by path (e.g., `Folder/Device/Temperature`) |
| `set_property` | Write a property value by path (e.g., `Folder/Device/TargetSpeed`). Blocked when `IsReadOnly` |
| `list_types` | List available types with interface property and method schemas |

### Path Format

The path format depends on the `PathProvider` configuration. The separator and index characters are configurable:

```
Folder/SubFolder/Device          (slash path notation)
Pins[0]                          (collection index)
Items[myKey]                     (dictionary key)
Folder/SubFolder/Device/Status   (property path)
```

Properties marked with `[InlinePaths]` flatten dictionary keys into the path (e.g., `Demo/MyMotor` instead of `Demo/Children[MyMotor]`).

### `browse`

Browse the subject tree starting at a path. The `result` is the browsed subject node with its children inline:

- **Subject nodes** include `$path` for use with `get_property`/`set_property`
- **Properties with children** at depth 0 show `$count` instead of expanding; homogeneous collections also include `$itemType`
- **Scalar properties** are shown as `{ "value": ... }` when `includeProperties` is true

```json
{
  "result": {
    "$path": "Demo",
    "Children": {
      "MyDevice": {
        "$path": "Demo/MyDevice",
        "$type": "MyApp.Device",
        "Temperature": { "value": 23.5 },
        "Sensors": { "$count": 3, "$itemType": "Sensor" }
      }
    }
  },
  "truncated": false,
  "subjectCount": 1
}
```

| Parameter | Default | Description |
|-----------|---------|-------------|
| `path` | root | Starting path (e.g., `Folder/Device`) |
| `depth` | 1 | Max traversal depth (0 = properties only) |
| `includeProperties` | false | Include property values |
| `includeAttributes` | false | Include registry attributes on properties |
| `includeMethods` | false | Include `$methods` in subject nodes |
| `includeInterfaces` | false | Include `$interfaces` in subject nodes |
| `excludeTypes` | (none) | Exclude subjects matching these type/interface names |
| `maxSubjects` | server limit | Maximum subjects to return |

### `search`

Search across all subjects. Returns `results` as a flat dictionary keyed by path:

```json
{
  "results": {
    "Demo/MyDevice": {
      "$path": "Demo/MyDevice",
      "$title": "My Device",
      "Temperature": { "value": 23.5 }
    }
  },
  "truncated": false,
  "subjectCount": 1
}
```

| Parameter | Default | Description |
|-----------|---------|-------------|
| `text` | (none) | Filter by text (matches title and path, case-insensitive) |
| `types` | all | Filter subjects by type/interface full names |
| `path` | (none) | Scope search to a subtree path prefix |
| `includeProperties` | false | Include property values |
| `includeAttributes` | false | Include registry attributes on properties |
| `includeMethods` | false | Include `$methods` in subject nodes |
| `includeInterfaces` | false | Include `$interfaces` in subject nodes |
| `excludeTypes` | (none) | Exclude subjects matching these type/interface names |
| `maxSubjects` | server limit | Maximum subjects to return |

### `get_property`

Read a property value by path. Returns the value, JSON schema type, and optional writeability flag.

### `set_property`

Write a property value by path. Blocked when `IsReadOnly` is true.

### `list_types`

List available types from registered type providers. Interface types include property and method schemas; concrete types list their implemented interfaces.

| Parameter | Default | Description |
|-----------|---------|-------------|
| `kind` | `all` | Filter by kind: `interfaces`, `concrete`, or `all` |
| `type` | (none) | Search type names (case-insensitive contains match) |

## Configuration

```csharp
var config = new McpServerConfiguration
{
    // Required: property filtering and path resolution
    PathProvider = new DefaultPathProvider(),

    // Optional: subject-level metadata enrichment
    SubjectEnrichers = { new MySubjectEnricher() },

    // Optional: type discovery for list_types
    TypeProviders = { new SubjectAbstractionsAssemblyTypeProvider() },

    // Optional: additional tools
    ToolProviders = { new MyToolProvider() },

    // Safety limits
    MaxDepth = 10,
    MaxSubjectsPerResponse = 500,
    IsReadOnly = true
};
```

### Access Control

| `IsReadOnly` | `set_property` | `invoke_method` (Browse) | `invoke_method` (Operation) |
|--------------|---------------|------------------------|-----------------------------|
| `true` | Blocked | Allowed | Blocked |
| `false` | Allowed | Allowed | Allowed |

## Extension Points

### IMcpSubjectEnricher

Add subject-level metadata (prefixed with `$`) to browse responses:

```csharp
public class MyEnricher : IMcpSubjectEnricher
{
    public IDictionary<string, object?> GetSubjectEnrichments(RegisteredSubject subject)
    {
        return new Dictionary<string, object?>
        {
            ["$customField"] = "value"
        };
    }
}
```

### IMcpTypeProvider

Provide types for the `list_types` tool:

```csharp
public class MyTypeProvider : IMcpTypeProvider
{
    public IEnumerable<McpTypeInfo> GetTypes()
    {
        yield return new McpTypeInfo(
            "MyNamespace.IMyInterface",
            "Description",
            IsInterface: true,
            Type: typeof(IMyInterface));
    }
}
```

The built-in `SubjectAbstractionsAssemblyTypeProvider` returns all interfaces from assemblies marked with `[SubjectAbstractionsAssembly]`.

### IMcpToolProvider

Add custom tools:

```csharp
public class MyToolProvider : IMcpToolProvider
{
    public IEnumerable<McpToolInfo> GetTools()
    {
        yield return new McpToolInfo
        {
            Name = "my_tool",
            Description = "My custom tool",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            Handler = async (input, ct) => new { result = "hello" }
        };
    }
}
```

Tools are transport-agnostic `McpToolInfo` instances registered via `WithSubjectRegistryTools`.

## Connecting Claude Desktop (Local Development)

When running with a local development HTTPS certificate, Claude Desktop requires `mcp-remote` as a proxy since the MCP protocol runs over HTTP/SSE.

Add to `~/Library/Application Support/Claude/claude_desktop_config.json` (macOS):

```json
{
  "mcpServers": {
    "my-app": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "https://localhost:7298/mcp"],
      "env": {
        "NODE_TLS_REJECT_UNAUTHORIZED": "0"
      }
    }
  }
}
```

`NODE_TLS_REJECT_UNAUTHORIZED=0` disables TLS certificate verification for the proxy process only — required because Node.js does not trust the ASP.NET Core development certificate by default. This is scoped to the `mcp-remote` process and only affects connections to your local server.

Restart Claude Desktop after editing the config. The MCP tools should appear in the tools menu.
