namespace HomeBlaze.Automation;

/// <summary>
/// Decides what an automation may do as the principal it runs as: which operations it may call and which properties it
/// may write. Asked when a configuration is validated and before every call and every write. An operation that requires
/// confirmation, a query, a read-only property and configuration are refused before the authorizer is asked.
/// </summary>
public interface IAutomationAuthorizer
{
    /// <summary>Gets what an automation with the roles may do now.</summary>
    /// <param name="roles">The role names of the automation, see <see cref="Automation.Roles"/>.</param>
    IAutomationPermissions GetPermissions(IReadOnlyList<string> roles);
}
