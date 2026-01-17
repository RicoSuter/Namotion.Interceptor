# MCP Server Design

Expose the registry and registered subjects via MCP (Model Context Protocol) server for AI consumption.

## Overview

Create `Namotion.Interceptor.Mcp` package that exposes the subject registry via MCP, enabling AI assistants and autonomous agents to browse, read, write, and invoke methods on the object graph.

## Use Cases

1. **AI Assistant for Monitoring/Control** - An AI that can browse, read, and control the object graph (e.g., home automation, industrial monitoring)
2. **Autonomous Agent Integration** - Enable AI agents to autonomously interact with and manipulate subjects based on goals
3. **Developer Assistance** - Natural language debugging, live system exploration, documentation on demand

## Architecture

### Package Structure

**Namotion.Interceptor.Mcp** (core package):
- Uses `IPathProvider` to determine which properties are exposed
- Default: `AttributeBasedPathProvider("mcp")` - explicit `[Path("mcp", "...")]` opt-in
- Uses `IMcpMetadataProvider` for extensible metadata

**HomeBlaze.Mcp** (HomeBlaze integration):
- Provides `StateAttributePathProvider` - includes `[State]` properties by default
- Provides `HomeBlazeMcpMetadataProvider` - extracts title, icon, unit, etc.
- Discovers methods via `[Operation]` and `[Query]` attributes

### Usage

```csharp
// Generic usage (explicit [Path("mcp", ...)] attributes)
services.AddMcpServer(subject);

// HomeBlaze usage - one-liner with sensible defaults
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

**Response key points:**
- `$` prefix for metadata (type, interfaces, title, icon, methods, hasChildren)
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
    },
    {
      "name": "MyPlugin.Devices.IIrrigationController",
      "description": "Controls irrigation system"
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

### HomeBlaze.Mcp

```
HomeBlaze.Mcp/
├── HomeBlazeMcpMetadataProvider.cs
├── StateAttributePathProvider.cs
└── ServiceCollectionExtensions.cs
```

## HomeBlaze Integration

### StateAttributePathProvider

Uses typed `StateAttribute` (not string-based reflection):

```csharp
public class StateAttributePathProvider : IPathProvider
{
    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return property.ReflectionAttributes.OfType<StateAttribute>().Any();
    }

    public string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        var stateAttribute = property.ReflectionAttributes
            .OfType<StateAttribute>()
            .FirstOrDefault();
        return stateAttribute?.Name ?? property.Name;
    }
}
```

### HomeBlazeMcpMetadataProvider

```csharp
public class HomeBlazeMcpMetadataProvider : IMcpMetadataProvider
{
    private readonly SubjectTypeRegistry _typeRegistry;

    public void EnrichSubjectMetadata(RegisteredSubject subject, McpSubjectMetadata metadata)
    {
        if (subject.Subject is ITitleProvider titleProvider)
            metadata.Title = titleProvider.Title;

        if (subject.Subject is IIconProvider iconProvider)
        {
            metadata.Icon = iconProvider.IconName;
            metadata.IconColor = iconProvider.IconColor;
        }
    }

    public void EnrichPropertyMetadata(RegisteredSubjectProperty property, McpPropertyMetadata metadata)
    {
        var stateAttribute = property.ReflectionAttributes
            .OfType<StateAttribute>()
            .FirstOrDefault();

        if (stateAttribute is not null)
        {
            metadata.Unit = stateAttribute.Unit.ToString();
            metadata.Position = stateAttribute.Position;
            metadata.IsCumulative = stateAttribute.IsCumulative;
        }
    }

    public IEnumerable<McpMethodInfo> GetMethods(RegisteredSubject subject)
    {
        // Use RegisteredSubjectMethodExtensions with typed OperationAttribute/QueryAttribute
    }

    public IEnumerable<McpTypeInfo> GetKnownTypes()
    {
        var interfaces = new HashSet<Type>();

        foreach (var type in _typeRegistry.RegisteredTypes)
        {
            foreach (var interfaceType in type.GetInterfaces())
            {
                if (interfaceType.IsPublic)
                    interfaces.Add(interfaceType);
            }
        }

        foreach (var interfaceType in interfaces)
        {
            yield return new McpTypeInfo
            {
                Name = interfaceType.FullName!,
                Description = GetInterfaceDescription(interfaceType)
            };
        }
    }
}
```

### Registration

```csharp
// HomeBlaze.Mcp extension method
public static IServiceCollection AddHomeBlazeMcpServer(
    this IServiceCollection services,
    IInterceptorSubject subject,
    Action<McpServerConfiguration>? configure = null)
{
    return services.AddMcpServer(subject, configuration =>
    {
        configuration.PathProvider = new StateAttributePathProvider();
        configuration.AddMetadataProvider<HomeBlazeMcpMetadataProvider>();
        configure?.Invoke(configuration);
    });
}
```

## Safety Limits

Configurable limits to prevent overwhelming responses:
- Max depth (default: 10)
- Max subjects in response (default: 100)
- Truncation indicator when limit is hit

## Documentation Deliverables

| File | Description |
|------|-------------|
| `docs/mcp.md` | Core Namotion.Interceptor.Mcp documentation |
| `src/HomeBlaze/HomeBlaze.Host/Data/Docs/mcp.md` | HomeBlaze-specific documentation |

## Design Decisions

1. **On-demand only** - No push notifications or change tracking. AI queries when needed.
2. **Interfaces over types** - `list_types` returns capability interfaces, not concrete implementations.
3. **Full interface names** - Use full namespace.interface names to avoid collisions and support plugins.
4. **Extension points** - `IMcpMetadataProvider` allows HomeBlaze and plugins to enhance metadata without core package dependencies.
5. **Property discovery via RegisteredSubjectProperty** - Use existing registry infrastructure, not raw reflection.
