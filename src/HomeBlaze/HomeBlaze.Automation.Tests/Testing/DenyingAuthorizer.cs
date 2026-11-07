namespace HomeBlaze.Automation.Tests.Testing;

/// <summary>An authorizer that denies every operation call and property write, naming the member.</summary>
internal sealed class DenyingAuthorizer : IAutomationAuthorizer
{
    public IAutomationPermissions GetPermissions(IReadOnlyList<string> roles) => new DenyingPermissions();
}
