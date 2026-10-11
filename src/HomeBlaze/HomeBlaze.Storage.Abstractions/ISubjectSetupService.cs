namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// Service for creating subjects through UI interaction.
/// Implementation is provided by UI layer (e.g., Blazor dialog).
/// </summary>
public interface ISubjectSetupService
{
    /// <summary>
    /// Prompts the user to create a new subject. An implementation rejects a name that equals one of the
    /// request's existing names.
    /// </summary>
    /// <returns>
    /// The created subject and its name, or null if cancelled. A result without a name is not added to storage
    /// by <see cref="SubjectCreatorExtensions.CreateSubjectAndAddToStorageAsync"/>.
    /// </returns>
    Task<CreateSubjectResult?> CreateSubjectAsync(SubjectSetupRequest request, CancellationToken cancellationToken);
}
