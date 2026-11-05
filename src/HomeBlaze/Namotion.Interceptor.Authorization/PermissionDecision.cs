using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Authorization;

/// <summary>
/// The answer of <see cref="AuthorizationPolicy.Explain(RegisteredSubjectProperty, PermissionAction)"/>:
/// whether the action is allowed and which rules decided it.
/// </summary>
/// <param name="IsAllowed">Whether the action is allowed.</param>
/// <param name="AllowedBy">The role whose allow rule matched first, or null when none matched.</param>
/// <param name="AllowedByRule">The allow rule that matched first, or null.</param>
/// <param name="DeniedBy">The role whose deny rule matched first, or null when none matched or a system deny rule matched first.</param>
/// <param name="DeniedByRule">The deny rule that matched first, a system deny rule included, or null.</param>
/// <param name="IsDeniedBySystem">Whether the deny rule that matched first is a system deny rule.</param>
public readonly record struct PermissionDecision(
    bool IsAllowed,
    AuthorizationRole? AllowedBy,
    PermissionRule? AllowedByRule,
    AuthorizationRole? DeniedBy,
    PermissionRule? DeniedByRule,
    bool IsDeniedBySystem);
