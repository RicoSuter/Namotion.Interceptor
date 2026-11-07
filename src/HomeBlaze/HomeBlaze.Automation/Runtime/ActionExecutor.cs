using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Runs the operation calls and property writes of rules: resolves the target, refuses what an automation may not do,
/// what its roles do not allow or what the authorizer failed to decide, binds the arguments and executes. A failure is a
/// result with its reason; only a cancellation of the caller's token is thrown.
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
        if (!TryPrepareOperation(action, scope, out var operation, out var error))
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
        TryPrepareOperation(action, scope, out var operation, out var error) ? operation.Description : NameTarget(action.OperationPath, error);

    private string Describe(SetPropertyAction action, ActionScope scope) =>
        TryPrepareWrite(action, scope, out var write, out var error) ? write.Description : NameTarget(action.PropertyPath, error);

    // Refusals already start with the path; binding errors name only the argument.
    private static string NameTarget(string path, string error) =>
        error.StartsWith(path, StringComparison.Ordinal) ? error : $"{path}: {error}";

    private bool TryPrepareOperation(
        InvokeOperationAction action,
        ActionScope scope,
        [NotNullWhen(true)] out PreparedOperation? operation,
        [NotNullWhen(false)] out string? error)
    {
        operation = null;
        var path = action.OperationPath;
        var property = AutomationPaths.ResolveTarget(resolver, path, scope.Rule);
        if (property?.TryGetMethodMetadata() is not { } method)
        {
            error = $"No operation at {path}";
            return false;
        }

        // Refused before the authorizer is asked: what an automation never does, and a disabled operation.
        error = AutomationTargets.CheckOperationNever(path, method) ??
            (property.IsMethodEnabled() ? null : $"{path} is disabled") ??
            AutomationTargets.CheckOperation(path, property, method, GetPermissions(scope));
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

        error = AutomationTargets.CheckWriteNever(path, property) ?? AutomationTargets.CheckWrite(path, property, GetPermissions(scope));
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

    // Asked for every action, so that a changed custom role applies to the next action of a run.
    private IAutomationPermissions GetPermissions(ActionScope scope) => AutomationTargets.GetPermissions(authorizer, scope.Roles);

    private sealed record PreparedOperation(MethodMetadata Method, object?[] Arguments, string Description);

    private sealed record PreparedWrite(RegisteredSubjectProperty Property, object? Value, string Description);
}
