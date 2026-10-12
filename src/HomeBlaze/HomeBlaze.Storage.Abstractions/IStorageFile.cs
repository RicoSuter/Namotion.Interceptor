namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// Interface for file subjects in storage.
/// </summary>
public interface IStorageFile
{
    /// <summary>
    /// Gets the file name including extension.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the full path relative to the storage container.
    /// </summary>
    string FullPath { get; }

    /// <summary>
    /// Gets the storage container this file belongs to.
    /// </summary>
    IStorageContainer Storage { get; }

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    long FileSize { get; set; }

    /// <summary>
    /// Gets or sets the last modification time (UTC).
    /// </summary>
    DateTime LastModified { get; set; }

    /// <summary>
    /// Reads the file content.
    /// </summary>
    Task<Stream> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes content to the file.
    /// </summary>
    Task WriteAsync(Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Called by the storage when the file is new or its content has changed. The implementation loads the
    /// content or does nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A storage can call this on the worker that changes its hierarchy, which waits for the returned task.
    /// The implementation must not call an operation that changes that storage
    /// (<see cref="IStorageContainer.WriteBlobAsync"/>, <see cref="IStorageContainer.DeleteBlobAsync"/>,
    /// <see cref="IStorageContainer.AddSubjectAsync"/>, <see cref="IStorageContainer.DeleteSubjectAsync"/>,
    /// saving configuration): the storage refuses such a call, also from a task or a timer that the
    /// implementation starts and does not await. A refused call does not throw where it is made. It returns
    /// a task that has failed with an <see cref="InvalidOperationException"/>, and nothing is written, so code
    /// that does not await that task does not learn that its write did not happen.
    /// </para>
    /// <para>
    /// To change the storage from here, make the call in a task that is started inside
    /// <see cref="ExecutionContext.SuppressFlow"/>, and do not wait for that task:
    /// <c>using (ExecutionContext.SuppressFlow()) { _ = Task.Run(() => Storage.WriteBlobAsync(path, content, CancellationToken.None)); }</c>.
    /// The call then runs once this method has returned and the storage has finished the work that called it.
    /// Only work that is started inside the scope runs without the execution flow: a call that is made
    /// directly inside it is still refused.
    /// </para>
    /// <para>
    /// The implementation may read from the storage, and a read of a stream that it does is its own to limit:
    /// the storage does not end one that stalls.
    /// </para>
    /// </remarks>
    Task OnFileChangedAsync(CancellationToken cancellationToken);
}
