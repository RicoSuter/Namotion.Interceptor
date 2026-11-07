using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Checks the targets of actions while a configuration is validated, with the checks the actions run with: what an
/// automation never does, and what its roles do not allow. A target that does not resolve yet, such as a device that is
/// not loaded or a relative path of a rule that is not saved, is not checkable and passes; it is checked when the saved
/// configuration is applied and when the action runs. While the roles cannot be used, for example before the custom roles
/// are loaded, the roles are not checked here either, and every call and write is refused when it runs.
/// </summary>
internal sealed class ActionTargetCheck
{
    private readonly SubjectPathResolver _resolver;
    private readonly IAutomationPermissions _permissions;
    private readonly Func<string, IInterceptorSubject> _getAnchor;

    /// <param name="resolver">Resolves the paths of the targets.</param>
    /// <param name="permissions">What the roles allow.</param>
    /// <param name="getAnchor">The subject that relative paths of the rule with the key start at.</param>
    public ActionTargetCheck(SubjectPathResolver resolver, IAutomationPermissions permissions, Func<string, IInterceptorSubject> getAnchor)
    {
        _resolver = resolver;
        _permissions = permissions;
        _getAnchor = getAnchor;
    }

    /// <summary>The check of the automation with the roles, whose relative paths start at its saved rules.</summary>
    public static ActionTargetCheck Create(Automation automation, IReadOnlyList<string> roles) => new(
        automation.PathResolver,
        AutomationTargets.GetPermissions(automation, roles),
        key => AutomationPreview.GetAnchor(automation, key));

    /// <summary>Gets why the action cannot run on its target, or null when it can or the target does not resolve yet.</summary>
    public string? Check(string ruleKey, AutomationAction action) => action switch
    {
        InvokeOperationAction invoke => CheckOperation(ruleKey, invoke.OperationPath),
        SetPropertyAction write => CheckWrite(ruleKey, write.PropertyPath),
        _ => null
    };

    private string? CheckOperation(string ruleKey, string path)
    {
        if (Resolve(ruleKey, path) is not { } property)
        {
            return null;
        }

        return property.TryGetMethodMetadata() is { } method
            ? AutomationTargets.CheckOperationNever(path, method) ?? CheckRoles(() => AutomationTargets.GetDenial(path, property, _permissions, isWrite: false))
            : $"No operation at {path}";
    }

    private string? CheckWrite(string ruleKey, string path) =>
        Resolve(ruleKey, path) is { } property
            ? AutomationTargets.CheckWriteNever(path, property) ?? CheckRoles(() => AutomationTargets.GetDenial(path, property, _permissions, isWrite: true))
            : null;

    // The roles are checked only when they can be used; otherwise the run refuses, with the reason.
    private string? CheckRoles(Func<string?> check) => _permissions.Error is null ? check() : null;

    private RegisteredSubjectProperty? Resolve(string ruleKey, string path) =>
        string.IsNullOrWhiteSpace(path) ? null : AutomationPaths.ResolveTarget(_resolver, path, _getAnchor(ruleKey));
}
