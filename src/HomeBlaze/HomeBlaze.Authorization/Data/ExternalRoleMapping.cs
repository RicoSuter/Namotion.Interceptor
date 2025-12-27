namespace HomeBlaze.Authorization.Data;

/// <summary>
/// Maps roles from external OAuth/OIDC providers to internal roles.
/// </summary>
public class ExternalRoleMapping
{
    /// <summary>
    /// Primary key.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The OAuth/OIDC provider name (e.g., "Google", "Microsoft").
    /// </summary>
    public string Provider { get; set; } = "";

    /// <summary>
    /// The role name/claim from the external provider.
    /// </summary>
    public string ExternalRole { get; set; } = "";

    /// <summary>
    /// The internal HomeBlaze role to assign.
    /// </summary>
    public string InternalRole { get; set; } = "";
}
