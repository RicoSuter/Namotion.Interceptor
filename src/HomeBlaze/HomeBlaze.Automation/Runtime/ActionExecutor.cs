using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Runs the operation calls and property writes of rules: resolves the target, refuses what an automation may not do or
/// what the authorizer failed to decide, binds the arguments and executes. A failure is a result with its reason; an
/// exception is thrown only once the caller's token is canceled, which need not be an <see cref="OperationCanceledException"/>.
/// </summary>
internal sealed class ActionExecutor(
    SubjectPathResolver resolver,
    IAutomationAuthorizer authorizer,
    IServiceProvider? serviceProvider,
    TimeProvider timeProvider)
{
    /// <summary>How long an operation may run before its cancellation is requested.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    /// <summary>Creates the executor of the rules of the automation, with its services.</summary>
    public static ActionExecutor For(Automation automation) =>
        new(automation.PathResolver, automation.Authorizer, automation.ServiceProvider, automation.TimeProvider);

    public async Task<ActionResult> InvokeOperationAsync(InvokeOperationAction action, ActionScope scope, CancellationToken cancellationToken)
    {
        if (!TryPrepareOperation(action, scope, cancellationToken, out var operation, out var error))
        {
            return ActionResult.Failure(error);
        }

        using var timeout = new CancellationTokenSource(Timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var abandon = new CancellationTokenSource();
        using var onStop = cancellationToken.Register(() => AbandonWhenBoth(timeout.Token, abandon));
        using var onTimeout = timeout.Token.Register(() => AbandonWhenBoth(cancellationToken, abandon));
        try
        {
            // Awaited also after the timeout, so that an operation that ignores its cancellation never overlaps a later
            // run; only a stop gives up on it, once it timed out, since no later run follows.
            await operation.Method.InvokeAsync(operation.Arguments, serviceProvider, linked.Token).WaitAsync(abandon.Token);
            return ActionResult.Success(operation.Description);
        }
        catch (OperationCanceledException) when (abandon.IsCancellationRequested)
        {
            return ActionResult.Failure($"{operation.Description} did not end within {Timeout.TotalSeconds:0} seconds and was abandoned, since the automation stopped");
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

    // Each token checks the other when it is canceled, so that whichever comes second abandons the operation.
    private static void AbandonWhenBoth(CancellationToken other, CancellationTokenSource abandon)
    {
        if (other.IsCancellationRequested)
        {
            abandon.Cancel();
        }
    }

    /// <summary>Describes the action with its bound arguments without running it, or why it could not run.</summary>
    public string Describe(AutomationAction action, ActionScope scope) => action switch
    {
        InvokeOperationAction invoke => Describe(invoke, scope),
        SetPropertyAction set => Describe(set, scope),
        ChangeStateAction change => DescribeChangeState(change),
        _ => "an action of an unknown type"
    };

    public static string DescribeChangeState(ChangeStateAction change) => $"ChangeState({change.TargetState})";

    private string Describe(InvokeOperationAction action, ActionScope scope) =>
        TryPrepareOperation(action, scope, CancellationToken.None, out var operation, out var error) ? operation.Description : NameTarget(action.OperationPath, error);

    private string Describe(SetPropertyAction action, ActionScope scope) =>
        TryPrepareWrite(action, scope, out var write, out var error) ? write.Description : NameTarget(action.PropertyPath, error);

    // Refusals already start with the path; binding errors name only the argument.
    private static string NameTarget(string path, string error) =>
        error.StartsWith(path, StringComparison.Ordinal) ? error : $"{path}: {error}";

    private bool TryPrepareOperation(
        InvokeOperationAction action,
        ActionScope scope,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out PreparedOperation? operation,
        [NotNullWhen(false)] out string? error)
    {
        operation = null;
        var path = action.OperationPath;
        var property = resolver.ResolveTarget(path, scope.Rule);
        if (property?.TryGetMethodMetadata() is not { } method)
        {
            error = $"No operation at {path}";
            return false;
        }

        error = GetOperationRefusal(path, property, method, scope.Automation, cancellationToken);
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

    private string? GetOperationRefusal(
        string path, RegisteredSubjectProperty property, MethodMetadata method, IInterceptorSubject automation, CancellationToken cancellationToken)
    {
        if (method.Kind != MethodKind.Operation)
        {
            return $"{path} is a query, not an operation";
        }

        if (method.RequiresConfirmation)
        {
            return $"{path} requires confirmation by a person and cannot be called by an automation";
        }

        return property.IsMethodEnabled() ? GetOperationDenial(path, property, automation, cancellationToken) : $"{path} is disabled";
    }

    private string? GetOperationDenial(string path, RegisteredSubjectProperty property, IInterceptorSubject automation, CancellationToken cancellationToken)
    {
        try
        {
            return authorizer.GetOperationDenial(automation, property);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            return DescribeAuthorizationFailure(path, exception);
        }
    }

    private bool TryPrepareWrite(
        SetPropertyAction action, ActionScope scope, [NotNullWhen(true)] out PreparedWrite? write, [NotNullWhen(false)] out string? error)
    {
        write = null;
        var path = action.PropertyPath;
        if (resolver.ResolveTarget(path, scope.Rule) is not { } property)
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
        if (!binder.TryBindProperty(property, action.Value, out var value, out error))
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
            : GetPropertyWriteDenial(path, property, automation);
    }

    private string? GetPropertyWriteDenial(string path, RegisteredSubjectProperty property, IInterceptorSubject automation)
    {
        try
        {
            return authorizer.GetPropertyWriteDenial(automation, property);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return DescribeAuthorizationFailure(path, exception);
        }
    }

    private static string DescribeAuthorizationFailure(string path, Exception exception) => $"{path}: the authorization failed: {exception.Message}";

    private sealed record PreparedOperation(MethodMetadata Method, object?[] Arguments, string Description);

    private sealed record PreparedWrite(RegisteredSubjectProperty Property, object? Value, string Description);
}
