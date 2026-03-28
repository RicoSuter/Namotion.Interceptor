# MCP Server

Namotion.Interceptor.Mcp exposes the subject registry via [MCP (Model Context Protocol)](https://modelcontextprotocol.io), enabling AI agents to browse, query, and interact with the object graph.

## Installation

```xml
<PackageReference Include="Namotion.Interceptor.Mcp" Version="0.1.0" />
```

## Quick Start

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry();

var root = new MyRootSubject(context);

// Configure MCP server
services.AddMcpSubjectServer(root, new McpServerConfiguration
{
    PathProvider = new DefaultPathProvider()
});
```

## Tools

The MCP server provides 4 core tools:

| Tool | Description |
|------|-------------|
| `query` | Browse the subject tree with depth control, property inclusion, and type filtering |
| `get_property` | Read a property value with type and registry attributes |
| `set_property` | Write a property value (blocked when `IsReadOnly`) |
| `list_types` | List available types from registered type providers |

### Path Format

Paths use dot notation with bracket indexing:

```
root.livingRoom.temperature
root.sensors[0].value
root.devices[myDevice].status
```

### Query Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `path` | root | Starting path |
| `depth` | 1 | Max traversal depth (0 = properties only, no children) |
| `includeProperties` | false | Include property values |
| `includeAttributes` | false | Include registry attributes on properties |
| `types` | all | Filter subjects by type/interface full names |

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
    MaxSubjectsPerResponse = 100,
    IsReadOnly = true
};
```

### Access Control

| `IsReadOnly` | `set_property` | `invoke_method` (Query) | `invoke_method` (Operation) |
|--------------|---------------|------------------------|-----------------------------|
| `true` | Blocked | Allowed | Blocked |
| `false` | Allowed | Allowed | Allowed |

## Extension Points

### IMcpSubjectEnricher

Add subject-level metadata (prefixed with `$`) to query responses:

```csharp
public class MyEnricher : IMcpSubjectEnricher
{
    public void EnrichSubject(RegisteredSubject subject, IDictionary<string, object?> metadata)
    {
        metadata["$customField"] = "value";
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
        yield return new McpTypeInfo("MyNamespace.IMyInterface", "Description", IsInterface: true);
    }
}
```

The built-in `SubjectAbstractionsAssemblyTypeProvider` returns all interfaces from assemblies marked with `[SubjectAbstractionsAssembly]`.

### IMcpToolProvider

Add custom tools:

```csharp
public class MyToolProvider : IMcpToolProvider
{
    public IEnumerable<McpToolDescriptor> GetTools()
    {
        yield return new McpToolDescriptor
        {
            Name = "my_tool",
            Description = "My custom tool",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            Handler = async (input, ct) => new { result = "hello" }
        };
    }
}
```

Tools are transport-agnostic `McpToolDescriptor` instances. They can be wrapped as MCP tools for external agents or `AIFunction` objects for in-process use.
