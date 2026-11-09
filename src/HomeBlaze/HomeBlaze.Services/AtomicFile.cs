using System.Text;

namespace HomeBlaze.Services;

/// <summary>
/// Replaces a file so that readers see the old or the new content and never a part of it, also after a power loss: the
/// content is written to a temporary file next to it, named <c>{path}.{32 lowercase hex digits}.tmp</c>, flushed to the
/// disk and moved over the file. The directory of the file is created when it is missing. A temporary file is deleted
/// when the write fails; one that a crash left behind stays.
/// </summary>
public static class AtomicFile
{
    /// <summary>Replaces the file with <paramref name="content"/>.</summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> content)
    {
        var temporaryPath = CreateTemporaryPath(path);
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);

                // Before the move, since a file system may persist the move before the content, which leaves an empty
                // file after a power loss.
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    /// <summary>Replaces the file with <paramref name="content"/>.</summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var temporaryPath = CreateTemporaryPath(path);
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
            {
                await stream.WriteAsync(content, cancellationToken);
                stream.Flush(flushToDisk: true); // Before the move, see WriteAllBytes.
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    /// <summary>Replaces the file with <paramref name="content"/> encoded as UTF-8 without a byte order mark.</summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public static Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken) =>
        WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(content), cancellationToken);

    private static string CreateTemporaryPath(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
