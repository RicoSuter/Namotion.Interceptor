using FluentStorage;
using FluentStorage.Blobs;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A storage whose next call of one operation can be made to hang, to test what a stalled source does.
/// </summary>
internal sealed class PausableBlobStorage(IBlobStorage inner) : IBlobStorage
{
    /// <summary>The operation of reading from a stream that <see cref="OpenReadAsync"/> returned.</summary>
    public const string ReadOperation = "Read";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _reached;
    private string? _pausedOperation;

    /// <summary>
    /// Makes the next call of the operation hang. The returned task completes when that call has arrived.
    /// </summary>
    public Task PauseNext(string operation)
    {
        _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pausedOperation = operation;
        return _reached.Task.WaitAsync(Timeout);
    }

    public async Task<IReadOnlyCollection<Blob>> ListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
    {
        await PauseAsync(nameof(ListAsync));
        return await inner.ListAsync(options, cancellationToken);
    }

    public async Task WriteAsync(string fullPath, Stream dataStream, bool append = false, CancellationToken cancellationToken = default)
    {
        await PauseAsync(nameof(WriteAsync));
        await inner.WriteAsync(fullPath, dataStream, append, cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        await PauseAsync(nameof(OpenReadAsync));
        var stream = await inner.OpenReadAsync(fullPath, cancellationToken);

        // Null for a file that does not exist, although the signature does not say so.
        return stream is null ? null! : new PausableStream(stream, this);
    }

    public Task DeleteAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
        => inner.DeleteAsync(fullPaths, cancellationToken);

    public Task<IReadOnlyCollection<bool>> ExistsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
        => inner.ExistsAsync(fullPaths, cancellationToken);

    public Task<IReadOnlyCollection<Blob>> GetBlobsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
        => inner.GetBlobsAsync(fullPaths, cancellationToken);

    public Task SetBlobsAsync(IEnumerable<Blob> blobs, CancellationToken cancellationToken = default)
        => inner.SetBlobsAsync(blobs, cancellationToken);

    public Task<ITransaction> OpenTransactionAsync()
        => inner.OpenTransactionAsync();

    public void Dispose()
    {
        // Ends a call that is still hanging, so that no task outlives the test. Cancelled and not released:
        // released, it would go on into the storage and write into a directory that the test deletes next.
        _released.TrySetCanceled();
        inner.Dispose();
    }

    private async Task PauseAsync(string operation)
    {
        if (_pausedOperation == operation)
        {
            _pausedOperation = null;
            _reached!.TrySetResult();
            await _released.Task;
        }
    }

    private sealed class PausableStream(Stream stream, PausableBlobStorage storage) : Stream
    {
        public override bool CanRead => stream.CanRead;

        public override bool CanSeek => stream.CanSeek;

        public override bool CanWrite => false;

        public override long Length => stream.Length;

        public override long Position
        {
            get => stream.Position;
            set => stream.Position = value;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await storage.PauseAsync(ReadOperation);
            return await stream.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);

        public override void Flush()
        {
            // Nothing is written through this stream.
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                stream.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
