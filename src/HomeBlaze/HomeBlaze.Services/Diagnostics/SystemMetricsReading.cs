namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// One reading of the host. CPU times are cumulative in a platform-specific unit; only their ratio is meaningful.
/// </summary>
internal readonly record struct SystemMetricsReading(
    ulong CpuBusyTime,
    ulong CpuTotalTime,
    long TotalMemory,
    long AvailableMemory);
