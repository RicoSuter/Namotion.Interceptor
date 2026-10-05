---
title: Configuration
navTitle: Configuration
position: 2
---

# Configuration

HomeBlaze reads its runtime configuration from the standard ASP.NET Core configuration sources:

1. `appsettings.json`
2. `appsettings.{Environment}.json` (e.g., `appsettings.Development.json`)
3. Environment variables
4. Command-line arguments

This page documents the HomeBlaze-specific settings. For managing subjects and the object graph, see [Subjects, Storage & Files](subjects.md).

---

## Logging

Standard ASP.NET Core logging configuration:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

Category-level overrides use the fully qualified type name (or prefix) as the key.

---

## AllowedHosts

Restricts which `Host` header values the server will accept. `*` allows any host.

```json
{
  "AllowedHosts": "*"
}
```

---

## PluginConfigurationPath

Path to the runtime plugin manifest (`Plugins.json`).

| Setting | Default |
|---------|---------|
| `PluginConfigurationPath` | `{AppContext.BaseDirectory}/Data/Plugins.json` |

Override example:

```json
{
  "PluginConfigurationPath": "/etc/homeblaze/plugins.json"
}
```

For plugin loading details (build-time vs. runtime, dependency resolution), see [Plugin System Design](../architecture/design/plugins.md).

---

## McpServer

Controls the built-in Model Context Protocol server that exposes the knowledge graph to AI agents.

```json
{
  "McpServer": {
    "Enabled": false,
    "ReadOnly": true
  }
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `McpServer:Enabled` | bool | `true` in Development, `false` otherwise | Registers the MCP server and maps the `/mcp` endpoint |
| `McpServer:ReadOnly` | bool | `true` | Restricts MCP tools to read-only operations; set to `false` to allow property writes and operation invocations |

### Example: Development override

`appsettings.Development.json` — enable the MCP server with write access for local testing:

```json
{
  "McpServer": {
    "Enabled": true,
    "ReadOnly": false
  }
}
```

### Example: Production with read-only access

`appsettings.json` — expose MCP in production but keep it read-only:

```json
{
  "McpServer": {
    "Enabled": true,
    "ReadOnly": true
  }
}
```

Enabling MCP in production exposes the graph to any client that can reach the `/mcp` endpoint. For the authorization story, see [Security Design](../architecture/design/security.md).

---

## Environment Variables

Any setting can be overridden by an environment variable using the ASP.NET Core `__` separator:

| Setting | Environment variable |
|---------|---------------------|
| `McpServer:Enabled` | `McpServer__Enabled` |
| `McpServer:ReadOnly` | `McpServer__ReadOnly` |
| `Logging:LogLevel:Default` | `Logging__LogLevel__Default` |

---

## Environments

HomeBlaze uses the standard `ASPNETCORE_ENVIRONMENT` variable (`Development`, `Staging`, `Production`). The environment selects which `appsettings.{Environment}.json` is layered on top of `appsettings.json`.

Built-in defaults that differ by environment:

| Setting | Development | Production |
|---------|-------------|------------|
| `McpServer:Enabled` | `true` | `false` |
| HTTPS redirection | disabled | enabled |
| HSTS | disabled | enabled |
| Exception handler | developer exception page | `/Error` |
