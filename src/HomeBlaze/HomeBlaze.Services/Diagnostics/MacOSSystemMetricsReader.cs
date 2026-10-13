using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Reads CPU ticks and memory through Mach host statistics. Available memory is free plus inactive pages,
/// which approximates Activity Monitor.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacOSSystemMetricsReader : ISystemMetricsReader
{
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const int HostCpuLoadInfoFlavor = 3;
    private const int HostVmInfo64Flavor = 4;
    private const int KernSuccess = 0;

    // Each mach_host_self call allocates a port right, so the port is taken once and reused.
    private readonly uint _host = MachHostSelf();

    private long? _totalMemory;
    private bool _hasPreviousTicks;
    private MacOSHostCpuLoadInfo _previousTicks;
    private ulong _userTicks;
    private ulong _systemTicks;
    private ulong _idleTicks;
    private ulong _niceTicks;

    public SystemMetricsReading Read()
    {
        var ticks = default(MacOSHostCpuLoadInfo);
        var ticksCount = MacOSHostCpuLoadInfo.Count;
        ThrowOnFailure(HostStatistics(_host, HostCpuLoadInfoFlavor, ref ticks, ref ticksCount), "host_statistics");

        if (_hasPreviousTicks)
        {
            _userTicks = RateCalculator.AccumulateTicks(_userTicks, _previousTicks.User, ticks.User);
            _systemTicks = RateCalculator.AccumulateTicks(_systemTicks, _previousTicks.System, ticks.System);
            _idleTicks = RateCalculator.AccumulateTicks(_idleTicks, _previousTicks.Idle, ticks.Idle);
            _niceTicks = RateCalculator.AccumulateTicks(_niceTicks, _previousTicks.Nice, ticks.Nice);
        }
        else
        {
            _userTicks = ticks.User;
            _systemTicks = ticks.System;
            _idleTicks = ticks.Idle;
            _niceTicks = ticks.Nice;
            _hasPreviousTicks = true;
        }

        _previousTicks = ticks;

        var statistics = default(MacOSVmStatistics64);
        var statisticsCount = MacOSVmStatistics64.Count;
        ThrowOnFailure(HostStatistics64(_host, HostVmInfo64Flavor, ref statistics, ref statisticsCount), "host_statistics64");

        var totalMemory = _totalMemory ??= ReadTotalMemory();
        var availableMemory = ((long)statistics.FreeCount + statistics.InactiveCount) * Environment.SystemPageSize;
        var totalTicks = _userTicks + _systemTicks + _idleTicks + _niceTicks;

        return new SystemMetricsReading(totalTicks - _idleTicks, totalTicks, totalMemory, Math.Min(availableMemory, totalMemory));
    }

    private static long ReadTotalMemory()
    {
        var size = (nuint)sizeof(ulong);
        ThrowOnFailure(SysctlByName("hw.memsize", out var memorySize, ref size, 0, 0), "sysctlbyname(hw.memsize)");
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

    [LibraryImport(LibSystem, EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SysctlByName(string name, out ulong value, ref nuint size, nint newValue, nuint newSize);
}
