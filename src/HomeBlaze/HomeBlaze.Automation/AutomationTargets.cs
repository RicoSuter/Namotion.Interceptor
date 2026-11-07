using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation;

/// <summary>
/// Decides which operations an automation may call and which properties it may write: what an automation never does, and
/// what its roles do not allow. The automation applies the same checks when it validates a configuration and before each
/// action, and the editor offers only the targets they allow.
/// </summary>
public static class AutomationTargets
{
    /// <summary>
    /// Gets what an automation with the roles may do now, from its authorizer; when the authorizer fails, permissions that
    /// refuse everything with the failure.
    /// </summary>
    /// <param name="automation">The automation whose authorizer decides.</param>
    /// <param name="roles">The roles, such as those of <see cref="Automation.Roles"/> or of an unsaved edit.</param>
    public static IAutomationPermissions GetPermissions(Automation automation, IReadOnlyList<string> roles)
    {
        ArgumentNullException.ThrowIfNull(automation);
        return GetPermissions(automation.Authorizer, roles);
    }

    /// <summary>Gets whether an automation with the permissions may call the operation that the property holds.</summary>
    public static bool CanCall(RegisteredSubjectProperty property, IAutomationPermissions permissions) =>
        property.TryGetMethodMetadata() is { } method && CheckOperation(property.Name, property, method, permissions) is null;

    /// <summary>Gets whether an automation with the permissions may write the property.</summary>
    public static bool CanWrite(RegisteredSubjectProperty property, IAutomationPermissions permissions) =>
        CheckWrite(property.Name, property, permissions) is null;

    /// <summary>
    /// Gets why every action of the rules that calls an operation or writes a property is refused, or null when the
    /// permissions have no <see cref="IAutomationPermissions.Error"/> or no rule calls or writes anything.
    /// </summary>
    public static string? GetRolesError(IAutomationPermissions permissions, IEnumerable<AutomationRule?> rules) =>
        permissions.Error is { } error && rules.Any(CallsOrWrites)
            ? $"The actions that call operations or write properties are refused: {error}"
            : null;

    internal static IAutomationPermissions GetPermissions(IAutomationAuthorizer authorizer, IReadOnlyList<string> roles)
    {
        try
        {
            return authorizer.GetPermissions(roles) ?? new FailedPermissions("the authorization failed: the authorizer returned no permissions");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new FailedPermissions($"the authorization failed: {exception.Message}");
        }
    }

    /// <summary>Gets why an automation never calls the operation, starting with the path, or null; neither roles nor its enabled state are checked.</summary>
    internal static string? CheckOperationNever(string path, MethodMetadata method)
    {
        if (method.Kind != MethodKind.Operation)
        {
            return $"{path} is a query, not an operation";
        }

        return method.RequiresConfirmation ? $"{path} requires confirmation by a person and cannot be called by an automation" : null;
    }

    /// <summary>Gets why an automation never writes the property, starting with the path, or null; roles are not checked.</summary>
    internal static string? CheckWriteNever(string path, RegisteredSubjectProperty property)
    {
        if (property.TryGetMethodMetadata() is not null)
        {
            return $"{path} is an operation; call it with an operation action";
        }

        if (!property.HasPublicSetter)
        {
            return $"{path} is read-only";
        }

        return property.IsConfigurationProperty() ? $"{path} is configuration, which an automation cannot change" : null;
    }

    /// <summary>Gets why the operation cannot be called, starting with the path, or null; its enabled state is not checked.</summary>
    internal static string? CheckOperation(string path, RegisteredSubjectProperty property, MethodMetadata method, IAutomationPermissions permissions) =>
        CheckOperationNever(path, method) ?? GetDenial(path, property, permissions, isWrite: false);

    /// <summary>Gets why the property cannot be written, starting with the path, or null.</summary>
    internal static string? CheckWrite(string path, RegisteredSubjectProperty property, IAutomationPermissions permissions) =>
        CheckWriteNever(path, property) ?? GetDenial(path, property, permissions, isWrite: true);

    private static bool CallsOrWrites(AutomationRule? rule) =>
        rule?.Actions.Any(action => action is InvokeOperationAction or SetPropertyAction) == true;

    private static string? GetDenial(string path, RegisteredSubjectProperty property, IAutomationPermissions permissions, bool isWrite)
    {
        try
        {
            var denial = permissions.Error ?? (isWrite ? permissions.GetPropertyWriteDenial(property) : permissions.GetOperationDenial(property));
            return denial is null ? null : $"{path}: {denial}";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return $"{path}: the authorization failed: {exception.Message}";
        }
    }

    private sealed class FailedPermissions(string error) : IAutomationPermissions
    {
        public string? Error { get; } = error;

        public string? GetOperationDenial(RegisteredSubjectProperty operation) => Error;

        public string? GetPropertyWriteDenial(RegisteredSubjectProperty property) => Error;
    }
}
