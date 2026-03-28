---
title: HomeBlaze MCP Extensions
navTitle: MCP Extensions
---

# HomeBlaze MCP Extensions

**Prerequisites:**
- [MCP Server](../../../../docs/mcp.md) — core `Namotion.Interceptor.Mcp` package

## Overview

HomeBlaze-specific extensions for the MCP server, configured via the core `McpServerConfiguration`. These extensions enrich the generic interceptor MCP server with HomeBlaze domain metadata, type discovery, method tools, and property filtering.

All HomeBlaze MCP extensions live in `HomeBlaze.AI`:

```
HomeBlaze.AI → Namotion.Interceptor.Mcp → Namotion.Interceptor.Registry
             → HomeBlaze.Services
```

## Configuration

```csharp
services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithHomeBlazeMcpTools();
```

Internally, `WithHomeBlazeMcpTools()` resolves `RootManager`, `SubjectTypeRegistry`, and `IServiceProvider` from DI to build the `McpServerConfiguration` lazily on first MCP request.

## Implementations

### StateAttributePathProvider (IPathProvider)

Determines which properties are exposed via MCP. Extends `PathProviderBase` and reads the `"State"` registry attribute so all `[State]` properties are automatically visible. Also matches structural properties (CanContainSubjects) by name for navigation through children dictionaries.

### HomeBlazeMcpSubjectEnricher (IMcpSubjectEnricher)

Adds subject-level metadata to `query` responses:
- `$title` — from `ITitleProvider`
- `$icon` — from `IIconProvider`
- `$type` — concrete type full name (only if it's a known type from type providers)
- `$interfaces` — list of known interface full names the subject implements

Property-level metadata (units, position, etc.) is already available as registry attributes (`StateMetadata`) and included directly in `query` responses when `includeAttributes=true`.

### SubjectTypeRegistryTypeProvider (IMcpTypeProvider)

Returns concrete subject types from HomeBlaze's `SubjectTypeRegistry` (the `$type` discriminator → CLR type mappings used for JSON polymorphism). Combined with the core `SubjectAbstractionsAssemblyTypeProvider` (which returns interfaces from `[SubjectAbstractionsAssembly]` assemblies), the `list_types` tool provides both abstractions and concrete types.

### HomeBlazeMcpToolProvider (IMcpToolProvider)

Registers two additional tools. These are HomeBlaze-specific because method discovery uses `MethodMetadata` with `[Operation]`/`[Query]` registry attributes.

#### `list_methods` Tool

List operations and queries on a subject, discovered from `MethodMetadata` registry properties.

**Parameters:** `path` (required) — subject path (e.g., `Root.Servers.OpcUaServer`)

**Response:** Array of methods with `name`, `kind` (operation/query), and `parameters` (name + type for user-input parameters).

#### `invoke_method` Tool

Execute a method on a subject. When `IsReadOnly=true`, only methods with `MethodKind.Query` are allowed — operations are blocked. Resolves `[FromServices]` parameters via `IServiceProvider`.

**Parameters:** `path` (required), `method` (required), `parameters` (optional object)

## Access Control

| `IsReadOnly` | `set_property` | `invoke_method` (Query) | `invoke_method` (Operation) |
|--------------|---------------|------------------------|-----------------------------|
| `true` | Blocked | Allowed | Blocked |
| `false` | Allowed | Allowed | Allowed |

Full per-subject and per-agent authorization is a separate cross-cutting concern (graph-level authorization).

## Future Tools

| Tool | Parameters | Description | Depends on |
|------|-----------|-------------|------------|
| `get_property_history` | `path`, `from?`, `to?` | Query time-series data | Time-series history |
| `get_event_history` | `from?`, `to?`, `eventType?`, `sourcePath?` | Query persisted events | Event store |
| `get_command_history` | `from?`, `to?`, `commandType?`, `sourcePath?` | Query persisted commands | Event store |

## Dependencies

- `Namotion.Interceptor.Mcp`: `McpServerConfiguration`, `IMcpSubjectEnricher`, `IMcpTypeProvider`, `IMcpToolProvider`, `McpToolInfo`
- `HomeBlaze.Abstractions`: `ITitleProvider`, `IIconProvider`, `MethodMetadata`, `[Operation]`, `[Query]`, `KnownAttributes`, `StateMetadata`
- `HomeBlaze.Services`: `SubjectTypeRegistry`, `RootManager`, `RegisteredSubjectMethodExtensions`
