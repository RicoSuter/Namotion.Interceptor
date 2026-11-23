using System.Runtime.InteropServices;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// Returns freed native heap to the operating system on Linux with glibc, which otherwise keeps it in its arenas,
/// so memory SQLite frees when a connection closes stays in the process's working set. A no-op elsewhere.
/// </summary>
internal static partial class NativeHeap
{
    // musl, macOS and Windows have no glibc to load; the first failed call turns trimming off for the process.
    private static volatile bool _isUnavailable = !OperatingSystem.IsLinux();

    // Whether trimming is still on: false off Linux and after a failed load.
    public static bool IsAvailable => !_isUnavailable;

    public static void Trim()
    {
        if (_isUnavailable)
        {
            return;
        }

        try
        {
            MallocTrim(0);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            _isUnavailable = true;
        }
    }

    [LibraryImport("libc.so.6", EntryPoint = "malloc_trim")]
    private static partial int MallocTrim(nuint pad);
}
