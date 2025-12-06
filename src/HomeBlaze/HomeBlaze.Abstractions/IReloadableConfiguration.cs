namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for subjects that can reload their configuration from external sources.
/// Implement this to control how configuration updates are applied without
/// recreating the entire subject (preserves object references and runtime state).
/// </summary>
public interface IReloadableConfiguration
{
    /// <summary>
    /// Reloads configuration from the provided JSON.
    /// Called when the backing file changes externally.
    /// Only [Configuration] properties should be updated.
    /// </summary>
    /// <param name="json">The new JSON content from the file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task that completes when reload is finished.</returns>
    Task ReloadConfigurationAsync(string json, CancellationToken cancellationToken = default);
}
