namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// Service for creating subjects through UI interaction.
/// Implementation is provided by UI layer (e.g., Blazor dialog).
/// </summary>
public interface ISubjectSetupService
{
    /// <summary>
    /// Prompts user to create a new subject.
    /// </summary>
    /// <returns>The created subject and name, or null if cancelled</returns>
    Task<CreateSubjectResult?> CreateSubjectAsync(CancellationToken cancellationToken);
}
