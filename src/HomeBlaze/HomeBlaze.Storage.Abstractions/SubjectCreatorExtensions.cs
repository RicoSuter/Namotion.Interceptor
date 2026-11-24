namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// Extension methods for ISubjectSetupService.
/// </summary>
public static class SubjectCreatorExtensions
{
    /// <summary>
    /// Opens the create dialog and adds each created subject to storage as <c>{name}.json</c>. A subject without a
    /// name is not added.
    /// </summary>
    /// <param name="subjectSetupService">The setup service.</param>
    /// <param name="storage">The storage the subjects are added to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public static Task CreateSubjectsAndAddToStorageAsync(
        this ISubjectSetupService subjectSetupService,
        IStorageContainer storage,
        CancellationToken cancellationToken)
    {
        return subjectSetupService.CreateSubjectsAsync(
            storage,
            async (result, addCancellationToken) =>
            {
                if (result.Name is not { } name)
                    return;

                var fileName = $"{name}{FileExtensions.Json}";
                await storage.AddSubjectAsync(fileName, result.Subject, addCancellationToken);
            },
            cancellationToken);
    }
}
