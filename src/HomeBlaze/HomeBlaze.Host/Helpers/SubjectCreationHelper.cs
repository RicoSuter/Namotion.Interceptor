using System.Text;
using HomeBlaze.Host.Components.Dialogs;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using MudBlazor;
using Namotion.Interceptor;

namespace HomeBlaze.Host.Helpers;

/// <summary>
/// Shared helper for creating subjects in folders and storage.
/// </summary>
public static class SubjectCreationHelper
{
    /// <summary>
    /// Opens the create subject wizard and saves the result to storage.
    /// </summary>
    public static async Task<IInterceptorSubject?> CreateSubjectAsync(
        IDialogService dialogService,
        ConfigurableSubjectSerializer serializer,
        IStorageContainer container,
        string relativePath)
    {
        var result = await CreateSubjectWizard.ShowAsync(dialogService);
        if (result == null)
            return null;

        var fileName = $"{result.Name}.json";
        var fullPath = string.IsNullOrEmpty(relativePath)
            ? fileName
            : Path.Combine(relativePath, fileName);

        var json = serializer.Serialize(result.Subject);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await container.WriteBlobAsync(fullPath, stream, CancellationToken.None);

        return result.Subject;
    }
}
