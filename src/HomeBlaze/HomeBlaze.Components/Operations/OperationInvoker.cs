using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Components.Dialogs;
using MudBlazor;

namespace HomeBlaze.Components.Operations;

/// <summary>
/// Runs an operation or query from the UI: asks for its parameters or a confirmation, invokes it, and shows its
/// result or error.
/// </summary>
public static class OperationInvoker
{
    /// <summary>
    /// Gets the icon name of a method: its own icon when set, else "Search" for a query and "PlayArrow" for an
    /// operation.
    /// </summary>
    public static string GetIconName(MethodMetadata method) =>
        !string.IsNullOrEmpty(method.Icon) ? method.Icon : method.Kind == MethodKind.Query ? "Search" : "PlayArrow";

    /// <summary>
    /// Gets what the user is asked before the method runs: the parameters when one of them requires input, else
    /// a confirmation when the method requires one.
    /// </summary>
    internal static OperationPrompt GetPrompt(MethodMetadata method)
    {
        foreach (var parameter in method.Parameters)
        {
            if (parameter.RequiresInput)
            {
                return OperationPrompt.Parameters;
            }
        }

        return method.RequiresConfirmation ? OperationPrompt.Confirmation : OperationPrompt.None;
    }

    /// <summary>
    /// Gets the values of the parameters that require input, keyed by name, from the dialog result, which lists them in
    /// their order.
    /// </summary>
    internal static Dictionary<string, object?> GetParameterValues(MethodMetadata method, object?[]? parameters)
    {
        var values = new Dictionary<string, object?>();
        var index = 0;
        foreach (var parameter in method.Parameters)
        {
            if (parameter.RequiresInput)
            {
                values[parameter.Name] = parameters is not null && index < parameters.Length ? parameters[index] : null;
                index++;
            }
        }

        return values;
    }

    /// <summary>
    /// Shows the prompt of the method, invokes it with <see cref="CancellationToken.None"/>, and shows the result
    /// dialog for a result that is not null or the error dialog for an exception, an <see cref="OperationCanceledException"/>
    /// included. Never throws for a failing method.
    /// </summary>
    /// <remarks>
    /// Exceptions thrown by the dialog service itself propagate to the caller.
    /// </remarks>
    /// <param name="dialogService">The dialog service of the caller.</param>
    /// <param name="serviceProvider">Resolves the parameters of the method that come from services.</param>
    /// <param name="method">The operation or query.</param>
    /// <param name="lifetime">
    /// Canceled when the caller goes away. From then on no dialog is shown and a method that has not started is
    /// not started. A started method is not canceled.
    /// </param>
    /// <param name="isRunningChanged">
    /// Called with true right before the method starts and with false once it has returned or thrown, before a result
    /// or error dialog shows. Not called when the method does not start.
    /// </param>
    /// <param name="initialValues">
    /// The values that prefill the parameter dialog, keyed by parameter name; only parameters entered in one field use them.
    /// </param>
    public static async Task<OperationInvocationResult> InvokeAsync(
        IDialogService dialogService,
        IServiceProvider serviceProvider,
        MethodMetadata method,
        CancellationToken lifetime,
        Action<bool>? isRunningChanged = null,
        IReadOnlyDictionary<string, object?>? initialValues = null)
    {
        if (lifetime.IsCancellationRequested)
        {
            return new OperationInvocationResult(OperationOutcome.Canceled, null, null);
        }

        object?[]? parameters = null;
        IReadOnlyDictionary<string, object?>? parameterValues = null;
        switch (GetPrompt(method))
        {
            case OperationPrompt.Parameters:
            {
                var dialog = await MethodParameterDialog.ShowAsync(dialogService, method, initialValues);
                var dialogResult = await dialog.Result;
                if (dialogResult is null || dialogResult.Canceled)
                {
                    return new OperationInvocationResult(OperationOutcome.Canceled, null, null);
                }

                parameters = dialogResult.Data as object?[];
                parameterValues = GetParameterValues(method, parameters);
                break;
            }
            case OperationPrompt.Confirmation:
            {
                var dialog = await MethodConfirmationDialog.ShowAsync(dialogService, method);
                var dialogResult = await dialog.Result;
                if (dialogResult is null || dialogResult.Canceled)
                {
                    return new OperationInvocationResult(OperationOutcome.Canceled, null, null);
                }

                break;
            }
        }

        if (lifetime.IsCancellationRequested)
        {
            return new OperationInvocationResult(OperationOutcome.Canceled, parameterValues, null);
        }

        object? result;
        isRunningChanged?.Invoke(true);
        try
        {
            // Navigating away must not abort an operation that a device has already started. The method is not canceled
            // from here, so an OperationCanceledException is a failure of its own, such as a request that timed out.
            result = await method.InvokeAsync(parameters, serviceProvider, CancellationToken.None);
        }
        catch (Exception exception)
        {
            isRunningChanged?.Invoke(false);
            if (!lifetime.IsCancellationRequested)
            {
                await MethodErrorDialog.ShowAsync(dialogService, method.Title, exception);
            }

            return new OperationInvocationResult(OperationOutcome.Failed, parameterValues, null);
        }

        isRunningChanged?.Invoke(false);
        if (result is not null && !lifetime.IsCancellationRequested)
        {
            await MethodResultDialog.ShowAsync(dialogService, method.Title, result);
        }

        return new OperationInvocationResult(OperationOutcome.Succeeded, parameterValues, result);
    }
}
