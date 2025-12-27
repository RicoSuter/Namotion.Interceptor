namespace HomeBlaze.Authorization.Data;

/// <summary>
/// Defines role hierarchy relationships.
/// A role can include other roles, inheriting their permissions.
/// </summary>
public class RoleComposition
{
    /// <summary>
    /// Primary key.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The role that includes another role.
    /// </summary>
    public string RoleName { get; set; } = "";

    /// <summary>
    /// The role that is included (inherited).
    /// </summary>
    public string IncludesRole { get; set; } = "";
}
