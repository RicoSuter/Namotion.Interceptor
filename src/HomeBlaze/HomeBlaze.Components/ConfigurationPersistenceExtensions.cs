using HomeBlaze.Services;
using MudBlazor;
using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>
/// Extension methods for components that persist a configuration change and report one that was not saved.
/// </summary>
public static class ConfigurationPersistenceExtensions
{
    /// <summary>
    /// Writes the configuration that contains the subject through the nearest configuration writer among its
    /// parents, else the root manager, and warns when the writer wrote nothing, in which case the change is
    /// applied but not saved.
    /// </summary>
    /// <returns>true if the configuration was written.</returns>
    public static async Task<bool> WriteConfigurationOrWarnAsync(
        this RootManager rootManager,
        IInterceptorSubject subject,
        ISnackbar snackbar,
        CancellationToken cancellationToken)
    {
        var configurationWriter = rootManager.GetConfigurationWriter(subject);
        var isWritten = await configurationWriter.WriteConfigurationAsync(subject, cancellationToken);
        if (!isWritten)
        {
            snackbar.AddConfigurationNotSavedWarning();
        }

        return isWritten;
    }

    /// <summary>
    /// Warns that a configuration change is applied but was not saved.
    /// </summary>
    public static void AddConfigurationNotSavedWarning(this ISnackbar snackbar)
    {
        snackbar.Add("Configuration applied but not saved. It will be lost on restart.", Severity.Warning);
    }
}
