namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// Service for creating subjects through UI interaction.
/// Implementation is provided by UI layer (e.g., Blazor dialog).
/// </summary>
public interface ISubjectSetupService
{
    /// <summary>
    /// Prompts the user for a new subject to be stored in the target container. An implementation rejects a
    /// name that <see cref="IStorageContainer.ChildNames"/> of the target already contains.
    /// </summary>
    /// <param name="target">The container the new subject is going to be stored in.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The created subject and its name, or null if cancelled. A result without a name is not added to storage
    /// by <see cref="SubjectCreatorExtensions.CreateSubjectAndAddToStorageAsync"/>.
    /// </returns>
    Task<CreateSubjectResult?> CreateSubjectAsync(IStorageContainer target, CancellationToken cancellationToken);
}
