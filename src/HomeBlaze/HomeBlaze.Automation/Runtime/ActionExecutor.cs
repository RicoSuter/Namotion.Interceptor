using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Runs the operation calls and property writes of rules: resolves the target, refuses what an automation may not do,
/// binds the arguments and executes. A failure is a result with its reason; only a cancellation of the caller's token
/// is thrown.
/// </summary>
internal sealed class ActionExecutor(
    SubjectPathResolver resolver,
    IAutomationAuthorizer authorizer,
    IServiceProvider? serviceProvider,
    TimeProvider timeProvider)
{
    /// <summary>How long an operation may run before its cancellation is requested.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    public async Task<ActionResult> InvokeOperationAsync(InvokeOperationAction action, ActionScope scope, CancellationToken cancellationToken)
    {
        if (!TryPrepareOperation(action, scope, out var operation, out var error))
        {
            return ActionResult.Failure(error);
        }

        using var timeout = new CancellationTokenSource(Timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            // Awaited also after the timeout, so that an operation that ignores its cancellation never overlaps a later run.
            await operation.Method.InvokeAsync(operation.Arguments, serviceProvider, linked.Token);
            return ActionResult.Success(operation.Description);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failure($"{operation.Description} timed out after {Timeout.TotalSeconds:0} seconds");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failure($"{operation.Description} failed: {exception.Message}");
        }
    }

    public ActionResult SetProperty(SetPropertyAction action, ActionScope scope)
    {
        if (!TryPrepareWrite(action, scope, out var write, out var error))
        {
            return ActionResult.Failure(error);
        }

        try
        {
            write.Property.SetValue(write.Value);
            return ActionResult.Success(write.Description);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ActionResult.Failure($"{write.Description} failed: {exception.Message}");
        }
    }

    /// <summary>Describes the call with its bound arguments without running it, or why it could not run.</summary>
    public string Describe(InvokeOperationAction action, ActionScope scope) =>
        TryPrepareOperation(action, scope, out var operation, out var error) ? operation.Description : $"{action.OperationPath}: {error}";

    /// <summary>Describes the write with its bound value without running it, or why it could not run.</summary>
    public string Describe(SetPropertyAction action, ActionScope scope) =>
        TryPrepareWrite(action, scope, out var write, out var error) ? write.Description : $"{action.PropertyPath}: {error}";

    private bool TryPrepareOperation(
        InvokeOperationAction action, ActionScope scope, [NotNullWhen(true)] out PreparedOperation? operation, [NotNullWhen(false)] out string? error)
    {
        operation = null;
        var path = action.OperationPath;
        var property = AutomationPaths.ResolveTarget(resolver, path, scope.Rule);
        if (property?.TryGetMethodMetadata() is not { } method)
        {
            error = $"No operation at {path}";
            return false;
        }

        error = GetOperationRefusal(path, property, method, scope.Automation);
        if (error is not null)
        {
            return false;
        }

        var binder = new ArgumentBinder(scope.Evaluation, scope.AllowsEvent);
        if (!binder.TryBindParameters(method.Parameters, action.Arguments, out var arguments, out error))
        {
            return false;
        }

        operation = new PreparedOperation(method, arguments, $"{path}({binder.Display})");
        return true;
    }

    private string? GetOperationRefusal(string path, RegisteredSubjectProperty property, MethodMetadata method, IInterceptorSubject automation)
    {
        if (method.Kind != MethodKind.Operation)
        {
            return $"{path} is a query, not an operation";
        }

        if (method.RequiresConfirmation)
        {
            return $"{path} requires confirmation by a person and cannot be called by an automation";
        }

        return property.IsMethodEnabled() ? authorizer.GetOperationDenial(automation, property) : $"{path} is disabled";
    }

    private bool TryPrepareWrite(
        SetPropertyAction action, ActionScope scope, [NotNullWhen(true)] out PreparedWrite? write, [NotNullWhen(false)] out string? error)
    {
        write = null;
        var path = action.PropertyPath;
        if (AutomationPaths.ResolveTarget(resolver, path, scope.Rule) is not { } property)
        {
            error = $"No property at {path}";
            return false;
        }

        error = GetWriteRefusal(path, property, scope.Automation);
        if (error is not null)
        {
            return false;
        }

        var binder = new ArgumentBinder(scope.Evaluation, scope.AllowsEvent);
        if (!binder.TryBindValue(property.Name, action.Value, property.Type, out var value, out error))
        {
            return false;
        }

        write = new PreparedWrite(property, value, $"{path} = {RawValueFormatter.Format(value)}");
        return true;
    }

    private string? GetWriteRefusal(string path, RegisteredSubjectProperty property, IInterceptorSubject automation)
    {
        if (property.TryGetMethodMetadata() is not null)
        {
            return $"{path} is an operation; call it with an operation action";
        }

        if (!property.HasPublicSetter)
        {
            return $"{path} is read-only";
        }

        return property.IsConfigurationProperty()
            ? $"{path} is configuration, which an automation cannot change"
            : authorizer.GetPropertyWriteDenial(automation, property);
    }

    private sealed record PreparedOperation(MethodMetadata Method, object?[] Arguments, string Description);

    private sealed record PreparedWrite(RegisteredSubjectProperty Property, object? Value, string Description);
}
