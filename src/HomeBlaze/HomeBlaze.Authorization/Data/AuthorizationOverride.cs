namespace HomeBlaze.Authorization.Data;

/// <summary>
/// Represents a permission override for a subject or property.
/// </summary>
public class AuthorizationOverride
{
    /// <summary>
    /// If true, the specified roles are added to inherited roles (extend).
    /// If false, the specified roles replace inherited roles (override).
    /// </summary>
    public bool Inherit { get; set; }

    /// <summary>
    /// The roles that can access this resource.
    /// </summary>
    public string[] Roles { get; set; } = [];
}
