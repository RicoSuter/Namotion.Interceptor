namespace HomeBlaze.Abstractions;

/// <summary>
/// Marker interface for subjects that can be persisted to and loaded from storage.
/// All subjects that should be serializable to JSON must implement this interface.
/// </summary>
public interface IPersistentSubject
{
    /// <summary>
    /// Called after configuration has been reloaded from storage.
    /// For JSON-based subjects: properties have already been updated by the serializer.
    /// For file-based subjects: reload content from the file system.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task that completes when reload handling is finished.</returns>
    Task ReloadAsync(CancellationToken cancellationToken = default);
}
