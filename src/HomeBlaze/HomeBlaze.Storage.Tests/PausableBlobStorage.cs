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
    private string? _blockedOperation;
    private int _callCount;
    private int _openedStreamCount;
    private int _disposedStreamCount;

    /// <summary>The number of calls that have arrived in the storage.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    public int OpenedStreamCount => Volatile.Read(ref _openedStreamCount);

    public int DisposedStreamCount => Volatile.Read(ref _disposedStreamCount);

    /// <summary>
    /// Makes the next call of the operation hang after it has returned its task. The returned task completes
    /// when that call has arrived.
    /// </summary>
    public Task PauseNext(string operation)
    {
        _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pausedOperation = operation;
        return _reached.Task.WaitAsync(Timeout);
    }

    /// <summary>
    /// Makes the next call of the operation block the thread of its caller, as a storage does that works before
    /// it returns its task. The returned task completes when that call has arrived.
    /// </summary>
    public Task BlockNext(string operation)
    {
        _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _blockedOperation = operation;
        return _reached.Task.WaitAsync(Timeout);
    }

    /// <summary>
    /// Lets the call that hangs go on into the storage, as a storage does that responds again.
    /// </summary>
    public void Release() => _released.TrySetResult();

    /// <summary>
    /// Makes the call that hangs fail, as a storage does that gives up later than its caller.
    /// </summary>
    public void FailHangingCall(Exception exception) => _released.TrySetException(exception);

    public async Task<IReadOnlyCollection<Blob>> ListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
    {
        await ArriveAsync(nameof(ListAsync));
        return await inner.ListAsync(options, cancellationToken);
    }

    public async Task WriteAsync(string fullPath, Stream dataStream, bool append = false, CancellationToken cancellationToken = default)
    {
        await ArriveAsync(nameof(WriteAsync));
        await inner.WriteAsync(fullPath, dataStream, append, cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        await ArriveAsync(nameof(OpenReadAsync));
        var stream = await inner.OpenReadAsync(fullPath, cancellationToken);

        // Null for a file that does not exist, although the signature does not say so.
        if (stream is null)
        {
            return null!;
        }

        Interlocked.Increment(ref _openedStreamCount);
        return new PausableStream(stream, this);
    }

    public async Task DeleteAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
    {
        await ArriveAsync(nameof(DeleteAsync));
        await inner.DeleteAsync(fullPaths, cancellationToken);
    }

    public async Task<IReadOnlyCollection<bool>> ExistsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
    {
        await ArriveAsync(nameof(ExistsAsync));
        return await inner.ExistsAsync(fullPaths, cancellationToken);
    }

    public async Task<IReadOnlyCollection<Blob>> GetBlobsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
    {
        await ArriveAsync(nameof(GetBlobsAsync));
        return await inner.GetBlobsAsync(fullPaths, cancellationToken);
    }

    public async Task SetBlobsAsync(IEnumerable<Blob> blobs, CancellationToken cancellationToken = default)
    {
        await ArriveAsync(nameof(SetBlobsAsync));
        await inner.SetBlobsAsync(blobs, cancellationToken);
    }

    public async Task<ITransaction> OpenTransactionAsync()
    {
        await ArriveAsync(nameof(OpenTransactionAsync));
        return await inner.OpenTransactionAsync();
    }

    public void Dispose()
    {
        // Ends a call that is still hanging, so that no task outlives the test. Cancelled and not released:
        // released, it would go on into the storage and write into a directory that the test deletes next.
        _released.TrySetCanceled();
        inner.Dispose();
    }

    // Not asynchronous itself, so a blocked call blocks before the call has returned its task.
    private Task ArriveAsync(string operation)
    {
        Interlocked.Increment(ref _callCount);
        if (_blockedOperation == operation)
        {
            _blockedOperation = null;
            _reached!.TrySetResult();
            _released.Task.GetAwaiter().GetResult();
        }

        return PauseAsync(operation);
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
        private int _isDisposed;

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
            if (disposing && Interlocked.Exchange(ref _isDisposed, 1) == 0)
            {
                Interlocked.Increment(ref storage._disposedStreamCount);
                stream.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
