namespace HomeBlaze.Storage.Internal;

/// <summary>
/// A call into the storage did not complete within <see cref="StorageCallTimeout.Limit"/>, or was not started
/// because an earlier call has not returned yet.
/// </summary>
internal sealed class StorageUnresponsiveException(string message) : TimeoutException(message);
