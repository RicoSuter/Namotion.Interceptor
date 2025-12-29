namespace HomeBlaze.Authorization.Roles;

/// <summary>
/// Default role names used in the authorization system.
/// Roles form a hierarchy: Anonymous -> Guest -> User -> Operator -> Supervisor -> Admin.
/// Higher roles inherit all permissions from lower roles.
/// </summary>
public static class DefaultRoles // TODO: Move to abstractions?
{
    /// <summary>
    /// Unauthenticated users. Lowest privilege level.
    /// </summary>
    public const string Anonymous = "Anonymous";

    /// <summary>
    /// Authenticated users without specific roles. Can view state properties.
    /// </summary>
    public const string Guest = "Guest";

    /// <summary>
    /// Standard users. Can view configuration and invoke queries.
    /// </summary>
    public const string User = "User";

    /// <summary>
    /// Operators. Can modify state and invoke operations.
    /// </summary>
    public const string Operator = "Operator";

    /// <summary>
    /// Supervisors. Can modify configuration.
    /// </summary>
    public const string Supervisor = "Supervisor";

    /// <summary>
    /// Administrators. Full access to all features including user management.
    /// </summary>
    public const string Admin = "Admin";

    /// <summary>
    /// All system roles in order from highest to lowest privilege.
    /// </summary>
    public static readonly string[] AllRoles = [Admin, Supervisor, Operator, User, Guest, Anonymous];
}
