namespace HomeBlaze.Abstractions.Authorization;

/// <summary>
/// Specifies authorization requirements at the subject (class) level.
/// Multiple attributes can be applied to define different permissions for different AuthorizationEntity+Action combinations.
/// </summary>
/// <example>
/// [SubjectAuthorize(AuthorizationEntity.State, AuthorizationAction.Read, "Guest")]
/// [SubjectAuthorize(AuthorizationEntity.State, AuthorizationAction.Write, "Operator")]
/// [SubjectAuthorize(AuthorizationEntity.Configuration, AuthorizationAction.Read, "User")]
/// [SubjectAuthorize(AuthorizationEntity.Configuration, AuthorizationAction.Write, "Admin")]
/// public partial class SecurityCamera { }
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public class SubjectAuthorizeAttribute : Attribute
{
    /// <summary>
    /// The entity type (State, Configuration, Query, Operation) this authorization applies to.
    /// </summary>
    public AuthorizationEntity Entity { get; }

    /// <summary>
    /// The action (Read, Write, Invoke) this authorization applies to.
    /// </summary>
    public AuthorizationAction Action { get; }

    /// <summary>
    /// The roles that are authorized to perform this action.
    /// Any of these roles grants access (OR logic).
    /// </summary>
    public string[] Roles { get; }

    /// <summary>
    /// Creates a new subject authorization attribute.
    /// </summary>
    /// <param name="entity">The entity type (State, Configuration, Query, Operation).</param>
    /// <param name="action">The action being authorized.</param>
    /// <param name="roles">One or more roles that grant access.</param>
    public SubjectAuthorizeAttribute(AuthorizationEntity entity, AuthorizationAction action, params string[] roles)
    {
        Entity = entity;
        Action = action;
        Roles = roles;
    }
}
