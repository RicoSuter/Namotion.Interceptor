using System.Globalization;
using System.Text;

namespace HomeBlaze.Installation;

/// <summary>
/// A run the updater took over: the requested build identity and the request time.
/// </summary>
internal sealed record UpdaterRun(BuildIdentity Requested, DateTimeOffset RequestedAt);

/// <summary>
/// The files shared with the updater sidecar in the root of the data directory. Holds no state; a file that
/// disappears between calls reads as missing, other I/O errors propagate.
/// </summary>
internal sealed class UpdaterFiles
{
    /// <summary>Touched by the updater while it runs.</summary>
    public const string HeartbeatFileName = ".updater-heartbeat";

    /// <summary>Written by HomeBlaze to ask for an install.</summary>
    public const string RequestFileName = ".update-request";

    /// <summary>The request, renamed by the updater when it takes it.</summary>
    public const string RunningFileName = ".update-running";

    /// <summary>Written by the updater with the exit code of compose.</summary>
    public const string ResultFileName = ".update-result";

    /// <summary>Written by the updater with the output of compose.</summary>
    public const string LogFileName = ".update-log";

    private const int MaximumFileLength = 4 * 1024;
    private const int MaximumLogLength = 64 * 1024;

    private readonly string _directory;
    private readonly TimeProvider _timeProvider;

    public UpdaterFiles(string directory, TimeProvider timeProvider)
    {
        _directory = directory;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// The time since the sidecar last touched the heartbeat, at least zero; null without a heartbeat.
    /// </summary>
    public TimeSpan? GetHeartbeatAge()
    {
        var lastWrite = GetLastWriteTime(HeartbeatFileName);
        if (lastWrite is null)
        {
            return null;
        }

        var age = _timeProvider.GetUtcNow() - lastWrite.Value;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }

    /// <summary>
    /// Deletes leftovers of an earlier install and writes the request through a rename, stamped with the current time,
    /// so the sidecar never sees a partial request and the request time follows the installation's clock.
    /// </summary>
    public void WriteRequest(BuildIdentity identity)
    {
        TryDeleteRequest();
        DeleteRunAndResult();

        var temporaryPath = GetPath(RequestFileName + ".tmp");
        File.WriteAllText(temporaryPath, identity.Format());
        File.SetLastWriteTimeUtc(temporaryPath, _timeProvider.GetUtcNow().UtcDateTime);
        File.Move(temporaryPath, GetPath(RequestFileName), overwrite: true);
    }

    /// <summary>
    /// Deletes the request and returns whether this call removed it; false means the updater took it first or nothing was requested.
    /// </summary>
    public bool TryDeleteRequest()
    {
        // The rename makes the sidecar's concurrent mv and this delete mutually exclusive, so the result is exact; do not simplify to File.Delete.
        var claimedPath = GetPath(RequestFileName + ".claimed");
        try
        {
            File.Move(GetPath(RequestFileName), claimedPath, overwrite: true);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        File.Delete(claimedPath);
        return true;
    }

    /// <summary>
    /// The run the sidecar took over, its request time being the last write time the request carried; null without one.
    /// </summary>
    public UpdaterRun? ReadRun()
    {
        var lastWrite = GetLastWriteTime(RunningFileName);
        var content = TryReadStart(RunningFileName);
        return lastWrite is null || content is null
            ? null
            : new UpdaterRun(BuildIdentity.Parse(content), lastWrite.Value);
    }

    /// <summary>
    /// Reads the exit code of compose; false while there is no result. A result that is not a number reads as -1.
    /// </summary>
    public bool TryReadExitCode(out int exitCode)
    {
        var content = TryReadStart(ResultFileName);
        if (content is null)
        {
            exitCode = 0;
            return false;
        }

        exitCode = int.TryParse(content.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;
        return true;
    }

    /// <summary>
    /// The output of compose, at most its last 64 KB starting at a full line; null without a log.
    /// </summary>
    public string? ReadLog()
    {
        try
        {
            using var stream = OpenRead(LogFileName);
            if (stream.Length <= MaximumLogLength)
            {
                return ReadText(stream, (int)stream.Length);
            }

            // One byte more than the limit tells whether the cut fell on a line start, so no full line is dropped.
            stream.Seek(-(MaximumLogLength + 1), SeekOrigin.End);
            var buffer = new byte[MaximumLogLength + 1];
            var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var lineStart = Array.IndexOf(buffer, (byte)'\n', 0, length) + 1;
            return Encoding.UTF8.GetString(buffer, lineStart, length - lineStart);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void DeleteRunAndResult()
    {
        TryDelete(RunningFileName);
        TryDelete(ResultFileName);
    }

    private string GetPath(string fileName) => Path.Combine(_directory, fileName);

    private DateTimeOffset? GetLastWriteTime(string fileName)
    {
        var file = new FileInfo(GetPath(fileName));
        return file.Exists ? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) : null;
    }

    private string? TryReadStart(string fileName)
    {
        try
        {
            using var stream = OpenRead(fileName);
            return ReadText(stream, (int)Math.Min(stream.Length, MaximumFileLength));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    // The updater may replace or remove a file while it is open, which must not fail the read or the updater.
    private FileStream OpenRead(string fileName) =>
        new(GetPath(fileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static string ReadText(FileStream stream, int maximumLength)
    {
        var buffer = new byte[maximumLength];
        var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private void TryDelete(string fileName)
    {
        try
        {
            File.Delete(GetPath(fileName));
        }
        catch (DirectoryNotFoundException)
        {
            // The data directory itself is gone, so the file is too.
        }
    }
}
