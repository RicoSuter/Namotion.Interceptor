using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The permissions of the applied roles narrowed by those of the roles of a rejected configuration: an action is allowed
/// only when both allow it, and an error of either refuses every action.
/// </summary>
internal sealed class RejectedRolesPermissions(IAutomationPermissions applied, IAutomationPermissions rejected) : IAutomationPermissions
{
    public string? Error => applied.Error ?? RefusedByRejected(rejected.Error);

    public string? GetOperationDenial(RegisteredSubjectProperty operation) =>
        applied.GetOperationDenial(operation) ?? RefusedByRejected(rejected.GetOperationDenial(operation));

    public string? GetPropertyWriteDenial(RegisteredSubjectProperty property) =>
        applied.GetPropertyWriteDenial(property) ?? RefusedByRejected(rejected.GetPropertyWriteDenial(property));

    private static string? RefusedByRejected(string? denial) =>
        denial is null ? null : $"refused by the roles of the rejected configuration: {denial}";
}
