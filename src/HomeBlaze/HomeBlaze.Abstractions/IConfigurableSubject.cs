using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for subjects with [Configuration] properties that can be persisted.
/// </summary>
[SubjectAbstraction]
[Description("Subject with persistable configuration properties.")]
public interface IConfigurableSubject
{
    /// <summary>
    /// Called after configuration properties have been updated.
    /// Implementations should apply any side effects from the new configuration.
    /// </summary>
    Task ApplyConfigurationAsync(CancellationToken cancellationToken);
}
