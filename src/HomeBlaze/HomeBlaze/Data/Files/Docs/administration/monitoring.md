---
title: Monitoring
navTitle: Monitoring
---

# Monitoring and Health Checks

HomeBlaze reports its health over HTTP and exports logs, metrics and traces with OpenTelemetry. The settings are listed in [Configuration](configuration.md).

See [Observability Design](../architecture/design/observability.md) for the architectural design.

The running version, its build and the update check are described in [Version and Updates](updates.md).

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
| `Process.GcCommittedSize` | Memory the garbage collector held from the operating system at the last garbage collection, including free space it keeps for reuse |
| `Process.GcFragmentedSize` | Free space between objects in the .NET heap at the last garbage collection |
| `Process.Gen2Size`, `Process.LargeObjectHeapSize` | Long-lived objects and objects of 85,000 bytes or more after the last full garbage collection, including the free space between them |
| `Process.ResidentAnonymousMemory`, `Process.ResidentFileMemory` | Physical memory not backed by a file (heaps, stacks) and backed by one (executable, libraries); Linux only |
| `Process.HandleCount` | Open file descriptors on Linux, kernel handles on Windows; empty on macOS |
| `Process.TimerCount` | Active .NET timers, including the ones behind `Task.Delay` and `CancelAfter` |
| `Process.ThreadPoolThreadCount`, `Process.ThreadPoolQueueLength` | Thread pool threads, and work items waiting for one |
| `Process.LockContentionRate` | Times per second a thread waited for a lock another thread held |
| `Process.ExceptionRate` | Exceptions thrown per second, caught or not; an exception rethrown through `await` counts again at each awaiting method |
| `Process.ResidentSharedMemory` | Physical shared memory; Linux only. It holds the code .NET compiles at runtime only while write-xor-execute is on, the default; with `DOTNET_EnableWriteXorExecute=0` that memory shows in `ResidentAnonymousMemory` instead |
| `Process.JitCompiledMethodCount` | Methods the JIT compiler compiled since HomeBlaze started |

The values refresh every `PollingInterval` (15 seconds by default, at least 1 second). CPU usage, allocation rate, GC pause time, lock contention rate and exception rate appear after the second reading. The garbage collection values stay empty until the first collection, `Gen2Size` and `LargeObjectHeapSize` until the first full one.

All of them are recorded in history, so a trend shows where memory or CPU goes:

| Trend | Points to |
|-------|-----------|
| `Gen2Size` or `LargeObjectHeapSize` rises over days | A managed leak: objects that are never released. Both are measured after the last full garbage collection and include free space. Under Server GC most full collections run in the background and do not compact, so free space can keep them high; run `Collect garbage`, which compacts, and compare the values right after it over days to tell a leak from fragmentation |
| `GcCommittedSize` keeps growing over days while `GcHeapSize` does not | Free memory the garbage collector keeps instead of returning it to the operating system; `Collect garbage` returns it. Staying far above `GcHeapSize` is normal under Server GC, so only a persistent growth matters |
| `ResidentAnonymousMemory` grows while `GcCommittedSize` does not | Native memory outside .NET, such as SQLite or a device driver. If `SqliteMemorySize` of the SQLite history store stays flat meanwhile, the growth is not in memory SQLite currently holds, though the C library may still keep heap SQLite freed; a rising `OpenConnectionCount` points to databases the store keeps open |
| `JitCompiledMethodCount` or `ResidentSharedMemory` keeps growing and does not level off long after startup | Code generated at runtime again and again, such as a regular expression or expression tree compiled per call. Tiered recompilation and code paths first used later add some, so only a growth that continues matters |
| `HandleCount` or `TimerCount` keeps growing | Connections, files or timers that are never closed |
| `ThreadPoolQueueLength` stays above zero while `Process.CpuUsage` is low | Blocked threads: work waits for a thread although the CPU is idle |
| `ExceptionRate` or `LockContentionRate` stays high | A retry loop or contended lock that costs CPU |

To see which exceptions are thrown, set the log level of `HomeBlaze.Services.Diagnostics.FirstChanceExceptionLogger` to `Debug`, then look at the log server, for example [Seq](#seq), and remove the setting again when done. The key `Logging:LogLevel:HomeBlaze.Services.Diagnostics.FirstChanceExceptionLogger` in `appsettings.json` applies as soon as the file is saved, the environment variable `Logging__LogLevel__HomeBlaze.Services.Diagnostics.FirstChanceExceptionLogger=Debug` after a restart, and `Logging:LogLevel:Default` set to `Debug` turns it on as well. Each entry carries several kilobytes of stack and also goes to the console.

- **Throw site:** the first method on the stack outside the .NET framework (namespaces `System` and `Microsoft`), so exceptions thrown by shared framework helpers, such as an `await` of a canceled task, are told apart by the code that called them. When the stack has only framework frames, it is the first frame the runtime shows, which is the framework method that awaited or called the throwing helper.
- **ThrowingFrame:** the method that executed the throw: user code when user code throws, a framework helper otherwise, `unknown` when no frame is available.
- **CallStack:** the stack of the thread at the throw, which for code after an `await` shows the throwing method above thread pool frames only. The exception attached to the entry carries its own stack trace, which usually also lists the methods it passed through on the way up.
- **Counting:** an exception rethrown or passing through `await`s is logged and counted once, where it was first thrown, and an exception object that is thrown again and again (a cached instance) is logged once while the process runs. At most one entry per exception type and throw site is written per minute; the next one reports how many were skipped meanwhile. The counts are kept while the process runs, so a skipped count can include exceptions from an earlier time Debug was on.
- **Limits:** up to 1024 combinations of type and site are tracked; others are not logged until restart, which a single entry reports. Entries are written in the background; once 256 are waiting, new ones are dropped, a separate entry reports how many, and each dropped one counts as skipped for its type and site.
- **Cost:** with Debug off, nothing beyond counting the exception. With Debug on, the stack of every exception is captured to find its throw site, including skipped ones.

The SQLite history store reports `OpenConnectionCount` and `SqliteMemorySize`, the memory all SQLite databases in the process hold.

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
