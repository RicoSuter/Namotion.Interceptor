namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// Extension methods for ISubjectSetupService.
/// </summary>
public static class SubjectCreatorExtensions
{
    /// <summary>
    /// Opens the create dialog and adds the created subject to storage.
    /// </summary>
    /// <param name="subjectSetupService">The setup service.</param>
    /// <param name="storage">The storage the subject is added to.</param>
    /// <param name="existingNames">The names that are already used in the storage, so the dialog can reject a duplicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public static async Task CreateSubjectAndAddToStorageAsync(
        this ISubjectSetupService subjectSetupService,
        IStorageContainer storage,
        IReadOnlyCollection<string> existingNames,
        CancellationToken cancellationToken)
    {
        var result = await subjectSetupService.CreateSubjectAsync(new SubjectSetupRequest(existingNames), cancellationToken);
        if (result?.Name is not { } name)
            return;

        var fileName = $"{name}{FileExtensions.Json}";
        await storage.AddSubjectAsync(fileName, result.Subject, cancellationToken);
    }
}
