namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// Extension methods for ISubjectSetupService.
/// </summary>
public static class SubjectCreatorExtensions
{
    /// <summary>
    /// Opens the create wizard and adds the created subject to storage.
    /// </summary>
    public static async Task CreateSubjectAndAddToStorageAsync(
        this ISubjectSetupService subjectSetupService,
        IStorageContainer storage,
        CancellationToken cancellationToken)
    {
        var result = await subjectSetupService.CreateSubjectAsync(cancellationToken);
        if (result == null)
            return;

        var fileName = $"{result.Name}{FileExtensions.Json}";
        await storage.AddSubjectAsync(fileName, result.Subject, cancellationToken);
    }
}
