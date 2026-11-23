using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// Ends the storages of a test before the directories they work on are deleted.
/// </summary>
internal static class StorageCleanup
{
    /// <summary>
    /// Disposes the storages and completes once none of them works on its files any more: the work item that each
    /// worker was running has finished, and every call into a storage that was given up has returned.
    /// </summary>
    public static async Task DisposeAsync(IReadOnlyCollection<FluentStorageContainer> storages)
    {
        // All ended before the first wait: a work item of one storage that waits for another storage ends only
        // once that one is ended as well.
        var workersStopped = storages.Select(storage => storage.DisposeAndWaitForWorkerAsync()).ToList();
        await Task.WhenAll(workersStopped).WaitAsync(TestTimeouts.Default);

        foreach (var storage in storages)
        {
            await AsyncTestHelpers.WaitUntilAsync(() => !storage.IsStorageUnresponsive, message: "A call into the storage that was given up has not returned.");
        }
    }

    /// <summary>Deletes the directory with everything in it, once nothing holds a file of it open.</summary>
    public static Task DeleteAsync(DirectoryInfo directory)
        // Retried also after DisposeAsync: a read that the end of a connection canceled is given up, not awaited,
        // and keeps its file open until the read returns. Windows does not delete an open file.
        => AsyncTestHelpers.WaitUntilAsync(
            () => TryDelete(directory),
            () => $"The directory '{directory.FullName}' could not be deleted.");

    private static bool TryDelete(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is (IOException and not DirectoryNotFoundException) or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
