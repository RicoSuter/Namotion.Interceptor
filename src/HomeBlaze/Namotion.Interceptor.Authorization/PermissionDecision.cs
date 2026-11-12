using Namotion.Interceptor.Authorization.Internal;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Authorization;

/// <summary>
/// The answer of <see cref="AuthorizationPolicy.Explain(RegisteredSubjectProperty, PermissionAction)"/>:
/// whether the action is allowed and which rules decided it.
/// </summary>
public readonly record struct PermissionDecision
{
    internal PermissionDecision(CompiledRule? allowed, CompiledRule? denied)
    {
        AllowedBy = allowed?.Role;
        AllowedByRule = allowed?.Rule;
        DeniedBy = denied?.Role;
        DeniedByRule = denied?.Rule;
    }

    /// <summary>Gets whether the action is allowed.</summary>
    public bool IsAllowed => AllowedByRule is not null && DeniedByRule is null;

    /// <summary>Gets the role whose allow rule matched first, or null when none matched.</summary>
    public AuthorizationRole? AllowedBy { get; }

    /// <summary>Gets the allow rule that matched first, or null.</summary>
    public PermissionRule? AllowedByRule { get; }

    /// <summary>Gets the role whose deny rule matched first, or null when none matched or a system deny rule matched first.</summary>
    public AuthorizationRole? DeniedBy { get; }

    /// <summary>Gets the deny rule that matched first, a system deny rule included, or null.</summary>
    public PermissionRule? DeniedByRule { get; }

    /// <summary>Gets whether the deny rule that matched first is a system deny rule.</summary>
    public bool IsDeniedBySystem => DeniedByRule is not null && DeniedBy is null;
}
