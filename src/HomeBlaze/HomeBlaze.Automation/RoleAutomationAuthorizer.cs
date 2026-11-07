using HomeBlaze.Authorization;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation;

/// <summary>
/// Decides with the roles of an automation through the <see cref="AuthorizationService"/>: a call needs <c>execute</c> on
/// the operation and a write needs <c>write</c> on the property, with the system rules on top. Roles that cannot be used,
/// an empty list included, allow nothing.
/// </summary>
public sealed class RoleAutomationAuthorizer(AuthorizationService authorizationService) : IAutomationAuthorizer
{
    public IAutomationPermissions GetPermissions(IReadOnlyList<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var result = authorizationService.GetPolicy(roles);
        return new RolePermissions(result.Policy, result.Error is { } error ? $"the roles of the automation cannot be used: {error}" : null);
    }

    private sealed class RolePermissions(AuthorizationPolicy policy, string? error) : IAutomationPermissions
    {
        public string? Error { get; } = error;

        public string? GetOperationDenial(RegisteredSubjectProperty operation) => GetDenial(operation, AuthorizationActions.Execute, "calling");

        public string? GetPropertyWriteDenial(RegisteredSubjectProperty property) => GetDenial(property, PermissionAction.Write, "writing");

        // Explain is asked only for a denial, as it is meant for reports and costs more than IsAllowed.
        private string? GetDenial(RegisteredSubjectProperty member, PermissionAction action, string verb)
        {
            if (Error is not null)
            {
                return Error;
            }

            if (policy.IsAllowed(member, action))
            {
                return null;
            }

            var deniedBy = policy.Explain(member, action).DeniedBy?.Name;
            if (deniedBy is null)
            {
                return $"no role of the automation allows {verb} it";
            }

            return string.Equals(deniedBy, AuthorizationPolicy.SystemRoleName, StringComparison.OrdinalIgnoreCase)
                ? $"the system rules deny {verb} it"
                : $"the role '{deniedBy}' denies {verb} it";
        }
    }
}
