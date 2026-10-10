using System.Collections.Concurrent;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A file subject whose load a test can pause after the content was read and before it is applied,
/// to put other work between the load of a subject and its placement in the hierarchy. A test can also make
/// a load fail, or run code of its own on the storage worker.
/// </summary>
[InterceptorSubject]
[FileExtension(".gated")]
public partial class GatedFile : IStorageFile
{
    private static readonly ConcurrentQueue<Gate> Gates = new();
    private static int _loadCount;
    private static Exception? _nextLoadFailure;
    private static Func<GatedFile, CancellationToken, Task>? _nextLoadCallback;

    public IStorageContainer Storage { get; }

    public string FullPath { get; }

    public string Name { get; }

    public partial long FileSize { get; set; }

    public partial DateTime LastModified { get; set; }

    public partial string? Content { get; set; }

    public GatedFile(IStorageContainer storage, string fullPath)
    {
        Storage = storage;
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
    }

    /// <summary>The number of loads since the last <see cref="Reset"/>.</summary>
    public static int LoadCount => Volatile.Read(ref _loadCount);

    /// <summary>
    /// Makes the next load pause until the returned gate is released. Loads without a gate run through.
    /// </summary>
    public static Gate PauseNextLoad()
    {
        var gate = new Gate();
        Gates.Enqueue(gate);
        return gate;
    }

    /// <summary>
    /// Makes the next load throw the exception, or an <see cref="InvalidOperationException"/> when none is given.
    /// </summary>
    public static void FailNextLoad(Exception? exception = null)
        => Volatile.Write(ref _nextLoadFailure, exception ?? new InvalidOperationException("The load was made to fail."));

    /// <summary>
    /// Makes the next load run the callback after it has read its content, as part of the load.
    /// </summary>
    public static void RunOnNextLoad(Func<GatedFile, CancellationToken, Task> callback)
        => Volatile.Write(ref _nextLoadCallback, callback);

    public static void Reset()
    {
        Gates.Clear();
        Volatile.Write(ref _loadCount, 0);
        Volatile.Write(ref _nextLoadFailure, null);
        Volatile.Write(ref _nextLoadCallback, null);
    }

    public Task<Stream> ReadAsync(CancellationToken cancellationToken)
        => Storage.ReadBlobAsync(FullPath, cancellationToken);

    public Task WriteAsync(Stream content, CancellationToken cancellationToken)
        => Storage.WriteBlobAsync(FullPath, content, cancellationToken);

    public async Task OnFileChangedAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loadCount);
        if (Interlocked.Exchange(ref _nextLoadFailure, null) is { } failure)
        {
            throw failure;
        }

        await using var stream = await ReadAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(cancellationToken);

        if (Interlocked.Exchange(ref _nextLoadCallback, null) is { } callback)
        {
            await callback(this, cancellationToken);
        }

        if (Gates.TryDequeue(out var gate))
        {
            await gate.PauseAsync();
        }

        Content = content;
    }

    public sealed class Gate
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Completes once a load has read its content and waits at the gate.
        /// </summary>
        public Task WhenReachedAsync() => _reached.Task.WaitAsync(Timeout);

        public void Release() => _released.SetResult();

        internal Task PauseAsync()
        {
            _reached.SetResult();
            return _released.Task.WaitAsync(Timeout);
        }
    }
}
