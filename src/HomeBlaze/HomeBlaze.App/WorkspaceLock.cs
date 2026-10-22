namespace HomeBlaze.App;

/// <summary>
/// An exclusive claim on a data directory, held for as long as the instance uses it.
/// </summary>
/// <remarks>
/// The claim is a file opened without sharing. It stops a second copy of this application from opening the
/// same folder, which would put two SQLite writers, two file watchers and two OPC UA servers on one data set.
/// It does not stop a container instance from using the folder, because the container takes no such lock.
/// </remarks>
public sealed class WorkspaceLock : IDisposable
{
    private const string LockFileName = ".homeblaze-app.lock";

    private readonly FileStream _stream;

    private WorkspaceLock(FileStream stream)
    {
        _stream = stream;
    }

    /// <summary>Tries to claim <paramref name="dataDirectory"/>, creating it when it does not exist.</summary>
    public static bool TryAcquire(string dataDirectory, out WorkspaceLock? workspaceLock)
    {
        workspaceLock = null;

        try
        {
            Directory.CreateDirectory(dataDirectory);

            // DeleteOnClose keeps a crash from leaving a lock file that blocks the next start.
            var stream = new FileStream(
                Path.Combine(dataDirectory, LockFileName),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            workspaceLock = new WorkspaceLock(stream);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose() => _stream.Dispose();
}
