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
├── authentication.db    users, password hashes and roles (and authentication.db.seeded next to it)
├── History/Sqlite/      SQLite history
└── OpcUa/               OPC UA certificates
```

When running from source, the data folder is `src/HomeBlaze/HomeBlaze/Data`. The container image uses `/data`; see [Installation](installation.md#data-folder).

`DataProtection-Keys/` holds the secret keys that sign the browser's connection to the UI and any sign-in, so treat the folder and every backup of it like a password, or encrypt it with a certificate (see [HomeBlaze:DataProtection](#homeblazedataprotection)). To revoke the keys, for example after a backup leaked or in a copy of the data folder that runs as a second instance, stop HomeBlaze, delete the folder and start it again. Open browser tabs then need a reload, and everyone has to sign in again, because the session cookie is encrypted with these keys.

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

## Authentication

Controls the authentication database and how HomeBlaze trusts a reverse proxy. Users, roles and sign-in are described in [Authorization](authorization.md).

```json
{
  "Authentication": {
    "Database": "/data/authentication.db",
    "ForwardedHeadersKnownNetworks": ["172.18.0.0/16"],
    "ForwardedHeadersKnownProxies": [],
    "PathBase": "/homeblaze"
  }
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Authentication:Database` | path | `authentication.db` in the data folder | The SQLite file with the users, their password hashes and the roles. It stays outside the browsable `Files` storage. It is read from `appsettings` because it must be known before anyone can sign in. Use an absolute path |
| `Authentication:ForwardedHeadersKnownNetworks` | list of CIDR networks | empty | Networks of reverse proxies whose `X-Forwarded-For`, `-Proto` and `-Host` headers are trusted, such as `10.0.0.0/8` |
| `Authentication:ForwardedHeadersKnownProxies` | list of addresses | empty | Addresses of reverse proxies whose forwarded headers are trusted |
| `Authentication:PathBase` | string | not set | The base path when a reverse proxy serves HomeBlaze below a path, such as `/homeblaze` |

Without a trusted network or proxy no forwarded header is applied, because any client could send one to spoof its address, scheme or host. A proxy that terminates TLS must be listed, or the session cookie is not marked secure and the sign-in dialogs show the warning about unencrypted HTTP. An address that is no valid network in CIDR notation stops the start with a message naming the setting. See [Remote Access](authorization.md#remote-access).

Changing `Authentication:Database` to a file that does not exist creates a new database, which is seeded like a first start: `admin` with an empty password and none of the earlier users or roles, because the marker `authentication.db.seeded` belongs to the database file and the new file has none. Move the file instead of pointing the setting at a new one when you want to keep them. See [First Start](authorization.md#first-start) for what the marker guarantees.

---

## McpServer

Controls the built-in Model Context Protocol server that exposes the knowledge graph to AI agents.

```json
{
  "McpServer": {
    "Enabled": false
  }
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `McpServer:Enabled` | bool | `true` in Development, `false` otherwise | Registers the MCP server and maps the `/mcp` endpoint. The built-in agents do not need it |

There is no read-only setting. What clients of `/mcp` may do is decided by the roles of the built-in user `mcp`, `reader` by default, which reads and runs queries but writes nothing and runs no operation, with the [system rules](authorization.md#system-rules): they cannot read secrets, write configuration, run operations that require confirmation, reach agents, or read or change the `Authorization` subject, roles and users. All clients share these roles; a role per client is not available yet. Change the roles with the operation "Set Roles" of the user `mcp` below the `Authorization` subject. Built-in agents use their own `AllowedTools` and `Roles` instead, see [AI Agents](ai-agents.md#tools-and-permissions).

The endpoint is unauthenticated until a key is created for `mcp`; then clients must send it as a bearer token. Both are described in [Anonymous and MCP](authorization.md#anonymous-and-mcp).

### Example: Development override

`appsettings.Development.json` enables the MCP server for local testing:

```json
{
  "McpServer": {
    "Enabled": true
  }
}
```

### Example: Production

`appsettings.json` exposes MCP in production. Keep the roles of `mcp` at `reader` and create a key before anything else can reach it:

```json
{
  "McpServer": {
    "Enabled": true
  }
}
```

Enabling MCP in production exposes the graph to any client that can reach the `/mcp` endpoint, with the access of the roles of `mcp`, and to anyone at all while it has no key. For the design, see [Security Design](../architecture/design/security.md).

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
| `Authentication:Database` | `Authentication__Database` |
| `Authentication:ForwardedHeadersKnownNetworks` | `Authentication__ForwardedHeadersKnownNetworks__0` (and `__1`, ...) |
| `Authentication:ForwardedHeadersKnownProxies` | `Authentication__ForwardedHeadersKnownProxies__0` (and `__1`, ...) |
| `Authentication:PathBase` | `Authentication__PathBase` |
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
