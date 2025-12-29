namespace HomeBlaze.Abstractions.Authorization;

/// <summary>
/// Specifies authorization requirements for a specific method.
/// Overrides subject-level authorization for this method.
/// </summary>
/// <example>
/// [Operation]
/// [SubjectMethodAuthorize("Admin")]  // Only admins can invoke
/// public async Task FactoryResetAsync() { }
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public class SubjectMethodAuthorizeAttribute : Attribute
{
    /// <summary>
    /// The roles that are authorized to invoke this method.
    /// Any of these roles grants access (OR logic).
    /// </summary>
    public string[] Roles { get; }

    /// <summary>
    /// Creates a new method authorization attribute.
    /// </summary>
    /// <param name="roles">One or more roles that grant access.</param>
    public SubjectMethodAuthorizeAttribute(params string[] roles)
    {
        Roles = roles;
    }
}
