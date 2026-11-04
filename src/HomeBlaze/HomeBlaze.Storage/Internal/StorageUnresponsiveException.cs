using FluentStorage.Storage;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// A call into the storage did not complete within <see cref="StorageCallTimeout.Limit"/>, or a call was not
/// started because an earlier one has not returned yet.
/// </summary>
internal sealed class StorageUnresponsiveException(string message, IStore storage, bool isRefusal) : TimeoutException(message)
{
    /// <summary>The storage that did not answer, to tell it from another storage that the caller works with.</summary>
    public IStore Storage { get; } = storage;

    /// <summary>True when the call was not started. The storage tells when it takes calls again.</summary>
    public bool IsRefusal { get; } = isRefusal;
}
