using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation;

/// <summary>
/// Allows every operation call and property write whatever the roles, apart from what an automation never does; for hosts
/// without roles and for tests, registered before <see cref="AutomationServiceCollectionExtensions.AddHomeBlazeAutomation"/>.
/// </summary>
public sealed class AllowAllAutomationAuthorizer : IAutomationAuthorizer
{
    public IAutomationPermissions GetPermissions(IReadOnlyList<string> roles) => AllowAll.Instance;

    private sealed class AllowAll : IAutomationPermissions
    {
        public static readonly AllowAll Instance = new();

        public string? Error => null;

        public string? GetOperationDenial(RegisteredSubjectProperty operation) => null;

        public string? GetPropertyWriteDenial(RegisteredSubjectProperty property) => null;
    }
}
