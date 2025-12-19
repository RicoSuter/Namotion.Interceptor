namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for subjects with [Configuration] properties that can be persisted.
/// </summary>
public interface IConfigurableSubject
{
    /// <summary>
    /// Called after configuration properties have been updated.
    /// Implementations should apply any side effects from the new configuration.
    /// </summary>
    Task ApplyConfigurationAsync(CancellationToken cancellationToken);
}
