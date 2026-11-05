using HomeBlaze.Components.Abstractions;
using HomeBlaze.Components.Abstractions.Access;
using HomeBlaze.Authorization.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MudBlazor;
using Namotion.Interceptor;

namespace Namotion.Devices.Sonos.HomeBlaze;

/// <summary>
/// A widget that runs operations of Sonos subjects for the viewer and renders again when what the viewer may do changes.
/// </summary>
public abstract class SonosWidgetBase : PermissionsComponentBase, ISubjectComponent
{
    [Inject]
    private IServiceProvider ServiceProvider { get; set; } = null!;

    [Inject]
    private IDialogService DialogService { get; set; } = null!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = null!;

    [Inject]
    private ILogger<SonosWidgetBase> Logger { get; set; } = null!;

    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    /// <summary>Gets whether the viewer may run the operation of the subject.</summary>
    protected bool CanRun(IInterceptorSubject subject, string methodName) => Permissions.CanInvoke(subject, methodName);

    /// <summary>
    /// Runs the operation of the subject when the viewer may; shows a refusal in a message box and a failure in a snackbar.
    /// Never throws: what fails besides the operation, such as the check or the message box, is logged.
    /// </summary>
    protected async Task RunAsync(IInterceptorSubject subject, string methodName, params object?[] parameters)
    {
        try
        {
            // Navigating away must not abort a command the speaker has already started.
            var invocation = await Permissions.TryInvokeAsync(subject, methodName, parameters, ServiceProvider, CancellationToken.None);
            if (invocation.Outcome == PermittedOperationOutcome.Denied)
            {
                await DialogService.ShowMessageBoxAsync(
                    new MessageBoxOptions { Title = AccessDeniedTexts.Title, Message = AccessDeniedTexts.Invoke, YesText = "OK" });
            }
            else if (invocation.Outcome == PermittedOperationOutcome.Failed)
            {
                Snackbar.Add($"Sonos: {invocation.Exception!.Message}", Severity.Error);
            }
        }
        catch (Exception exception)
        {
            // An uncaught exception from a UI event handler ends the Blazor circuit.
            Logger.LogWarning(exception, "Failed to run the Sonos operation {MethodName}.", methodName);
        }
    }
}
