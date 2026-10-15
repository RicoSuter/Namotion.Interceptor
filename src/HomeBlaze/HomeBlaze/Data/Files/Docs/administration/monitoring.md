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

## Installation

The `Installation` subject describes the running HomeBlaze installation: its build, the assemblies it loaded, its environment and whether a newer release exists. A new installation has one in `Servers/Installation.json`; otherwise create it from the create dialog in the `System` group. Only one can be created.

| Property | Description |
|----------|-------------|
| `Version`, `Channel` | The running version and whether it is a `Release`, an `Edge` build of the latest commit, or a `Development` build |
| `Commit`, `Tag`, `Branch`, `CommitDate`, `BuildTime` | Where and when the build was made; only CI builds have a tag, branch and times |
| `RepositoryUrl`, `VersionUrl`, `CommitUrl`, `BuildRunUrl` | Links to the repository, to the release or commit, and to the CI run |
| `EnvironmentName`, `IsDeveloperMode` | The hosting environment and whether developer mode is on |
| `DataDirectory`, `DataVolumeFreeSpace`, `DataVolumeTotalSize`, `DataVolumeUsage` | The data folder and the free space, total size and usage of its volume |
| `Repositories` | The loaded assemblies grouped by the repository they were built from, each with version, commit and load context |
| `UpdateAvailable`, `LatestVersion`, `LatestReleaseDate`, `LatestReleaseUrl`, `LastUpdateCheck`, `UpdateCheckStatus` | The result of the update check, see below |

`Status` is `Running` while the installation runs and `Stopped` otherwise. The environment values refresh every `PollingInterval` (30 seconds by default, at least 1 second), and the inventory refreshes on the next poll after an assembly was loaded. The inventory leaves out dynamic assemblies and those of the shared .NET framework; an assembly in a plugin load context is listed with the context name in parentheses. A dashboard widget shows the version, channel and build, an available update, `UpdateCheckStatus` when it is set, the data volume usage and the environment.

### Update check

Only release builds can check for updates. For an edge or development build, a repository outside GitHub or not in the owner/name form, or no repository URL at all, `UpdateCheckStatus` says why the check is not available.

The update check is off by default. With `UpdateCheckEnabled`, HomeBlaze asks the GitHub API for the latest release once every `UpdateCheckInterval` (24 hours by default, from 24 hours up to 365 days), starting 5 to 60 minutes after the start. The request sends nothing about the installation except the version in its user agent. A check is one request, plus the retries after a failure. GitHub allows 60 unauthenticated requests per hour per IP address, shared by everything behind that address. A request that takes longer than 30 seconds fails the check with "GitHub did not answer in time."

After a failure the check backs off 1, 2, 4, 8 and 16 hours, doubling each time up to a cap of 24 hours; a longer wait requested by GitHub wins instead, also capped at 24 hours. The `Check for updates` operation checks immediately, at most once per minute, also while the scheduled check is off. It reports why no check is possible, and it is refused while the installation is stopped or while GitHub asked to wait, with the minutes that remain. A change of `UpdateCheckEnabled` or `UpdateCheckInterval` in the editor or the configuration file applies at once; one written directly to the property, for example through MCP, applies within an hour.

`UpdateAvailable` is true when the latest release is newer than the running version and false when it is not or when the repository has no release. It stays empty until the first successful check, and when the running version is not a semantic version. `LatestVersion`, `LatestReleaseDate` and `LatestReleaseUrl` describe the latest release, and an available update colors the icon as a warning. `LastUpdateCheck` is when the last check finished, successful or not. `UpdateCheckStatus` says why no check can run for this build, or otherwise holds the error of the last check until a check succeeds; it is empty while the check is simply off and nothing failed. A failed check is reported only there and does not change `Status`.

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
