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

## Data folder {#data-folder}

HomeBlaze keeps everything it stores in one data folder: the folder that contains the root configuration file `Root.json`. Relative paths in the configuration resolve against this folder.

```
Data/
├── Root.json       root configuration
├── Files/          devices, dashboards, pages, Plugins.json, docs
├── History/Sqlite/ SQLite history
├── OpcUa/          OPC UA certificates
└── Plugins/Cache/  downloaded plugin packages
```

When running from source, the data folder is `src/HomeBlaze/HomeBlaze/Data`. The container image uses `/data`; see [Installation](installation.md#data-folder).

`Plugins/Cache` only holds packages that HomeBlaze downloads again when they are missing, so it can be excluded from backups as long as the configured feeds still serve those package versions. Plugins are configured in plugin provider files such as `Files/Plugins.json`, which can live anywhere in the subject tree. Relative feed folders and a relative cache directory in these files resolve against the data folder. See [Plugin System Design](../architecture/design/plugins.md#configuration) for the fields.

---

## HomeBlaze:RootConfigFile

Root configuration file. Its folder is the data folder. A relative path resolves against the working directory; an empty value uses the default.

| Setting | Default |
|---------|---------|
| `HomeBlaze:RootConfigFile` | `Data/Root.json` |

Override example:

```json
{
  "HomeBlaze": {
    "RootConfigFile": "/srv/homeblaze/Root.json"
  }
}
```

The container image sets it to `/data/Root.json`.

---

## HomeBlaze:SeedDirectory

Folder that is copied into the data folder on the first start, when the root configuration file does not exist. Existing files are never overwritten, and the root configuration file is copied last, so an interrupted copy is completed on the next start.

| Setting | Default |
|---------|---------|
| `HomeBlaze:SeedDirectory` | not set (nothing is copied) |

Override example:

```json
{
  "HomeBlaze": {
    "SeedDirectory": "/opt/homeblaze/Seed"
  }
}
```

The container image sets it to `/app/Seed`, which holds the starting set described in [Installation](installation.md#data-folder).

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

`appsettings.Development.json` enables the MCP server with write access for local testing:

```json
{
  "McpServer": {
    "Enabled": true,
    "ReadOnly": false
  }
}
```

### Example: Production with read-only access

`appsettings.json` exposes MCP in production but keeps it read-only:

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

## Telemetry

HomeBlaze always serves the health endpoints `/health` and `/alive`. Logs, metrics and traces are exported only when a target is configured.

| Setting | Default | Description |
|---------|---------|-------------|
| `ConnectionStrings:seq` | not set | Seq server URL, for example `http://seq:5341`; exports logs and traces to Seq |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | not set | OpenTelemetry (OTLP) endpoint for logs, metrics and traces |

See [Monitoring](monitoring.md) for details.

---

## Environment Variables

Any setting can be overridden by an environment variable using the ASP.NET Core `__` separator:

| Setting | Environment variable |
|---------|---------------------|
| `HomeBlaze:RootConfigFile` | `HomeBlaze__RootConfigFile` |
| `HomeBlaze:SeedDirectory` | `HomeBlaze__SeedDirectory` |
| `McpServer:Enabled` | `McpServer__Enabled` |
| `McpServer:ReadOnly` | `McpServer__ReadOnly` |
| `ConnectionStrings:seq` | `ConnectionStrings__seq` |
| `Logging:LogLevel:Default` | `Logging__LogLevel__Default` |

`OTEL_EXPORTER_OTLP_ENDPOINT` is already an environment variable name.

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
