---
title: Aspire Development Environment
navTitle: Aspire
position: 6
---

# Aspire Development Environment

The Aspire AppHost `HomeBlaze.AppHost` starts HomeBlaze from source together with Seq, n8n and an OPC UA simulator, and shows the logs, metrics, traces and health of all of them in the Aspire dashboard.

## Prerequisites

- .NET 10 SDK
- Docker Desktop, or Docker Engine with the buildx plugin (`docker-buildx` or `docker-buildx-plugin` in distribution packages). Without buildx, Aspire cannot build the proxy image of its container tunnel, and n8n cannot reach HomeBlaze. Aspire also supports Podman (`ASPIRE_CONTAINER_RUNTIME=podman`).
- A trusted HTTPS development certificate: `dotnet dev-certs https --trust`. On Linux, also add the certificate folder to `SSL_CERT_DIR` as described in the [ASP.NET Core documentation](https://learn.microsoft.com/aspnet/core/security/enforcing-ssl).

## Running

```bash
dotnet run --project src/HomeBlaze/HomeBlaze.AppHost
```

Or start `HomeBlaze.AppHost` with F5 in the IDE. `dotnet run` uses the first launch profile, `https`, and hands over to the Aspire CLI. The console prints a login link for the dashboard at https://localhost:17245. The `http` profile runs without HTTPS and can be selected in the IDE.

Stop the AppHost with Ctrl+C in its console. Killing the `dotnet run` process does not stop it.

## Resources

| Resource | Description | Address |
|----------|-------------|---------|
| `homeblaze` | HomeBlaze from source, with the development data folder `src/HomeBlaze/HomeBlaze/Data` | http://localhost:5192 |
| `seq` | Seq log server, receives the logs and traces of HomeBlaze | URL shown in the dashboard |
| `n8n` | n8n workflow automation, pinned version | http://localhost:5678 |
| `opcplc` | OPC UA PLC simulator with auto-accept and unsecured transport | `opc.tcp://localhost:50000` |

The dashboard shows the health of HomeBlaze from its `/health` endpoint. HomeBlaze runs in the `Development` environment, so its MCP server at `/mcp` is enabled with write access.

## Persistent containers

Both launch profiles set `AppHost__PersistentContainers` to `true`. The `seq`, `n8n` and `opcplc` containers then keep running after the AppHost stops and are reused on the next start, so a restart does not wait for them. Without the setting, the containers belong to the AppHost session and are removed when it stops.

To use session containers, set `AppHost__PersistentContainers` to `false` in `src/HomeBlaze/HomeBlaze.AppHost/Properties/launchSettings.json` for that run.

To start fresh, remove the containers, whose names start with `seq-`, `n8n-` and `opcplc-`:

```bash
docker ps -a --filter name=seq- --filter name=n8n- --filter name=opcplc-
docker rm -f <container names>
```

The data of Seq and n8n is kept in volumes and survives removing the containers.

## n8n

Open http://localhost:5678 and create the owner account on the first start. To use HomeBlaze from an AI agent workflow, add an **MCP Client Tool** node with transport **HTTP Streamable** and endpoint:

```
http://aspire.dev.internal:5192/mcp
```

`aspire.dev.internal` is the name under which containers reach services on the host in an Aspire session. The n8n container also receives the address in the `services__homeblaze__http__0` environment variable.

n8n encrypts its stored credentials with a key that the AppHost generates once and keeps in its user secrets (`Parameters:n8n-encryption-key`). The key and the `homeblaze-n8n-data` volume belong together: if the user secrets are cleared, a new key is generated and the existing volume can no longer be decrypted, so delete the volume as well:

```bash
docker volume rm homeblaze-n8n-data
```

The n8n image is pinned because n8n migrates its database on the volume when it upgrades. Change the version in `AppHost.cs` deliberately.

## OPC UA simulator

The `opcplc` container runs the [OPC PLC](https://github.com/Azure-Samples/iot-edge-opc-plc) simulator with changing sample values. To connect HomeBlaze to it, add an OPC UA client subject in the UI with the server URL `opc.tcp://localhost:50000`.
