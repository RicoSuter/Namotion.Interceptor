using System.Collections.Concurrent;
using FluentStorage.Model;
using FluentStorage.Storage;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A storage whose next call of one operation can be made to hang, to test what a stalled source does.
/// </summary>
internal sealed class PausableStore(IStore inner) : StoreBase
{
    /// <summary>The operation of reading from a stream that <see cref="OpenRead"/> returned.</summary>
    public const string ReadOperation = "Read";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, int> _callCounts = new();
    private TaskCompletionSource? _reached;
    private string? _pausedOperation;
    private string? _blockedOperation;
    private Action? _afterNextListing;
    private Exception? _deleteFailure;
    private int _pausedStreamNumber;
    private int _callCount;
    private int _openedStreamCount;
    private int _disposedStreamCount;

    /// <summary>The number of calls that have arrived in the storage.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>The number of calls of one operation that have arrived in the storage.</summary>
    public int GetCallCount(string operation) => _callCounts.GetValueOrDefault(operation);

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
    /// Does what <see cref="PauseNext"/> does after an earlier call was released: the call hangs until
    /// <see cref="Release"/> is called once more.
    /// </summary>
    public Task PauseNextAfterRelease(string operation)
    {
        _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return PauseNext(operation);
    }

    /// <summary>
    /// Runs the action once, when the next listing has been read from the storage and before its caller has it.
    /// </summary>
    public void RunAfterNextListing(Action action) => Volatile.Write(ref _afterNextListing, action);

    /// <summary>
    /// Makes the first read from one of the streams that <see cref="OpenRead"/> returns from now on hang.
    /// The returned task completes when that read has arrived.
    /// </summary>
    /// <param name="streamNumber">Which of the streams opened from now on, starting at one.</param>
    public Task PauseReadOfStream(int streamNumber)
    {
        _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pausedStreamNumber = OpenedStreamCount + streamNumber;
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

    /// <summary>
    /// Makes the next delete fail after it has deleted, as a storage does whose answer is lost.
    /// </summary>
    public void FailNextDeleteAfterItRan(Exception exception) => Volatile.Write(ref _deleteFailure, exception);

    public override async Task<List<StoreObject>> ListObjects(StorageListOptions? options = null, CancellationToken cancellationToken = default)
    {
        await ArriveAsync(nameof(ListObjects));
        var blobs = await inner.ListObjects(options, cancellationToken);
        Interlocked.Exchange(ref _afterNextListing, null)?.Invoke();
        return blobs;
    }

    public override async Task SetObject(string objectPath, Stream sourceStream, bool append, CancellationToken cancellationToken = default)
    {
        await ArriveAsync(nameof(SetObject));
        await inner.SetObject(objectPath, sourceStream, append, cancellationToken);
    }

    public override async Task<Stream> OpenRead(string objectPath, CancellationToken cancellationToken = default)
    {
        await ArriveAsync(nameof(OpenRead));
        var stream = await inner.OpenRead(objectPath, cancellationToken);

        // Null for a file that does not exist, although the signature does not say so.
        if (stream is null)
        {
            return null!;
        }

        return new PausableStream(stream, this, Interlocked.Increment(ref _openedStreamCount));
    }

    public override async Task DeleteObject(string objectPath, CancellationToken cancellationToken = default)
    {
        await ArriveAsync(nameof(DeleteObject));
        await inner.DeleteObject(objectPath, cancellationToken);
        if (Interlocked.Exchange(ref _deleteFailure, null) is { } failure)
        {
            throw failure;
        }
    }

    public override async Task<bool> ObjectExists(string objectPath, CancellationToken cancellationToken = default)
    {
        await ArriveAsync(nameof(ObjectExists));
        return await inner.ObjectExists(objectPath, cancellationToken);
    }

    public override async Task<StoreObject> GetObjectInfo(string objectPath, CancellationToken cancellationToken = default)
    {
        await ArriveAsync(nameof(GetObjectInfo));
        return await inner.GetObjectInfo(objectPath, cancellationToken);
    }

    public override void Dispose()
    {
        // Ends a call that is still hanging, so that no task outlives the test. Canceled and not released:
        // released, it would go on into the storage and write into a directory that the test deletes next.
        _released.TrySetCanceled();
        inner.Dispose();
    }

    // Not asynchronous itself, so a blocked call blocks before the call has returned its task.
    private Task ArriveAsync(string operation)
    {
        Interlocked.Increment(ref _callCount);
        _callCounts.AddOrUpdate(operation, 1, static (_, count) => count + 1);
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

    private sealed class PausableStream(Stream stream, PausableStore storage, int number) : Stream
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
            if (storage._pausedStreamNumber == number)
            {
                storage._pausedStreamNumber = 0;
                storage._reached!.TrySetResult();
                await storage._released.Task;
            }

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
