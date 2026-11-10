using HomeBlaze.Services;
using MudBlazor;
using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>
/// Extension methods for components that persist a configuration change and report one that was not saved.
/// </summary>
internal static class ConfigurationPersistenceExtensions
{
    /// <summary>
    /// Writes the configuration that contains the subject through its configuration writer, see
    /// <see cref="RootManagerExtensions.GetConfigurationWriter"/>, and warns when the writer wrote nothing, in
    /// which case the change is applied but not saved. A write that fails is shown as an error and leaves the change
    /// applied but not saved; nothing is thrown.
    /// </summary>
    /// <returns>true if the configuration was written.</returns>
    public static async Task<bool> WriteConfigurationOrWarnAsync(
        this RootManager rootManager,
        IInterceptorSubject subject,
        ISnackbar snackbar,
        CancellationToken cancellationToken)
    {
        bool isWritten;
        try
        {
            var configurationWriter = rootManager.GetConfigurationWriter(subject);
            isWritten = await configurationWriter.WriteConfigurationAsync(subject, cancellationToken);
        }
        catch (Exception exception)
        {
            // Called from event handlers of the UI, which a failure would end together with the circuit.
            snackbar.AddConfigurationSaveError(exception.Message);
            return false;
        }

        if (!isWritten)
        {
            snackbar.AddConfigurationNotSavedWarning();
        }

        return isWritten;
    }

    /// <summary>
    /// Shows that a configuration could not be saved, with the reason.
    /// </summary>
    public static void AddConfigurationSaveError(this ISnackbar snackbar, string message)
    {
        snackbar.Add($"Error saving configuration: {message}", Severity.Error);
    }

    /// <summary>
    /// Warns that a configuration change is applied but was not saved.
    /// </summary>
    public static void AddConfigurationNotSavedWarning(this ISnackbar snackbar)
    {
        snackbar.Add("Configuration applied but not saved. It will be lost on restart.", Severity.Warning);
    }
}
