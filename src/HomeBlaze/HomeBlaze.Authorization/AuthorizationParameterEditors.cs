namespace HomeBlaze.Authorization;

/// <summary>
/// The names of the editors of operation parameters that the authorization uses, see
/// <see cref="HomeBlaze.Abstractions.Attributes.OperationParameterAttribute.Editor"/>.
/// </summary>
public static class AuthorizationParameterEditors
{
    /// <summary>
    /// A list of role names, offered as the roles the person may assign; the user store still checks the roles on save.
    /// </summary>
    public const string RoleNames = "HomeBlaze.Authorization.RoleNames";
}
