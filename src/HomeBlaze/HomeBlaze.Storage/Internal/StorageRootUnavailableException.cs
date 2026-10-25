using FluentStorage.Blobs;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The root of the storage could not be examined before a call, so the call was not made. The failure of the
/// check is the inner exception.
/// </summary>
internal sealed class StorageRootUnavailableException(IBlobStorage storage, Exception innerException)
    : IOException(innerException.Message, innerException)
{
    /// <summary>The storage whose root failed, to tell it from another storage that the caller works with.</summary>
    public IBlobStorage Storage { get; } = storage;
}
