using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Authorization;

/// <summary>
/// The answer of <see cref="AuthorizationPolicy.Explain(RegisteredSubjectProperty, PermissionAction)"/>:
/// whether the action is allowed and which rules decided it.
/// </summary>
public readonly record struct PermissionDecision
{
    internal PermissionDecision(
        bool isAllowed,
        AuthorizationRole? allowedBy,
        PermissionRule? allowedByRule,
        AuthorizationRole? deniedBy,
        PermissionRule? deniedByRule,
        bool isDeniedBySystem)
    {
        IsAllowed = isAllowed;
        AllowedBy = allowedBy;
        AllowedByRule = allowedByRule;
        DeniedBy = deniedBy;
        DeniedByRule = deniedByRule;
        IsDeniedBySystem = isDeniedBySystem;
    }

    /// <summary>Gets whether the action is allowed.</summary>
    public bool IsAllowed { get; }

    /// <summary>Gets the role whose allow rule matched first, or null when none matched.</summary>
    public AuthorizationRole? AllowedBy { get; }

    /// <summary>Gets the allow rule that matched first, or null.</summary>
    public PermissionRule? AllowedByRule { get; }

    /// <summary>Gets the role whose deny rule matched first, or null when none matched or a system deny rule matched first.</summary>
    public AuthorizationRole? DeniedBy { get; }

    /// <summary>Gets the deny rule that matched first, a system deny rule included, or null.</summary>
    public PermissionRule? DeniedByRule { get; }

    /// <summary>Gets whether the deny rule that matched first is a system deny rule.</summary>
    public bool IsDeniedBySystem { get; }
}
