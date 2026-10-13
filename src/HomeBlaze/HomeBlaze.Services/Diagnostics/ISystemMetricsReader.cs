namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Reads host-wide CPU and memory values. Throws when the platform call fails.
/// </summary>
internal interface ISystemMetricsReader
{
    SystemMetricsReading Read();
}

internal static class SystemMetricsReaderFactory
{
    /// <summary>
    /// Returns the reader for the current OS, or null when the OS is not supported.
    /// </summary>
    public static ISystemMetricsReader? CreateForCurrentPlatform()
    {
        if (OperatingSystem.IsLinux())
        {
            return new LinuxSystemMetricsReader();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsSystemMetricsReader();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOSSystemMetricsReader();
        }

        return null;
    }
}
