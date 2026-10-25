namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Reads /proc/stat and /proc/meminfo. Inside a container these describe the host, not the container.
/// </summary>
internal sealed class LinuxSystemMetricsReader : ISystemMetricsReader
{
    // Only read by the polling loop, so the buffer is never shared between threads.
    private readonly byte[] _buffer = new byte[8192];

    public SystemMetricsReading Read()
    {
        var length = ReadFile("/proc/stat");
        if (!LinuxProcParser.TryParseProcStat(_buffer.AsSpan(0, length), out var busyTime, out var totalTime))
        {
            throw new InvalidDataException("/proc/stat has an unexpected format.");
        }

        length = ReadFile("/proc/meminfo");
        if (!LinuxProcParser.TryParseMemInfo(_buffer.AsSpan(0, length), out var totalMemory, out var availableMemory))
        {
            throw new InvalidDataException("/proc/meminfo has an unexpected format.");
        }

        return new SystemMetricsReading(busyTime, totalTime, totalMemory, availableMemory);
    }

    private int ReadFile(string path)
    {
        using var handle = File.OpenHandle(path);

        var length = 0;
        int read;
        while (length < _buffer.Length &&
               (read = RandomAccess.Read(handle, _buffer.AsSpan(length), length)) > 0)
        {
            length += read;
        }

        return length;
    }
}
