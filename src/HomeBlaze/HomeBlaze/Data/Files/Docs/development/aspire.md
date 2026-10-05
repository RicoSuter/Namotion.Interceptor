---
title: Aspire Development Environment
navTitle: Aspire
position: 6
---

# Aspire Development Environment

The Aspire AppHost `HomeBlaze.AppHost` starts HomeBlaze from source together with Seq and an OPC UA simulator, and shows the logs, metrics, traces and health of all of them in the Aspire dashboard.

## Prerequisites

- .NET 10 SDK
- Docker Desktop, or Docker Engine. Aspire also supports Podman (`ASPIRE_CONTAINER_RUNTIME=podman`).
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
| `opcplc` | OPC UA PLC simulator with auto-accept and unsecured transport | `opc.tcp://localhost:50000` |

The dashboard shows the health of HomeBlaze from its `/health` endpoint. HomeBlaze runs in the `Development` environment, so its MCP server at `/mcp` is enabled with write access.

## Persistent containers

Both launch profiles set `AppHost__PersistentContainers` to `true`. The `seq` and `opcplc` containers then keep running after the AppHost stops and are reused on the next start, so a restart does not wait for them. Without the setting, the containers belong to the AppHost session and are removed when it stops.

To use session containers, set `AppHost__PersistentContainers` to `false` in `src/HomeBlaze/HomeBlaze.AppHost/Properties/launchSettings.json` for that run.

To start fresh, remove the containers, whose names start with `seq-` and `opcplc-`:

```bash
docker ps -a --filter name=seq- --filter name=opcplc-
docker rm -f <container names>
```

The data of Seq is kept in a volume and survives removing the containers.

## OPC UA simulator

The `opcplc` container runs the [OPC PLC](https://github.com/Azure-Samples/iot-edge-opc-plc) simulator with changing sample values. To connect HomeBlaze to it, add an OPC UA client subject in the UI with the server URL `opc.tcp://localhost:50000`.
