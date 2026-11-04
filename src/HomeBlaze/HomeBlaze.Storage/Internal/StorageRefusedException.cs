namespace HomeBlaze.Storage.Internal;

/// <summary>
/// A refusal of the storage whose message tells the caller why, such as a storage that is not connected, an item that
/// is no longer in it, or a metadata file that cannot be read.
/// </summary>
internal sealed class StorageRefusedException(string message) : InvalidOperationException(message);
