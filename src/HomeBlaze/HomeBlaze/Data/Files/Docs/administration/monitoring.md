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

The `Installation` subject describes the running HomeBlaze installation: its build, the assemblies it loaded, its environment, its health and whether a newer version exists. A new installation has one in `Servers/Installation.json`; otherwise create it from the create dialog in the `System` group. Only one can be created.

| Property | Description |
|----------|-------------|
| `Version`, `Channel` | The running version and whether it is a `Release`, an `Edge` build of the latest commit, or a `Development` build |
| `Commit`, `Tag`, `Branch`, `CommitDate`, `BuildTime`, `ImageTags` | Where and when the build was made; only CI builds have a tag, branch, times and image tags |
| `VersionUrl`, `CommitUrl`, `BuildRunUrl` | Links to the release or commit on GitHub and to the CI run |
| `EnvironmentName`, `IsDeveloperMode`, `ListeningUrls`, `StartTime` | How HomeBlaze runs and when it started |
| `DataDirectory`, `DataVolumeFreeSpace`, `DataVolumeTotalSize`, `DataVolumeUsage` | The data folder and the free space, total size and usage of its volume |
| `SubjectCount`, `MonitoredServiceCount`, `ServicesInErrorCount` | Size of the graph, excluding this installation's own repository and assembly subjects, and services in error; these do not change the status of the installation itself |
| `Repositories` | The loaded assemblies grouped by the repository they were built from, each with version, commit and load context |
| `HasVersionMismatch`, `MismatchedRepositories` | Whether the loaded assemblies are inconsistent, see below |
| `UpdateAvailable`, `LatestVersion`, `LatestReleaseDate`, `LatestReleaseUrl`, `CommitsBehind`, `ChangesUrl`, `LastUpdateCheck`, `UpdateCheckStatus` | The result of the update check, see below |

The build, environment and health values refresh every `PollingInterval` (30 seconds by default, at least 1 second). The assembly inventory also refreshes on each poll, but only after a new assembly has been loaded since the last one. A dashboard widget shows the version, channel and build, an update or mismatch warning, the reason of a failed update check while the installation is in `Error`, service and subject counts, and data volume usage.

### Version mismatches

An assembly is marked when the same assembly is loaded more than once with another version or commit, usually because a plugin brings its own copy of a library HomeBlaze already has. A repository is marked when the assemblies of the repository HomeBlaze itself was built from come from more than one commit. Other repositories only list their commits, because large repositories ship packages at different versions. An assembly with a missing commit or version never causes a mismatch on that assembly.

### Update check

The update check is off by default. With `UpdateCheckEnabled`, HomeBlaze asks the GitHub API for the latest release once every `UpdateCheckInterval` (24 hours by default, from 24 hours up to 365 days), starting 5 to 60 minutes after the start. An edge build also asks how many commits its build branch (`master` by default) has that its own commit does not. The latest-release request sends nothing about the installation except the version in its user agent; the comparison request of an edge build also contains its commit and branch in the URL. Each request repeats the last answer's ETag so an unchanged answer is not downloaded again. A check uses one request for a release build and two for an edge build, plus the retries after a failure. GitHub allows 60 unauthenticated requests per hour per IP address, shared by everything behind that address; since the requests are unauthenticated, an unchanged (`304`) answer counts against the limit the same as any other. A request that takes longer than 30 seconds fails the check with "GitHub did not answer in time."

After a failure the check backs off 1, 2, 4, 8 and 16 hours, doubling each time up to a cap of 24 hours; a longer wait requested by GitHub wins instead, also capped at 24 hours. The `Check for updates` operation checks immediately, at most once per minute, also while the scheduled check is off; it reports why no check is possible, such as for a development build or when the repository is not on GitHub, and it is refused while GitHub asked to wait, with the minutes that remain. A change of `UpdateCheckEnabled` or `UpdateCheckInterval` in the editor or the configuration file applies at once; one written directly to the property, for example through MCP, applies within an hour.

`UpdateAvailable`, `LatestVersion`, `LatestReleaseDate` and `LatestReleaseUrl` show the result; an edge build also gets `CommitsBehind` and `ChangesUrl`. `LastUpdateCheck` is when the last check finished, successful or not. `UpdateCheckStatus` says why no check can run for this build, for example a development build or a repository outside GitHub, or otherwise holds the error of the last check until a check succeeds. It is empty while the check is simply off and nothing failed. A failed check puts the installation into `Error` while `UpdateCheckEnabled` is on, until the next success or until the check is turned off.

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
