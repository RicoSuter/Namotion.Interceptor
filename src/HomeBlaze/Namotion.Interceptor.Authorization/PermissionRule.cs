using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Authorization.Internal;

namespace Namotion.Interceptor.Authorization;

/// <summary>
/// Selectors and actions of one rule. The rule applies to an action on a member when the action is listed and every
/// selector that is not empty matches. Whether the rule allows or denies is decided by the list of
/// <see cref="AuthorizationRole"/> it is in.
/// </summary>
/// <remarks>Two instances are equal when every property is equal element by element, tags and actions ignoring case like matching, the other selector values exactly.</remarks>
[JsonConverter(typeof(PermissionRuleJsonConverter))]
public sealed record PermissionRule
{
    /// <summary>
    /// Gets the path patterns, each matching a member's whole path, the path of its subject followed by the member's
    /// segment; empty for everywhere. A pattern for a branch ends in <c>**</c>. The syntax is described in
    /// docs/authorization.md.
    /// </summary>
    /// <remarks>
    /// A subject reachable by several paths matches when any of them does. When the paths of a subject cannot all be
    /// listed (a path cannot be written, the subject has parents but no path to a root, it was detached from the graph,
    /// or it has more than 64 paths or too densely connected parents), an allow rule matches only through the paths that were listed, at most the first
    /// 64, and every deny rule with paths matches.
    /// </remarks>
    public ImmutableArray<string> Paths { get; init; } = [];

    /// <summary>Gets the full or simple names of types or interfaces the member's subject is assignable to; empty for any type.</summary>
    public ImmutableArray<string> Types { get; init; } = [];

    /// <summary>Gets the member name globs, <c>*</c> matching any text; empty for all members. An attribute is named <c>Property@Attribute</c>.</summary>
    public ImmutableArray<string> Members { get; init; } = [];

    /// <summary>Gets the tags a member must all carry, compared ignoring case; empty for any.</summary>
    public ImmutableArray<string> Tags { get; init; } = [];

    /// <summary>Gets the actions the rule applies to; at least one.</summary>
    public ImmutableArray<PermissionAction> Actions { get; init; } = [];

    /// <summary>Whether every property of <paramref name="other"/> is equal element by element, in order.</summary>
    public bool Equals(PermissionRule? other)
        => ReferenceEquals(this, other) ||
           (other is not null &&
            SequenceEquality.AreEqual(Paths, other.Paths, StringComparer.Ordinal) &&
            SequenceEquality.AreEqual(Types, other.Types, StringComparer.Ordinal) &&
            SequenceEquality.AreEqual(Members, other.Members, StringComparer.Ordinal) &&
            SequenceEquality.AreEqual(Tags, other.Tags, StringComparer.OrdinalIgnoreCase) &&
            SequenceEquality.AreEqual(Actions, other.Actions));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        SequenceEquality.AddTo(ref hash, Paths, StringComparer.Ordinal);
        SequenceEquality.AddTo(ref hash, Types, StringComparer.Ordinal);
        SequenceEquality.AddTo(ref hash, Members, StringComparer.Ordinal);
        SequenceEquality.AddTo(ref hash, Tags, StringComparer.OrdinalIgnoreCase);
        SequenceEquality.AddTo(ref hash, Actions);
        return hash.ToHashCode();
    }
}
