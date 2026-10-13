using System.Buffers.Text;
using System.Globalization;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Parses the Linux files the system host reads. Polled files are parsed from bytes so a poll allocates no strings.
/// </summary>
internal static class LinuxProcParser
{
    /// <summary>
    /// Parses the aggregate first line of /proc/stat. The total is the sum of user to steal; guest and guest_nice
    /// are already part of user and nice. Idle is idle plus iowait.
    /// </summary>
    public static bool TryParseProcStat(ReadOnlySpan<byte> content, out ulong busyTime, out ulong totalTime)
    {
        busyTime = 0;
        totalTime = 0;

        if (!content.StartsWith("cpu "u8))
        {
            return false;
        }

        var lineEnd = content.IndexOf((byte)'\n');
        var fields = lineEnd >= 0 ? content[4..lineEnd] : content[4..];

        ulong idleTime = 0;
        var fieldCount = 0;
        while (fieldCount < 8)
        {
            fields = fields.TrimStart((byte)' ');
            if (fields.IsEmpty)
            {
                break;
            }

            if (!Utf8Parser.TryParse(fields, out ulong value, out var consumed))
            {
                return false;
            }

            fields = fields[consumed..];
            totalTime += value;
            if (fieldCount is 3 or 4)
            {
                idleTime += value;
            }

            fieldCount++;
        }

        if (fieldCount < 4)
        {
            totalTime = 0;
            return false;
        }

        busyTime = totalTime - idleTime;
        return true;
    }

    /// <summary>
    /// Parses MemTotal and MemAvailable from /proc/meminfo. Its "kB" unit means KiB.
    /// </summary>
    public static bool TryParseMemInfo(ReadOnlySpan<byte> content, out long totalBytes, out long availableBytes)
    {
        availableBytes = 0;
        return TryReadMemInfoValue(content, "MemTotal:"u8, out totalBytes) &
               TryReadMemInfoValue(content, "MemAvailable:"u8, out availableBytes);
    }

    /// <summary>
    /// Returns the PRETTY_NAME value of /etc/os-release without quotes, or null when it is missing or empty.
    /// </summary>
    public static string? ParseOsReleasePrettyName(string content)
    {
        const string key = "PRETTY_NAME=";
        foreach (var line in content.AsSpan().EnumerateLines())
        {
            if (!line.StartsWith(key, StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[key.Length..].Trim();
            if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            return value.IsEmpty ? null : value.ToString();
        }

        return null;
    }

    /// <summary>
    /// Parses a cgroup v2 memory.max or v1 memory.limit_in_bytes value. "max", non-positive values and limits at or
    /// above the total memory mean no limit, which also covers the page-size dependent v1 sentinel.
    /// </summary>
    public static long? ParseCgroupMemoryLimit(string? content, long? totalMemory)
    {
        if (content is null ||
            !long.TryParse(content.AsSpan().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) ||
            limit <= 0)
        {
            return null;
        }

        return totalMemory is { } total && limit >= total ? null : limit;
    }

    /// <summary>
    /// Parses cgroup v2 cpu.max ("quota period" or "max period") into cores.
    /// </summary>
    public static decimal? ParseCgroupV2CpuMax(string? content)
    {
        if (content is null)
        {
            return null;
        }

        var text = content.AsSpan().Trim();
        var separator = text.IndexOf(' ');
        return separator < 0 ? null : ComputeCores(text[..separator], text[(separator + 1)..]);
    }

    /// <summary>
    /// Parses cgroup v1 cpu.cfs_quota_us and cpu.cfs_period_us into cores. A quota of -1 means no limit.
    /// </summary>
    public static decimal? ParseCgroupV1CpuLimit(string? quota, string? period)
    {
        return quota is null || period is null ? null : ComputeCores(quota.AsSpan().Trim(), period.AsSpan().Trim());
    }

    private static decimal? ComputeCores(ReadOnlySpan<char> quota, ReadOnlySpan<char> period)
    {
        if (!long.TryParse(quota, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quotaValue) || quotaValue <= 0 ||
            !long.TryParse(period, NumberStyles.Integer, CultureInfo.InvariantCulture, out var periodValue) || periodValue <= 0)
        {
            return null;
        }

        return Math.Round((decimal)quotaValue / periodValue, 2);
    }

    private static bool TryReadMemInfoValue(ReadOnlySpan<byte> content, ReadOnlySpan<byte> key, out long bytes)
    {
        bytes = 0;

        var index = content.IndexOf(key);
        if (index < 0 || (index > 0 && content[index - 1] != (byte)'\n'))
        {
            return false;
        }

        var value = content[(index + key.Length)..].TrimStart((byte)' ');
        if (!Utf8Parser.TryParse(value, out long kibibytes, out _))
        {
            return false;
        }

        bytes = kibibytes * 1024;
        return true;
    }
}
