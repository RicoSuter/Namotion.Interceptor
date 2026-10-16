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

The MCP server is exposed as an HTTP endpoint. AI agents (Claude Desktop, custom MCP clients) connect to `/mcp` using the MCP Streamable HTTP transport. The legacy SSE transport is disabled by default and cannot be enabled together with `Stateless = true`, so clients must use Streamable HTTP.

## Output Format

The `browse` and `search` tools support a `format` parameter:

| Format | Default | Description |
|--------|---------|-------------|
| `text` | Yes | Purpose-built indented format optimized for LLM comprehension. One subject per line, one property per line, tree structure via indentation. Best for navigating and understanding the subject tree. |
| `json` | No | Pretty-printed JSON using typed DTOs with polymorphic property serialization. Use when exact structured data is needed for processing. |

### Text Format

Every text response starts with a one-line legend, followed by subjects in a compact indented format:

```
# path [Type] "title" $key=value | prop: value | type | @attr: value | Collection/ (Nx Type)
# Use get_property for exact values or browse with format=json for structured data.

/Demo [MyApp.VirtualFolder] "Demo" $icon=Folder
  /Demo/MyDevice [MyApp.Device] "My Device"
    Temperature: 23.5 | number, writable
      @Unit: °C
    Sensors/ (3x Sensor)
[2 subjects]
```

Key conventions:
- **Subject line:** `path [FullyQualifiedType] "title" $enrichment=value`
- **Property:** `name: value | type` or `name: value | type, writable`
- **Attribute:** `@name: value` (scalar) or `@name: {json}` (complex object)
- **Collapsed collection:** `Name/ (Nx ItemType)` or `Name/ (N children)`
- **Collapsed child subject:** `Name/ (ItemType)` for a single subject property beyond the depth limit (omitted when the property is null)
- **Footer:** `[N subjects]` or `[N subjects, truncated]`
- Special values: `null` for null, `""` for empty strings, `...` for truncated strings (>100 chars)
- Collection values (arrays, lists, dictionaries of plain values) render as compact JSON, such as `[{"Title":"Radio","IsContainer":false}]`. Whole items are shown up to about 500 characters and the rest as `... +N more`; `get_property` returns the full value. Byte arrays render as their length, such as `<16 bytes>`.

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

Properties marked with `[InlinePaths]` flatten dictionary keys into the path (e.g., `Demo/MyMotor` instead of `Demo/Children[MyMotor]`). See [Path Syntax](registry.md#path-syntax) for how keys are written and read, including keys that need the bracket form. A path that does not resolve is answered with `Path not found: <path>`, or with `Invalid path syntax: <reason>` when it is malformed.

### `browse`

Browse the subject tree starting at a path. Use `text` format (default) for overview and navigation, `json` for exact structured data.

**Text format (default):**

```
# path [Type] "title" $key=value | prop: value | type | @attr: value | Collection/ (Nx Type)
# Use get_property for exact values or browse with format=json for structured data.

/Demo [MyApp.VirtualFolder] "Demo"
  /Demo/MyDevice [MyApp.Device] "My Device"
    Temperature: 23.5 | number, writable
    Sensors/ (3x Sensor)
    Controller/ (Controller)
[2 subjects]
```

**JSON format** (`format=json`):

```json
{
  "result": {
    "$path": "Demo",
    "$type": "MyApp.VirtualFolder",
    "$title": "Demo",
    "properties": {
      "Children": {
        "kind": "dictionary",
        "children": {
          "MyDevice": {
            "$path": "Demo/MyDevice",
            "$type": "MyApp.Device",
            "properties": {
              "Temperature": { "kind": "value", "value": 23.5, "type": "number" },
              "Sensors": { "kind": "collection", "count": 3, "itemType": "Sensor", "isCollapsed": true },
              "Controller": { "kind": "object", "child": null, "isCollapsed": true, "itemType": "Controller" }
            }
          }
        }
      }
    }
  },
  "subjectCount": 1,
  "truncated": false
}
```

| Parameter | Default | Description |
|-----------|---------|-------------|
| `format` | `text` | Output format: `text` for LLM-readable overview, `json` for exact structured data |
| `path` | root | Starting path (e.g., `Folder/Device`) |
| `depth` | 1 | Max traversal depth (0 = properties only) |
| `includeProperties` | false | Include property values |
| `includeAttributes` | false | Include registry attributes on properties |
| `includeMethods` | false | Include `$methods` in subject nodes |
| `includeInterfaces` | false | Include `$interfaces` in subject nodes |
| `properties` | (all) | Return only these properties, e.g. `["IsConnected", "BatteryLevel"]`; implies `includeProperties` |
| `excludeTypes` | (none) | Exclude subjects matching these type/interface names |
| `maxSubjects` | server limit | Maximum subjects to return |

### `search`

Search across all subjects. Use `text` format (default) for overview, `json` for exact structured data.

**Text format (default):**

```
# path [Type] "title" $key=value | prop: value | type | @attr: value | Collection/ (Nx Type)
# Use get_property for exact values or browse with format=json for structured data.

/Demo/MyDevice [MyApp.Device] "My Device"
  Temperature: 23.5 | number, writable

[1 subject]
```

**JSON format** (`format=json`):

```json
{
  "results": {
    "Demo/MyDevice": {
      "$path": "Demo/MyDevice",
      "$type": "MyApp.Device",
      "$title": "My Device",
      "properties": {
        "Temperature": { "kind": "value", "value": 23.5, "type": "number" }
      }
    }
  },
  "subjectCount": 1,
  "truncated": false
}
```

| Parameter | Default | Description |
|-----------|---------|-------------|
| `format` | `text` | Output format: `text` for LLM-readable overview, `json` for exact structured data |
| `query` | (none) | Filter by path and enrichment values (e.g. `$title`, `$icon`); does not match types, interfaces, or property names |
| `types` | all | Filter subjects by type/interface full names |
| `path` | (none) | Scope search to a subtree path prefix |
| `where` | (none) | Filter by property values, see [Filtering by value](#filtering-by-value) |
| `properties` | (all) | Return only these properties, e.g. `["IsConnected", "BatteryLevel"]`; implies `includeProperties` |
| `includeProperties` | false | Include property values |
| `includeAttributes` | false | Include registry attributes on properties |
| `includeMethods` | false | Include `$methods` in subject nodes |
| `includeInterfaces` | false | Include `$interfaces` in subject nodes |
| `excludeTypes` | (none) | Exclude subjects matching these type/interface names |
| `maxSubjects` | server limit | Maximum subjects to return |

#### Filtering by value

`where` takes one C#-style expression over the subject's properties:

```text
IsConnected == false || BatteryLevel < 0.2 && Status != "Running"
```

- Comparisons: `==`, `!=`, `<`, `<=`, `>`, `>=` between a property name and a number, `true`, `false`, `null` or a string in double or single quotes. A bare word on the right, as in `Status != Running`, is read as a string.
- Combine comparisons with `&&` (or `and`) and `||` (or `or`); `&&` binds tighter, and parentheses group.
- Numbers compare numerically whatever their CLR type. Enums compare by name and strings case-insensitively for `==` and `!=`; strings order by ordinal. `DateTime` and `DateTimeOffset` values compare against an ISO 8601 string, a string without an offset read as UTC.
- A subject without the property, with a null value compared to a non-null literal, with a NaN value, or with a value of an incompatible type does not match, for every operator including `!=`. `== null` and `!= null` test whether the value is null; the other operators never match `null`. Properties holding subjects and attributes never match.
- Only properties the path provider exposes can be filtered on.
- An expression may be at most 1000 characters long and nest parentheses at most 32 levels deep.
- An invalid expression returns an error naming the zero-based position, for example `where: use '==' for equality at position 12`.

`browse` has no `where`; to filter a subtree, use `search` with `path` and `where`. Combine `where` with `properties` to return only the values the question needs.

### `get_property`

Read a property value by path. Returns the value, JSON schema type, and optional writeability flag.

### `set_property`

Write a property value by path. Blocked when `IsReadOnly` is true. The value is converted by `McpValueConverter`, which a custom tool can use for its own arguments: `TimeSpan`, `TimeOnly`, `DateOnly`, `DateTime`, `DateTimeOffset`, `Guid`, `Uri` and enums from their string form, enums by name (ignoring case) or by a defined number, and other values also from a string holding their JSON, such as `"0.5"`. A value that cannot be converted returns an error and leaves the property unchanged.

An application can refuse writes to some properties with `PropertyWriteFilter` on the configuration: it receives the property and returns null to allow the write or the reason it is refused, for example to keep a client from changing properties that hold settings. `set_property` then returns the error `Property is not writable: <path>. <reason>` and writes nothing. A refused property is not reported as writable by `get_property`, `browse` and `search`. The filter may be called concurrently.

### `list_types`

List available types from registered type providers. Interface types include property and method schemas; concrete types list their implemented interfaces.

| Parameter | Default | Description |
|-----------|---------|-------------|
| `kind` | `all` | Filter by kind: `interfaces`, `concrete`, or `all` |
| `type` | (none) | Search type names (case-insensitive contains match) |

Method parameters are listed as `McpMethodParameter` objects: `name` and JSON Schema `type`, plus the `format` of a string value: the standard JSON Schema name where one matches (`date-time`, `date`, `uuid`, `uri`), otherwise a short label: `hh:mm:ss` for `TimeSpan` (accepted as `[-][d.]hh:mm:ss[.fffffff]`) `HH:mm:ss` for `TimeOnly` (accepted as `HH:mm:ss[.fffffff]`) and `base64` for a byte array. `TimeSpan` is not `duration`, which means ISO 8601 (`PT1M30S`) and is not read by System.Text.Json. A parameter also has `enum` with the allowed names of an enum, `nullable`, and an optional `description`. `list_types` leaves `description` empty. A custom tool that lists methods can build the same shape with `McpMethodParameter.Create` and put a host-specific hint, such as a unit, into `description`.

## Configuration

```csharp
var config = new McpServerConfiguration
{
    // Required: property filtering and path resolution
    PathProvider = new DefaultPathProvider(),

    // Optional: subject-level metadata enrichment
    SubjectEnrichers = { new MySubjectEnricher() },

    // Optional: type discovery for list_types
    TypeProviders = { new MyTypeProvider() },

    // Optional: additional tools
    ToolProviders = { new MyToolProvider() },

    // Optional: text appended to a tool's description, keyed by tool name
    ToolDescriptionAdditions = { ["browse"] = "Subjects of type Document hold the manuals." },

    // Optional: refuse writes to some properties; return null to allow, else the reason for the client
    PropertyWriteFilter = property => property.Name == "Password" ? "Passwords cannot be changed." : null,

    // Safety limits
    MaxDepth = 10,
    MaxSubjectsPerResponse = 500,
    IsReadOnly = true
};
```

`ToolDescriptionAdditions` describes conventions of the application to the client without replacing the generic description of a tool, for example which subjects hold its documentation. Each text is appended after a space; names of tools that are not offered are ignored.

### Access Control

| `IsReadOnly` | `set_property` |
|--------------|----------------|
| `true` | Blocked |
| `false` | Allowed |

Custom tools provided via `IMcpToolProvider` can inspect `IsReadOnly` on the configuration and apply the same convention to their own mutating operations.

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
            Handler = async (input, ct) => new { result = "hello" },
            Annotations = McpToolAnnotations.ReadOnly
        };
    }
}
```

Tools are transport-agnostic `McpToolInfo` instances registered via `WithSubjectRegistryTools`.

`Annotations` carries the MCP tool annotations (`readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`) that clients may use to decide which calls need the user's approval. They are hints, not enforcement. `McpToolAnnotations.ReadOnly` marks a tool that only reads and `McpToolAnnotations.Destructive` one that can change state; both set `openWorldHint` to false. The core tools carry these: `set_property` is destructive, the other core tools are read-only.

## Using the Tools In-Process

The tools do not depend on the MCP transport, so an application can hand them to its own AI agent directly or serve a selection of them on another route.

```csharp
// The core tools and those of the tool providers, with ToolDescriptionAdditions applied
var tools = new McpToolFactory(() => rootSubject, configuration).CreateTools();

// Function calling with Microsoft.Extensions.AI
var options = new ChatOptions { Tools = [.. tools.Select(tool => tool.ToAIFunction())] };
var client = new FunctionInvokingChatClient(innerChatClient);
var response = await client.GetResponseAsync(messages, options, cancellationToken);
```

`ToAIFunction()` wraps a tool as an `AIFunction` with the tool's name, description and input schema; invoking it calls the tool's handler with the arguments as a JSON object.

`McpToolDispatcher` serves any list of tools through the MCP list and call requests, which `WithSubjectRegistryTools` uses as well. It lets a custom route offer a different tool list, for example only the tools one client may use, set in a stateless transport's `ConfigureSessionOptions` on `options.Handlers`:

```csharp
var handlers = options.Handlers;
handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(McpToolDispatcher.ListTools(tools));
handlers.CallToolHandler = (request, cancellationToken) =>
    McpToolDispatcher.CallToolAsync(tools, request.Params, logger, cancellationToken);
```

`ListTools` maps the annotations of each tool. `CallToolAsync` returns a string result as text and any other result as indented JSON that keeps non-ASCII characters and `<>&'` unescaped, and turns an unknown tool or a throwing handler into an error result. A call whose cancellation token is cancelled throws `OperationCanceledException` instead.

To change one member of a tool, for example to wrap its handler with a budget or logging, copy it with the copy constructor, which keeps all other members including the annotations:

```csharp
var counted = new McpToolInfo(tool)
{
    Handler = async (input, cancellationToken) =>
    {
        Interlocked.Increment(ref callCount);
        return await tool.Handler(input, cancellationToken);
    }
};
```

## Connecting Claude Desktop (Local Development)

Claude Desktop connects to local servers through `mcp-remote`, which bridges its stdio transport to the Streamable HTTP endpoint. With a local development HTTPS certificate, TLS validation must be relaxed as shown below.

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

`NODE_TLS_REJECT_UNAUTHORIZED=0` disables TLS certificate verification for the proxy process only. It is required because Node.js does not trust the ASP.NET Core development certificate by default. This is scoped to the `mcp-remote` process and only affects connections to your local server.

Restart Claude Desktop after editing the config. The MCP tools should appear in the tools menu.
