---
title: Configuration
navTitle: Configuration
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

## Data folder

HomeBlaze keeps everything it stores in one data folder: the folder that contains the root configuration file `Root.json`. Relative paths in the configuration resolve against this folder.

```
Data/
├── Root.json            root configuration
├── Files/               devices, dashboards, pages, Plugins.json, docs
├── Agents/              timeline, memory, chat session, run count and CLI working directory of AI agents
├── DataProtection-Keys/ ASP.NET Core keys that keep antiforgery tokens valid across restarts
├── History/Sqlite/      SQLite history
└── OpcUa/               OPC UA certificates
```

When running from source, the data folder is `src/HomeBlaze/HomeBlaze/Data`. The container image uses `/data`; see [Installation](installation.md#data-folder).

`DataProtection-Keys/` holds the secret keys that sign the browser's connection to the UI and any sign-in, so treat the folder and every backup of it like a password, or encrypt it with a certificate (see [HomeBlaze:DataProtection](#homeblazedataprotection)). To revoke the keys, for example after a backup leaked or in a copy of the data folder that runs as a second instance, stop HomeBlaze, delete the folder and start it again. Open browser tabs then need a reload.

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

Its `Files/Docs` folder is the exception: it replaces `Files/Docs` in the data folder on every start. See [Installation](installation.md#data-folder).

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

## HomeBlaze:DataProtection

Certificate that encrypts the keys in `DataProtection-Keys/`. Without it, the keys are stored unencrypted on Linux and macOS, readable only by the user HomeBlaze runs as, and encrypted with the Windows account (DPAPI) on Windows. With it, a copy of the data folder, such as a backup, is useless without the certificate.

| Setting | Default |
|---------|---------|
| `HomeBlaze:DataProtection:CertificatePath` | not set (no certificate) |
| `HomeBlaze:DataProtection:CertificatePassword` | not set (no password) |

The certificate is a PKCS#12 file (`.pfx`) with its private key. A relative path resolves against the working directory. Keep it outside the data folder and out of its backups, otherwise it protects nothing. HomeBlaze does not start when the file is missing, has no private key or the password is wrong.

Create one, for example valid for ten years:

```bash
openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj "/CN=HomeBlaze Data Protection" -keyout key.pem -out cert.pem
openssl pkcs12 -export -inkey key.pem -in cert.pem -out data-protection.pfx -passout pass:
rm key.pem cert.pem
```

In the compose file, mount it as a secret:

```yaml
services:
  homeblaze:
    environment:
      HomeBlaze__DataProtection__CertificatePath: /run/secrets/data-protection.pfx
    secrets:
      - data-protection.pfx

secrets:
  data-protection.pfx:
    file: ./secrets/data-protection.pfx
```

Keys written before the certificate was set stay unencrypted and in use, and keys encrypted with a certificate cannot be read without it. So after setting, replacing or removing the certificate, stop HomeBlaze and delete `DataProtection-Keys/` before starting it again.

---

## PluginConfigurationPath

Path to the runtime plugin manifest (`Plugins.json`). A relative path resolves against the data folder.

| Setting | Default |
|---------|---------|
| `PluginConfigurationPath` | `Files/Plugins.json` |

Override example:

```json
{
  "PluginConfigurationPath": "/etc/homeblaze/plugins.json"
}
```

Relative feed URLs and the cache directory inside `Plugins.json` resolve against the application directory, not the data folder.

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
| `McpServer:Enabled` | bool | `true` in Development, `false` otherwise | Registers the MCP server and maps the `/mcp` endpoint. The built-in agents do not need it |
| `McpServer:ReadOnly` | bool | `true` | Restricts the tools of `/mcp` to read-only operations; `false` allows property writes and operation invocations. `set_property` never writes configuration properties either way. Built-in agents use their own `AllowedTools` instead. See [AI Agents](ai-agents.md#tools-and-permissions) |

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
| `PluginConfigurationPath` | `PluginConfigurationPath` |
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
