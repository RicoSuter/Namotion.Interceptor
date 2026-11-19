using System.Globalization;

namespace HomeBlaze.Installation;

/// <summary>
/// The installation's start of a run, read from the running file: what was requested and when.
/// </summary>
internal sealed record UpdaterRun(BuildIdentity Requested, DateTimeOffset RequestedAt);

/// <summary>
/// The files shared with the updater sidecar in the root of the data directory. Holds no state; a file that
/// disappears between calls reads as missing, other I/O errors propagate.
/// </summary>
internal sealed class UpdaterFiles
{
    public const string HeartbeatFileName = ".updater-heartbeat";
    public const string RequestFileName = ".update-request";
    public const string RunningFileName = ".update-running";
    public const string ResultFileName = ".update-result";
    public const string LogFileName = ".update-log";

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
        TryDelete(RequestFileName);
        DeleteRunAndResult();

        var temporaryPath = GetPath(RequestFileName + ".tmp");
        File.WriteAllText(temporaryPath, identity.Format());
        File.SetLastWriteTimeUtc(temporaryPath, _timeProvider.GetUtcNow().UtcDateTime);
        File.Move(temporaryPath, GetPath(RequestFileName), overwrite: true);
    }

    /// <summary>
    /// Deletes the request and returns whether it still existed; false means the sidecar took it or nothing was requested.
    /// </summary>
    public bool TryDeleteRequest() => TryDelete(RequestFileName);

    /// <summary>
    /// The run the sidecar took over, its request time being the last write time the request carried; null without one.
    /// </summary>
    public UpdaterRun? ReadRun()
    {
        var lastWrite = GetLastWriteTime(RunningFileName);
        var content = TryReadAllText(RunningFileName);
        return lastWrite is null || content is null
            ? null
            : new UpdaterRun(BuildIdentity.Parse(content), lastWrite.Value);
    }

    /// <summary>
    /// Reads the exit code of compose; false while there is no result. A result that is not a number reads as -1.
    /// </summary>
    public bool TryReadExitCode(out int exitCode)
    {
        var content = TryReadAllText(ResultFileName);
        if (content is null)
        {
            exitCode = 0;
            return false;
        }

        exitCode = int.TryParse(content.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;
        return true;
    }

    public string? ReadLog() => TryReadAllText(LogFileName);

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

    private string? TryReadAllText(string fileName)
    {
        try
        {
            return File.ReadAllText(GetPath(fileName));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private bool TryDelete(string fileName)
    {
        var path = GetPath(fileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
