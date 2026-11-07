using HomeBlaze.Storage.Abstractions;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Blazor.Tests;

/// <summary>A storage that holds the content of one file and counts its reads.</summary>
internal sealed class FakeStorageContainer(byte[]? content) : IStorageContainer
{
    public StorageStatus Status { get; init; } = StorageStatus.Connected;

    /// <summary>Thrown by a read instead of returning the content.</summary>
    public Exception? ReadException { get; init; }

    public int ReadCount { get; private set; }

    public IReadOnlyCollection<string> ChildNames => [];

    public Task<Stream> ReadBlobAsync(string path, CancellationToken cancellationToken)
    {
        ReadCount++;
        if (ReadException is not null)
        {
            return Task.FromException<Stream>(ReadException);
        }

        // A storage can answer a file that is gone with no stream.
        return Task.FromResult(content is null ? null! : (Stream)new MemoryStream(content));
    }

    public Task<BlobMetadata?> GetBlobMetadataAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task WriteBlobAsync(string path, Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task WriteBlobAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteBlobAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task AddSubjectAsync(string path, IInterceptorSubject subject, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteSubjectAsync(IInterceptorSubject subject, CancellationToken cancellationToken) => throw new NotSupportedException();
}
