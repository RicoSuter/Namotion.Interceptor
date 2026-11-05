using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>A root storage that stores nothing; every operation but <see cref="IStorageContainer.Status"/> is not supported.</summary>
public abstract class TestStorageRoot : IStorageContainer, IConfigurable
{
    public abstract StorageStatus Status { get; }

    public IReadOnlyCollection<string> ChildNames => [];

    public Task<BlobMetadata?> GetBlobMetadataAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<Stream> ReadBlobAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task WriteBlobAsync(string path, Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task WriteBlobAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteBlobAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task AddSubjectAsync(string path, IInterceptorSubject subject, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteSubjectAsync(IInterceptorSubject subject, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A root storage that never loads its files, so the startup waits for it forever.</summary>
[InterceptorSubject]
public partial class NeverLoadingStorageRoot : TestStorageRoot
{
    public override StorageStatus Status => StorageStatus.Initializing;
}

/// <summary>A root storage that failed to load its files, so the startup waits for it.</summary>
[InterceptorSubject]
public partial class FailedStorageRoot : TestStorageRoot
{
    public override StorageStatus Status => StorageStatus.Error;
}

/// <summary>A root storage whose status cannot be read, so adding the Authorization subject fails unexpectedly.</summary>
[InterceptorSubject]
public partial class BrokenStorageRoot : TestStorageRoot
{
    public override StorageStatus Status => throw new InvalidOperationException("The storage is broken.");
}
