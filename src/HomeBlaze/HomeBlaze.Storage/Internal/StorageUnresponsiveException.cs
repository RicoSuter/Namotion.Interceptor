using FluentStorage.Blobs;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// A call into the storage did not complete within <see cref="StorageCallTimeout.Limit"/>, or a call was not
/// started because an earlier one has not returned yet.
/// </summary>
internal sealed class StorageUnresponsiveException(string message, IBlobStorage storage) : TimeoutException(message)
{
    /// <summary>The storage that did not answer, to tell it from another storage that the caller works with.</summary>
    public IBlobStorage Storage { get; } = storage;
}
