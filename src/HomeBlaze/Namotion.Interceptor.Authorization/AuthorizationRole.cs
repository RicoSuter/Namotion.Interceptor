using System.Collections.Immutable;
using Namotion.Interceptor.Authorization.Internal;

namespace Namotion.Interceptor.Authorization;

/// <summary>
/// A named set of allow and deny rules that can include other roles. The effective rules of a role are its own and
/// those of every role it includes, transitively.
/// </summary>
/// <remarks>Two instances are equal when every property is equal element by element; role names compare ignoring case.</remarks>
public sealed record AuthorizationRole
{
    /// <summary>Gets the name, unique among the roles of a policy ignoring case. <see cref="AuthorizationPolicy.SystemRoleName"/> is reserved.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the names of the included roles.</summary>
    public ImmutableArray<string> Includes { get; init; } = [];

    /// <summary>Gets the rules that grant.</summary>
    public ImmutableArray<PermissionRule> Allow { get; init; } = [];

    /// <summary>Gets the rules that remove what any allow rule granted.</summary>
    public ImmutableArray<PermissionRule> Deny { get; init; } = [];

    /// <summary>Whether every property of <paramref name="other"/> is equal element by element, in order, role names ignoring case.</summary>
    public bool Equals(AuthorizationRole? other)
        => ReferenceEquals(this, other) ||
           (other is not null &&
            string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase) &&
            SequenceEquality.AreEqual(Includes, other.Includes, StringComparer.OrdinalIgnoreCase) &&
            SequenceEquality.AreEqual(Allow, other.Allow) &&
            SequenceEquality.AreEqual(Deny, other.Deny));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name, StringComparer.OrdinalIgnoreCase);
        SequenceEquality.AddTo(ref hash, Includes, StringComparer.OrdinalIgnoreCase);
        SequenceEquality.AddTo(ref hash, Allow);
        SequenceEquality.AddTo(ref hash, Deny);
        return hash.ToHashCode();
    }
}
