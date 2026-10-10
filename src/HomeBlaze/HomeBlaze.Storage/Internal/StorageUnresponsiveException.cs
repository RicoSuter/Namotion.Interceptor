namespace HomeBlaze.Storage.Internal;

/// <summary>
/// A call into the storage, or the reading of a stream it returned, did not complete within
/// <see cref="StorageCallTimeout.Limit"/>, or a call was not started because an earlier one has not returned yet.
/// </summary>
internal sealed class StorageUnresponsiveException(string message) : TimeoutException(message);
