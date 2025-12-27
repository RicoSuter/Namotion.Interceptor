namespace HomeBlaze.Abstractions.Authorization;

/// <summary>
/// Specifies authorization requirements for a specific property.
/// Overrides subject-level authorization for this property.
/// </summary>
/// <example>
/// [Configuration]
/// [SubjectPropertyAuthorize(AuthorizationAction.Read, "Admin")]  // Only admins can read
/// [SubjectPropertyAuthorize(AuthorizationAction.Write, "Admin")] // Only admins can write
/// public partial string ApiKey { get; set; }
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = true)]
public class SubjectPropertyAuthorizeAttribute : Attribute
{
    /// <summary>
    /// The action (Read or Write) this authorization applies to.
    /// </summary>
    public AuthorizationAction Action { get; }

    /// <summary>
    /// The roles that are authorized to perform this action.
    /// Any of these roles grants access (OR logic).
    /// </summary>
    public string[] Roles { get; }

    /// <summary>
    /// Creates a new property authorization attribute.
    /// </summary>
    /// <param name="action">The action being authorized (Read or Write).</param>
    /// <param name="roles">One or more roles that grant access.</param>
    public SubjectPropertyAuthorizeAttribute(AuthorizationAction action, params string[] roles)
    {
        Action = action;
        Roles = roles;
    }
}
