using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Components.Abstractions.Access;
using HomeBlaze.Authorization.Abstractions;
using MudBlazor;
using Namotion.Interceptor.Registry.Abstractions;

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

    // The values of the caller, completed with the readable properties the parameters name as the person is shown them,
    // converted to the parameter type.
    private static IReadOnlyDictionary<string, object?>? GetInitialValues(
        MethodMetadata method, RegisteredSubjectProperty member, IUserPermissions permissions, IReadOnlyDictionary<string, object?>? initialValues)
    {
        Dictionary<string, object?>? values = null;
        foreach (var parameter in method.Parameters)
        {
            if (parameter.InitialValueProperty is not { } propertyName ||
                initialValues?.ContainsKey(parameter.Name) == true ||
                member.Parent.TryGetProperty(propertyName) is not { } property ||
                !permissions.CanRead(property) ||
                !TryConvert(permissions.GetValue(property), parameter.Type, out var value))
            {
                continue;
            }

            values ??= initialValues is null ? [] : new Dictionary<string, object?>(initialValues);
            values[parameter.Name] = value;
        }

        return values ?? initialValues;
    }

    // An array parameter takes the items of any collection, such as a list of names for a string[].
    private static bool TryConvert(object? value, Type type, out object? converted)
    {
        if (value is null || type.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }

        if (type.IsArray && value is System.Collections.ICollection collection)
        {
            var array = Array.CreateInstanceFromArrayType(type, collection.Count);
            try
            {
                collection.CopyTo(array, 0);
            }
            catch (Exception exception) when (exception is ArrayTypeMismatchException or InvalidCastException)
            {
                converted = null;
                return false;
            }

            converted = array;
            return true;
        }

        converted = null;
        return false;
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
    /// dialog for a result that is not null, the <see cref="OneTimeSecretDialog"/> for a <see cref="OneTimeSecret"/>, which the
    /// returned outcome always leaves out, or the error dialog for an exception, an <see cref="OperationCanceledException"/>
    /// included. Never throws for a failing method.
    /// </summary>
    /// <remarks>
    /// Exceptions thrown by the dialog service itself propagate to the caller.
    /// </remarks>
    /// <param name="dialogService">The dialog service of the caller.</param>
    /// <param name="serviceProvider">Resolves the parameters of the method that come from services.</param>
    /// <param name="permissions">
    /// The person who runs the method. When <see cref="IUserPermissions.CanInvoke"/> refuses <paramref name="member"/> before
    /// the prompt or right before the method starts (through <see cref="UserPermissionsExtensions"/>), the method does not start, a
    /// message box titled "Not allowed" says so and <see cref="OperationOutcome.Denied"/> is returned. What the method
    /// itself writes is not checked.
    /// </param>
    /// <param name="member">
    /// The member that holds the operation or query; refused as not allowed when it holds none, or one excluded from user
    /// interfaces.
    /// </param>
    /// <param name="lifetime">
    /// Canceled when the caller goes away. From then on no dialog is shown and a method that has not started is
    /// not started. A started method is not canceled.
    /// </param>
    /// <param name="isRunningChanged">
    /// Called with true right before the last check and the start of the method, and with false once the method has
    /// returned or thrown or the last check refused it, before a result, error or refusal dialog shows. Not called when
    /// the method is refused or canceled before or during the prompt.
    /// </param>
    /// <param name="initialValues">
    /// The values that prefill the parameter dialog, keyed by parameter name; only parameters entered in one field use them.
    /// A parameter without one takes the value of its <see cref="MethodParameter.InitialValueProperty"/> on the subject of
    /// <paramref name="member"/> when <paramref name="permissions"/> may read it.
    /// </param>
    public static async Task<OperationInvocationResult> InvokeAsync(
        IDialogService dialogService,
        IServiceProvider serviceProvider,
        IUserPermissions permissions,
        RegisteredSubjectProperty member,
        CancellationToken lifetime,
        Action<bool>? isRunningChanged = null,
        IReadOnlyDictionary<string, object?>? initialValues = null)
    {
        if (lifetime.IsCancellationRequested)
        {
            return new OperationInvocationResult(OperationOutcome.Canceled, null, null);
        }

        // A method excluded from user interfaces is meant for agents only, such as one that skips the checks of the UI, so
        // a button configured with its path does not run it either.
        if (member.GetValue() is not MethodMetadata method || method.IsExcludedFrom(ExclusionTarget.UserInterface) || !permissions.CanInvoke(member))
        {
            return await DenyAsync(dialogService, null);
        }

        object?[]? parameters = null;
        IReadOnlyDictionary<string, object?>? parameterValues = null;
        switch (GetPrompt(method))
        {
            case OperationPrompt.Parameters:
            {
                var dialog = await MethodParameterDialog.ShowAsync(dialogService, method, GetInitialValues(method, member, permissions, initialValues));
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

        // The gate checks again right before the method starts: the roles may have changed while a prompt was open.
        // Navigating away must not abort an operation that a device has already started. The method is not canceled
        // from here, so an OperationCanceledException is a failure of its own, such as a request that timed out.
        isRunningChanged?.Invoke(true);
        var invocation = await permissions.TryInvokeAsync(member, parameters, serviceProvider, CancellationToken.None);
        isRunningChanged?.Invoke(false);

        switch (invocation.Outcome)
        {
            case PermittedOperationOutcome.Denied:
                return await DenyAsync(dialogService, parameterValues);
            case PermittedOperationOutcome.Failed:
                if (!lifetime.IsCancellationRequested)
                {
                    await MethodErrorDialog.ShowAsync(dialogService, method.Title, invocation.Exception!);
                }

                return new OperationInvocationResult(OperationOutcome.Failed, parameterValues, null);
            default:
                if (invocation.Result is OneTimeSecret secret)
                {
                    // Shown once, in a dialog that keeps it nowhere else, and never carried on by the outcome, also when
                    // the caller went away and nobody sees it.
                    if (!lifetime.IsCancellationRequested)
                    {
                        await OneTimeSecretDialog.ShowAsync(dialogService, secret);
                    }

                    return new OperationInvocationResult(OperationOutcome.Succeeded, parameterValues, null);
                }

                if (invocation.Result is not null && !lifetime.IsCancellationRequested)
                {
                    await MethodResultDialog.ShowAsync(dialogService, method.Title, invocation.Result);
                }

                return new OperationInvocationResult(OperationOutcome.Succeeded, parameterValues, invocation.Result);
        }
    }

    private static async Task<OperationInvocationResult> DenyAsync(
        IDialogService dialogService, IReadOnlyDictionary<string, object?>? parameterValues)
    {
        await dialogService.ShowMessageBoxAsync(
            new MessageBoxOptions { Title = AccessDeniedTexts.Title, Message = AccessDeniedTexts.Invoke, YesText = "OK" });
        return new OperationInvocationResult(OperationOutcome.Denied, parameterValues, null);
    }
}
