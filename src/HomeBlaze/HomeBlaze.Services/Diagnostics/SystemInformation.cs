using System.Runtime.InteropServices;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Reads the host values that do not change while the process runs.
/// </summary>
internal static class SystemInformation
{
    public static string ReadOperatingSystemName()
    {
        if (OperatingSystem.IsLinux() &&
            TryReadAllText("/etc/os-release") is { } osRelease &&
            LinuxProcParser.ParseOsReleasePrettyName(osRelease) is { } prettyName)
        {
            return prettyName;
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"macOS {Environment.OSVersion.Version}";
        }

        return RuntimeInformation.OSDescription;
    }

    public static bool IsContainer()
    {
        return string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase) ||
               File.Exists("/.dockerenv") ||
               File.Exists("/run/.containerenv");
    }

    /// <summary>
    /// Returns the cgroup memory limit, or null outside Linux, without a limit, or when it is not below the total memory.
    /// </summary>
    public static long? ReadContainerMemoryLimit(long? totalMemory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var content = TryReadAllText("/sys/fs/cgroup/memory.max") ??
                      TryReadAllText("/sys/fs/cgroup/memory/memory.limit_in_bytes");
        return LinuxProcParser.ParseCgroupMemoryLimit(content, totalMemory);
    }

    /// <summary>
    /// Returns the cgroup CPU limit in cores, or null outside Linux or without a limit.
    /// </summary>
    public static decimal? ReadContainerCpuLimit()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var cpuMax = TryReadAllText("/sys/fs/cgroup/cpu.max");
        return cpuMax is not null
            ? LinuxProcParser.ParseCgroupV2CpuMax(cpuMax)
            : LinuxProcParser.ParseCgroupV1CpuLimit(
                TryReadAllText("/sys/fs/cgroup/cpu/cpu.cfs_quota_us"),
                TryReadAllText("/sys/fs/cgroup/cpu/cpu.cfs_period_us"));
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
