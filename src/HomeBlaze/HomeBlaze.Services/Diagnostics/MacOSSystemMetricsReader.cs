using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Reads CPU ticks and memory through Mach host statistics. Available memory is free, inactive and speculative
/// pages (speculative pages are reclaimable and already excluded from free_count by the kernel), which
/// approximates Activity Monitor.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacOSSystemMetricsReader : ISystemMetricsReader
{
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const int HostCpuLoadInfoFlavor = 3;
    private const int HostVmInfo64Flavor = 4;
    private const int KernSuccess = 0;

    // mach_host_self allocates a port right; obtained lazily on the first Read so the call runs inside the
    // polling loop's error handling rather than the constructor, and cached because only that loop calls Read.
    private uint? _host;

    private nuint? _pageSize;
    private long? _totalMemory;

    // Starts at zero, so the first read accumulates the ticks since boot.
    private MacOSHostCpuLoadInfo _previousTicks;
    private ulong _busyTicks;
    private ulong _idleTicks;

    public SystemMetricsReading Read()
    {
        var host = _host ??= MachHostSelf();

        var ticks = default(MacOSHostCpuLoadInfo);
        var ticksCount = MacOSHostCpuLoadInfo.Count;
        ThrowOnFailure(HostStatistics(host, HostCpuLoadInfoFlavor, ref ticks, ref ticksCount), "host_statistics");

        _busyTicks = RateCalculator.AccumulateTicks(_busyTicks, _previousTicks.User, ticks.User);
        _busyTicks = RateCalculator.AccumulateTicks(_busyTicks, _previousTicks.System, ticks.System);
        _busyTicks = RateCalculator.AccumulateTicks(_busyTicks, _previousTicks.Nice, ticks.Nice);
        _idleTicks = RateCalculator.AccumulateTicks(_idleTicks, _previousTicks.Idle, ticks.Idle);
        _previousTicks = ticks;

        var statistics = default(MacOSVmStatistics64);
        var statisticsCount = MacOSVmStatistics64.Count;
        ThrowOnFailure(HostStatistics64(host, HostVmInfo64Flavor, ref statistics, ref statisticsCount), "host_statistics64");

        var totalMemory = _totalMemory ??= ReadTotalMemory();
        var pageSize = _pageSize ??= ReadPageSize(host);
        var availableMemory = ((long)statistics.FreeCount + statistics.InactiveCount + statistics.SpeculativeCount) * (long)pageSize;

        return new SystemMetricsReading(_busyTicks, _busyTicks + _idleTicks, totalMemory, Math.Min(availableMemory, totalMemory));
    }

    private static nuint ReadPageSize(uint host)
    {
        ThrowOnFailure(HostPageSize(host, out var pageSize), "host_page_size");
        return pageSize;
    }

    private static long ReadTotalMemory()
    {
        var size = (nuint)sizeof(ulong);
        var result = SysctlByName("hw.memsize", out var memorySize, ref size, 0, 0);
        if (result != 0)
        {
            // sysctlbyname is a POSIX call: it returns -1 and sets errno, not a kern_return_t.
            throw new InvalidOperationException($"sysctlbyname(hw.memsize) failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        return (long)memorySize;
    }

    private static void ThrowOnFailure(int result, string call)
    {
        if (result != KernSuccess)
        {
            throw new InvalidOperationException($"{call} failed with result {result}.");
        }
    }

    [LibraryImport(LibSystem, EntryPoint = "mach_host_self")]
    private static partial uint MachHostSelf();

    [LibraryImport(LibSystem, EntryPoint = "host_statistics")]
    private static partial int HostStatistics(uint host, int flavor, ref MacOSHostCpuLoadInfo info, ref uint count);

    [LibraryImport(LibSystem, EntryPoint = "host_statistics64")]
    private static partial int HostStatistics64(uint host, int flavor, ref MacOSVmStatistics64 info, ref uint count);

    [LibraryImport(LibSystem, EntryPoint = "host_page_size")]
    private static partial int HostPageSize(uint host, out nuint pageSize);

    [LibraryImport(LibSystem, EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int SysctlByName(string name, out ulong value, ref nuint size, nint newValue, nuint newSize);
}
