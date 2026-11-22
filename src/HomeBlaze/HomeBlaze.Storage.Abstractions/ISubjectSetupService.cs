namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// Service for creating subjects through UI interaction.
/// Implementation is provided by UI layer (e.g., Blazor dialog).
/// </summary>
public interface ISubjectSetupService
{
    /// <summary>
    /// Prompts the user for new subjects to be stored in the target container and passes each created subject to
    /// <paramref name="addSubjectAsync"/>. The prompt can stay open for further subjects, such as after a subject of a
    /// found device. An implementation rejects a name that <see cref="IStorageContainer.ChildNames"/> of the target
    /// already contains, or that an earlier subject of the same prompt got.
    /// </summary>
    /// <param name="target">The container the new subjects are going to be stored in.</param>
    /// <param name="addSubjectAsync">
    /// Stores a created subject. When it throws, the prompt reports the error and the user can try again.
    /// </param>
    /// <param name="cancellationToken">The cancellation token, which is passed on to <paramref name="addSubjectAsync"/>.</param>
    /// <returns>
    /// A task that completes when the prompt is closed. Closing the prompt while a subject is being stored completes it
    /// before that store has ended.
    /// </returns>
    Task CreateSubjectsAsync(
        IStorageContainer target,
        Func<CreateSubjectResult, CancellationToken, Task> addSubjectAsync,
        CancellationToken cancellationToken);
}
