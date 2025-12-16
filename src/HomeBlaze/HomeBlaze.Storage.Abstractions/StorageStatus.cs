namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// Status of a storage connection.
/// </summary>
public enum StorageStatus
{
    Disconnected,
    Initializing,
    Connected,
    Error
}
