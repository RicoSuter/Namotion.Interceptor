using System.Runtime.InteropServices;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// host_cpu_load_info_data_t: cumulative ticks per CPU state, 32 bits each, which wrap around.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MacOSHostCpuLoadInfo
{
    /// <summary>HOST_CPU_LOAD_INFO_COUNT.</summary>
    public const uint Count = 4;

    public uint User;
    public uint System;
    public uint Idle;
    public uint Nice;
}

/// <summary>
/// vm_statistics64_data_t. The field order and widths must match the C header exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MacOSVmStatistics64
{
    /// <summary>HOST_VM_INFO64_COUNT.</summary>
    public const uint Count = 38;

    public uint FreeCount;
    public uint ActiveCount;
    public uint InactiveCount;
    public uint WireCount;
    public ulong ZeroFillCount;
    public ulong Reactivations;
    public ulong Pageins;
    public ulong Pageouts;
    public ulong Faults;
    public ulong CowFaults;
    public ulong Lookups;
    public ulong Hits;
    public ulong Purges;
    public uint PurgeableCount;
    public uint SpeculativeCount;
    public ulong Decompressions;
    public ulong Compressions;
    public ulong Swapins;
    public ulong Swapouts;
    public uint CompressorPageCount;
    public uint ThrottledCount;
    public uint ExternalPageCount;
    public uint InternalPageCount;
    public ulong TotalUncompressedPagesInCompressor;
}
