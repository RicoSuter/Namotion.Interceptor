using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Tests.Testing;

/// <summary>Permissions that deny every call and write by naming the member, or that have an error.</summary>
internal sealed class DenyingPermissions(string? error = null) : IAutomationPermissions
{
    public string? Error { get; } = error;

    public string? GetOperationDenial(RegisteredSubjectProperty operation) => $"the automation may not call {operation.Name}";

    public string? GetPropertyWriteDenial(RegisteredSubjectProperty property) => $"the automation may not write {property.Name}";
}

/// <summary>An authorizer that has no permissions to give.</summary>
internal sealed class NullAuthorizer : IAutomationAuthorizer
{
    public IAutomationPermissions GetPermissions(IReadOnlyList<string> roles) => null!;
}

/// <summary>An authorizer that gives the same permissions for any roles and records the roles it was asked with.</summary>
internal sealed class RecordingAuthorizer(IAutomationPermissions permissions) : IAutomationAuthorizer
{
    private readonly List<IReadOnlyList<string>> _requests = [];

    public static RecordingAuthorizer AllowingAll() => new(new AllowAllAutomationAuthorizer().GetPermissions([]));

    /// <summary>The roles of each request so far, in order; the worker of an automation asks from its own thread.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public IAutomationPermissions GetPermissions(IReadOnlyList<string> roles)
    {
        lock (_requests)
        {
            _requests.Add(roles);
        }

        return permissions;
    }
}

/// <summary>An authorizer that gives the permissions of the first role, and no permissions for a role it does not know.</summary>
internal sealed class FirstRoleAuthorizer(IReadOnlyDictionary<string, IAutomationPermissions?> permissions) : IAutomationAuthorizer
{
    public IAutomationPermissions GetPermissions(IReadOnlyList<string> roles) =>
        roles.Count > 0 && permissions.TryGetValue(roles[0], out var found) ? found! : null!;
}
