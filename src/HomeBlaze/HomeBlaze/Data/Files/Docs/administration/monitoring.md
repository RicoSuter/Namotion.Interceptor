---
title: Monitoring
navTitle: Monitoring
---

# Monitoring and Health Checks

HomeBlaze reports its health over HTTP and exports logs, metrics and traces with OpenTelemetry. The settings are listed in [Configuration](configuration.md).

See [Observability Design](../architecture/design/observability.md) for the architectural design.

## Host system

The `Host system` subject reports the machine HomeBlaze runs on and the HomeBlaze process. A new installation has one in `Servers/HostSystem.json`; otherwise create it from the create dialog in the `System` group. Only one can be created.

| Property | Description |
|----------|-------------|
| `CpuUsage` | CPU usage of the whole host across all its CPUs |
| `TotalMemory`, `AvailableMemory`, `MemoryUsage` | Physical memory of the host |
| `Uptime` | Time since the host booted |
| `ContainerMemoryLimit`, `ContainerCpuLimit` | Limits of the container HomeBlaze runs in, empty without a limit |
| `Process.CpuUsage` | CPU usage of HomeBlaze across the processors available to it |
| `Process.WorkingSet` | Physical memory used by HomeBlaze |
| `Process.GcHeapSize` | Size of the .NET heap at the last garbage collection |
| `Process.AllocationRate` | Memory HomeBlaze allocates per second |
| `Process.GcPauseTime` | Share of the time the garbage collector paused HomeBlaze; under 1 % is normal |

The values refresh every `PollingInterval` (15 seconds by default, at least 1 second). CPU usage, allocation rate and GC pause time appear after the second reading.

Inside a container the host values describe the machine running the container, and the container limits sit beside them. `Process.CpuUsage` is measured against the processors available to HomeBlaze, which is the container CPU limit rounded up.

| Platform | Host CPU and memory | Container limits |
|----------|---------------------|------------------|
| Linux | Yes | Yes (cgroup v1 and v2) |
| Windows | Yes | No |
| macOS | Yes, available memory approximates Activity Monitor | No |

The `Collect garbage` operation on `Process` runs a full, compacting garbage collection and returns free memory to the operating system. It pauses HomeBlaze while it runs and asks for confirmation. Use it to tell a memory leak apart from memory the garbage collector has not reclaimed yet.

## Health endpoints

| Endpoint | Checks | Use |
|----------|--------|-----|
| `/health` | All health checks | Readiness, for example a load balancer or uptime monitor |
| `/alive` | Liveness checks only | Liveness, for example a container restart policy |

Both endpoints are available in every environment, return only the status (`Healthy`, `Degraded` or `Unhealthy`) and are not traced. An unreachable Seq server does not affect them.

```bash
curl http://localhost:8080/health
```

## OpenTelemetry

Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export logs, metrics and traces over OTLP to any OpenTelemetry backend or collector. The other standard OTLP exporter variables, such as `OTEL_EXPORTER_OTLP_PROTOCOL` and `OTEL_EXPORTER_OTLP_HEADERS`, apply as well.

HomeBlaze records traces only when an OTLP endpoint or Seq is configured.

## Seq

Set `ConnectionStrings:seq` (environment variable `ConnectionStrings__seq`) to the URL of a [Seq](https://datalust.co/seq) server to send logs and traces to it, for example `http://seq:5341` with the `seq` profile of the [Docker compose file](installation.md#optional-services). If Seq is not reachable, HomeBlaze keeps running and the export fails silently.

Seq and an OTLP endpoint can be used at the same time.

## Aspire dashboard

During development, the Aspire AppHost collects the logs, metrics and traces of HomeBlaze in the Aspire dashboard. See [Aspire](../development/aspire.md).
