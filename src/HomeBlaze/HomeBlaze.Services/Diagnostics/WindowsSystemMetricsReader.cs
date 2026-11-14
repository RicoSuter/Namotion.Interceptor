using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Reads CPU times and physical memory through kernel32.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsSystemMetricsReader : ISystemMetricsReader
{
    public SystemMetricsReading Read()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var status = new MemoryStatusEx { Length = (uint)Unsafe.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        // Kernel time includes idle time.
        var totalTime = kernelTime + userTime;
        return new SystemMetricsReading(totalTime - idleTime, totalTime, (long)status.TotalPhysical, (long)status.AvailablePhysical);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    // FILETIME is two little-endian 32-bit halves, read here as one 64-bit count of 100 ns units.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
